using System.Text.Json;
using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Core.Cards;

public sealed record DeckResolveResult(IReadOnlyDictionary<Guid, CardInfo> Cards, IReadOnlyList<DeckCard> NotFound);

/// <summary>
/// Card lookups for the app: Scryfall plus a cache kept on the device, so opening a deck
/// doesn't refetch every card. Cached cards expire after a few days so ban-list and price changes show up.
/// </summary>
public sealed class CardCatalog(ScryfallClient scryfall, IKeyValueStore store, TimeProvider? clock = null)
{
    private const string CacheKey = "mtg-companion.card-cache";
    private const int MaxCachedCards = 2000;
    private const int MaxFuzzyLookups = 25;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(3);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Dictionary<Guid, CacheEntry>? _cache;

    public Task<IReadOnlyList<string>> AutocompleteAsync(string partialName, CancellationToken ct = default) =>
        scryfall.AutocompleteAsync(partialName, ct);

    public async Task<CardSearchPage> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        var result = await scryfall.SearchAsync(query, page, ct);
        await RememberAsync(result.Cards);
        return result;
    }

    public async Task<CardInfo?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var cache = await LoadAsync();
        var hit = cache.Values.FirstOrDefault(e => IsFresh(e)
            && (string.Equals(e.Card.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Card.FrontName, name, StringComparison.OrdinalIgnoreCase)));
        if (hit is not null) return hit.Card;

        var card = await scryfall.FindByNameAsync(name, ct);
        if (card is not null) await RememberAsync([card]);
        return card;
    }

    public async Task<CardInfo?> GetByPrintingAsync(string set, string collectorNumber, CancellationToken ct = default)
    {
        var cache = await LoadAsync();
        var hit = cache.Values.FirstOrDefault(e => IsFresh(e)
            && string.Equals(e.Card.SetCode, set, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Card.CollectorNumber, collectorNumber, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit.Card;

        var card = await scryfall.GetByPrintingAsync(set, collectorNumber, ct);
        if (card is not null) await RememberAsync([card]);
        return card;
    }

    public async Task<CardInfo?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var cache = await LoadAsync();
        if (cache.TryGetValue(id, out var entry) && IsFresh(entry)) return entry.Card;
        var card = await scryfall.GetByIdAsync(id, ct);
        if (card is not null) await RememberAsync([card]);
        return card;
    }

    /// <summary>
    /// Looks up every card in a deck. Lines that match get their Scryfall id, canonical name and
    /// printing filled in; lines that don't are returned in <see cref="DeckResolveResult.NotFound"/>.
    /// </summary>
    public async Task<DeckResolveResult> ResolveAsync(Deck deck, CancellationToken ct = default)
    {
        var cache = await LoadAsync();
        var found = new Dictionary<Guid, CardInfo>();
        var pending = new List<DeckCard>();

        foreach (var line in deck.Cards)
        {
            if (line.ScryfallId is { } id && cache.TryGetValue(id, out var entry) && IsFresh(entry))
                found[id] = entry.Card;
            else
                pending.Add(line);
        }

        if (pending.Count > 0)
        {
            // Pass 1: by id, else exact printing, else name.
            var firstPass = await scryfall.GetCollectionAsync(pending.Select(IdentifierFor).ToList(), ct);
            var missing = new List<DeckCard>();
            for (var i = 0; i < pending.Count; i++)
            {
                if (firstPass[i].Card is { } card) Apply(pending[i], card, found);
                else missing.Add(pending[i]);
            }

            // Pass 2: an unknown printing or id might still be a known card name.
            var byName = missing.Where(l => l.ScryfallId is not null || l.SetCode is not null).ToList();
            if (byName.Count > 0)
            {
                var secondPass = await scryfall.GetCollectionAsync(byName.Select(l => CardIdentifier.ByName(l.Name)).ToList(), ct);
                for (var i = 0; i < byName.Count; i++)
                    if (secondPass[i].Card is { } card) { Apply(byName[i], card, found); missing.Remove(byName[i]); }
            }

            // Pass 3: fuzzy matching catches front-face-only names and small typos, one card at a time.
            foreach (var line in missing.Take(MaxFuzzyLookups).ToList())
            {
                if (await scryfall.FindByNameAsync(line.Name, ct) is { } card) { Apply(line, card, found); missing.Remove(line); }
            }

            await RememberAsync(found.Values);
            return new(found, missing);
        }

        return new(found, []);
    }

    /// <summary>Forgets every cached card, e.g. to pick up a ban-list change right away.</summary>
    public async Task ClearCacheAsync()
    {
        _cache = [];
        await store.RemoveAsync(CacheKey);
    }

    private static CardIdentifier IdentifierFor(DeckCard line) =>
        line.ScryfallId is { } id ? CardIdentifier.ById(id)
        : line.SetCode is { } set && line.CollectorNumber is { } number ? CardIdentifier.ByPrinting(set, number)
        : CardIdentifier.ByName(line.Name);

    private static void Apply(DeckCard line, CardInfo card, Dictionary<Guid, CardInfo> found)
    {
        line.ScryfallId = card.Id;
        line.Name = card.Name;
        line.SetCode = card.SetCode;
        line.CollectorNumber = card.CollectorNumber;
        found[card.Id] = card;
    }

    // Entries without prices were cached by an older version of the app, so they're refetched.
    private bool IsFresh(CacheEntry entry) =>
        entry.Card.Prices is not null && _clock.GetUtcNow() - entry.FetchedAt < CacheLifetime;

    private async Task<Dictionary<Guid, CacheEntry>> LoadAsync()
    {
        if (_cache is not null) return _cache;
        _cache = [];
        var json = await store.GetAsync(CacheKey);
        if (json is null) return _cache;
        try
        {
            foreach (var entry in JsonSerializer.Deserialize<List<CacheEntry>>(json) ?? [])
                if (IsFresh(entry)) _cache[entry.Card.Id] = entry;
        }
        catch (JsonException)
        {
            // Corrupt or old-format cache: start over.
        }
        return _cache;
    }

    private async Task RememberAsync(IEnumerable<CardInfo> cards)
    {
        var cache = await LoadAsync();
        var now = _clock.GetUtcNow();
        var changed = false;
        foreach (var card in cards)
        {
            cache[card.Id] = new CacheEntry(card, now);
            changed = true;
        }
        if (!changed) return;

        if (cache.Count > MaxCachedCards)
        {
            foreach (var old in cache.Values.OrderBy(e => e.FetchedAt).Take(cache.Count - MaxCachedCards).ToList())
                cache.Remove(old.Card.Id);
        }
        await store.SetAsync(CacheKey, JsonSerializer.Serialize(cache.Values.ToList()));
    }

    private sealed record CacheEntry(CardInfo Card, DateTimeOffset FetchedAt);
}
