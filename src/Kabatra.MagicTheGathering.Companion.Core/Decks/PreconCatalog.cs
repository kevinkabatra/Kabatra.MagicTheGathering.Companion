using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

/// <summary>An official prebuilt deck (a "precon") as listed by MTGJSON.</summary>
/// <param name="FileName">MTGJSON's id for the deck, used to fetch its card list.</param>
/// <param name="Type">The kind of product, e.g. "Commander Deck", "Duel Deck" or "Theme Deck".</param>
public sealed record PreconSummary(string FileName, string Name, string SetCode, string Type, DateOnly? ReleaseDate);

/// <summary>
/// Official prebuilt decks from MTGJSON (https://mtgjson.com), a free, open data set whose files can be
/// fetched straight from the browser. The list of decks is cached on the device for a week; each deck's
/// card list is fetched when it's imported. Cards come with their Scryfall ids, so the exact printings
/// from the box can be looked up.
/// </summary>
public sealed class PreconCatalog(HttpClient http, IKeyValueStore store, TimeProvider? clock = null)
{
    public static readonly Uri DefaultBaseAddress = new("https://mtgjson.com/api/v5/");
    public static readonly TimeSpan ListLifetime = TimeSpan.FromDays(7);

    /// <summary>The product types most people mean by "precon", shown first in the picker.</summary>
    public static readonly IReadOnlyList<string> CommonTypes =
        ["Commander Deck", "Duel Deck", "Challenger Deck", "Starter Kit", "Brawl Deck", "Planeswalker Deck", "Theme Deck"];

    private const string CacheKey = "mtg-companion.precon-list";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private IReadOnlyList<PreconSummary>? _list;

    /// <summary>Every prebuilt deck, newest first.</summary>
    public async Task<IReadOnlyList<PreconSummary>> ListAsync(CancellationToken ct = default)
    {
        if (_list is not null) return _list;

        var cached = await LoadCachedAsync();
        if (cached is not null && _clock.GetUtcNow() - cached.FetchedAt < ListLifetime) return _list = cached.Decks;

        try
        {
            var body = await GetAsync<DeckListDto>("DeckList.json", ct)
                ?? throw new PreconException("MTGJSON's deck list is missing.");
            var decks = body.Data
                .Where(d => !string.IsNullOrWhiteSpace(d.FileName) && !string.IsNullOrWhiteSpace(d.Name))
                .Select(d => new PreconSummary(d.FileName!, d.Name!, d.Code ?? "", d.Type ?? "",
                    DateOnly.TryParse(d.ReleaseDate, out var date) ? date : null))
                .OrderByDescending(d => d.ReleaseDate)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            await store.SetAsync(CacheKey, JsonSerializer.Serialize(new CachedList(decks, _clock.GetUtcNow()), Json));
            return _list = decks;
        }
        catch (Exception e) when (cached is not null && e is HttpRequestException or PreconException or JsonException)
        {
            return _list = cached.Decks; // offline or MTGJSON is down: an old list beats none
        }
    }

    /// <summary>
    /// Fetches a prebuilt deck's card list as a new, unsaved <see cref="Deck"/>. Lines carry Scryfall ids;
    /// run the deck through <c>CardCatalog.ResolveAsync</c> to fill in the card details.
    /// </summary>
    public async Task<Deck> GetDeckAsync(PreconSummary precon, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(precon);
        var body = await GetAsync<DeckFileDto>($"decks/{Uri.EscapeDataString(precon.FileName)}.json", ct)
            ?? throw new PreconException($"MTGJSON has no deck called {precon.Name}.");
        return ToDeck(body.Data, precon.Type);
    }

    internal static Deck ToDeck(DeckDto dto, string type)
    {
        var deck = new Deck { Name = dto.Name ?? "Prebuilt deck" };
        AddSection(deck, dto.Commander, DeckSection.Commander);
        AddSection(deck, dto.MainBoard, DeckSection.Main);
        AddSection(deck, dto.SideBoard, DeckSection.Sideboard);
        deck.Format = FormatFor(type, deck.Commanders.Any()).Id;
        return deck;
    }

    /// <summary>The format a prebuilt deck is made for. Most non-commander precons aren't legal anywhere as-is.</summary>
    public static DeckFormat FormatFor(string type, bool hasCommander)
    {
        if (type.Contains("Historic Brawl", StringComparison.OrdinalIgnoreCase)) return DeckFormat.HistoricBrawl;
        if (type.Contains("Brawl", StringComparison.OrdinalIgnoreCase)) return DeckFormat.Brawl;
        return hasCommander ? DeckFormat.Commander : DeckFormat.Casual;
    }

    private static void AddSection(Deck deck, List<CardDto>? cards, DeckSection section)
    {
        foreach (var card in cards ?? [])
        {
            if (string.IsNullOrWhiteSpace(card.Name) || card.Count < 1) continue;
            Guid? id = Guid.TryParse(card.Identifiers?.ScryfallId, out var parsed) ? parsed : null;

            // Foil and non-foil copies of a card are listed separately; one deck line is enough.
            var existing = deck.Cards.FirstOrDefault(c => c.Section == section
                && (id is not null ? c.ScryfallId == id : c.ScryfallId is null && string.Equals(c.Name, card.Name, StringComparison.OrdinalIgnoreCase)));
            if (existing is not null)
            {
                existing.Quantity += card.Count;
                continue;
            }

            deck.Cards.Add(new DeckCard
            {
                Name = card.Name,
                Quantity = card.Count,
                Section = section,
                ScryfallId = id,
                SetCode = string.IsNullOrWhiteSpace(card.SetCode) ? null : card.SetCode,
                CollectorNumber = string.IsNullOrWhiteSpace(card.Number) ? null : card.Number,
            });
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var response = await http.GetAsync(new Uri(http.BaseAddress ?? DefaultBaseAddress, path), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new PreconException($"MTGJSON returned {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private async Task<CachedList?> LoadCachedAsync()
    {
        var json = await store.GetAsync(CacheKey);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<CachedList>(json, Json) is { Decks.Count: > 0 } list ? list : null; }
        catch (JsonException) { return null; }
    }

    private sealed record CachedList(List<PreconSummary> Decks, DateTimeOffset FetchedAt);

    // ----- MTGJSON shapes (only the fields we use; deck files are large, the rest is skipped) -----

    private sealed record DeckListDto(List<DeckListEntryDto> Data);
    private sealed record DeckListEntryDto(string? Code, string? FileName, string? Name, string? ReleaseDate, string? Type);
    private sealed record DeckFileDto(DeckDto Data);

    internal sealed record DeckDto(string? Name, List<CardDto>? Commander, List<CardDto>? MainBoard, List<CardDto>? SideBoard);
    internal sealed record CardDto(string Name, int Count, string? SetCode, string? Number, IdentifiersDto? Identifiers);
    internal sealed record IdentifiersDto(string? ScryfallId);
}

public sealed class PreconException(string message) : Exception(message);
