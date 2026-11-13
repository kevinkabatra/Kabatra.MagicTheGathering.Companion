using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kabatra.MagicTheGathering.Companion.Core.Cards;

/// <summary>A way to look a card up: by Scryfall id, by exact printing, or by name.</summary>
public sealed record CardIdentifier(Guid? Id = null, string? Name = null, string? Set = null, string? CollectorNumber = null)
{
    public static CardIdentifier ById(Guid id) => new(Id: id);
    public static CardIdentifier ByName(string name) => new(Name: name);
    public static CardIdentifier ByPrinting(string set, string collectorNumber) => new(Set: set, CollectorNumber: collectorNumber);
}

public sealed record CardSearchPage(IReadOnlyList<CardInfo> Cards, int TotalCards, bool HasMore);

/// <summary>
/// A small client for the public Scryfall API (https://scryfall.com/docs/api). No key is needed.
/// Scryfall asks for no more than about 10 requests a second, so calls are spaced out.
/// </summary>
public sealed class ScryfallClient
{
    public static readonly Uri DefaultBaseAddress = new("https://api.scryfall.com/");
    private const int CollectionBatchSize = 75; // Scryfall's limit for /cards/collection
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    /// <param name="http">An HttpClient whose BaseAddress is the Scryfall API (or null to use the default).</param>
    public ScryfallClient(HttpClient http, TimeProvider? clock = null)
    {
        _http = http;
        _http.BaseAddress ??= DefaultBaseAddress;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Card names that start with or closely match <paramref name="partialName"/>, at most 20.</summary>
    public async Task<IReadOnlyList<string>> AutocompleteAsync(string partialName, CancellationToken ct = default)
    {
        if (partialName.Trim().Length < 2) return [];
        var result = await GetAsync<CatalogDto>($"cards/autocomplete?q={Uri.EscapeDataString(partialName.Trim())}", ct);
        return result?.Data ?? [];
    }

    /// <summary>
    /// Runs a Scryfall search (full syntax supported, e.g. "t:dragon c:r"). One printing per card, sorted by name.
    /// </summary>
    public async Task<CardSearchPage> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new([], 0, false);
        var url = $"cards/search?q={Uri.EscapeDataString(query.Trim())}&unique=cards&order=name&page={page}";
        var result = await GetAsync<ListDto>(url, ct);
        if (result is null) return new([], 0, false);
        return new(result.Data.Select(ToCardInfo).ToList(), result.TotalCards, result.HasMore);
    }

    /// <summary>
    /// Every paper printing of a card (each set, promo and collector number), newest first, at most
    /// <paramref name="maxCards"/>. Basic lands have hundreds, so the cap keeps this to a few requests.
    /// </summary>
    public async Task<IReadOnlyList<CardInfo>> GetPrintingsAsync(CardInfo card, int maxCards = 350, CancellationToken ct = default)
    {
        var which = card.OracleId != Guid.Empty ? $"oracleid:{card.OracleId}" : $"!\"{card.Name}\"";
        var query = Uri.EscapeDataString($"{which} game:paper");
        var printings = new List<CardInfo>();
        for (var page = 1; printings.Count < maxCards; page++)
        {
            var result = await GetAsync<ListDto>($"cards/search?q={query}&unique=prints&order=released&dir=desc&page={page}", ct);
            if (result is null) break;
            printings.AddRange(result.Data.Select(ToCardInfo));
            if (!result.HasMore) break;
        }
        return printings.Take(maxCards).ToList();
    }

    /// <summary>Finds a single card by name, forgiving small typos and front-face-only names.</summary>
    public async Task<CardInfo?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var card = await GetAsync<CardDto>($"cards/named?fuzzy={Uri.EscapeDataString(name.Trim())}", ct);
        return card is null ? null : ToCardInfo(card);
    }

    /// <summary>Finds one exact printing by set code and collector number, e.g. ("2x2", "117").</summary>
    public async Task<CardInfo?> GetByPrintingAsync(string set, string collectorNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(set) || string.IsNullOrWhiteSpace(collectorNumber)) return null;
        var url = $"cards/{Uri.EscapeDataString(set.Trim().ToLowerInvariant())}/{Uri.EscapeDataString(collectorNumber.Trim())}";
        var card = await GetAsync<CardDto>(url, ct);
        return card is null ? null : ToCardInfo(card);
    }

    public async Task<CardInfo?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var card = await GetAsync<CardDto>($"cards/{id}", ct);
        return card is null ? null : ToCardInfo(card);
    }

    /// <summary>
    /// Looks up many cards at once. Returns each identifier with the card it matched, or null if Scryfall
    /// didn't find it. Identifiers that ask for an exact printing fall back to the card's name when the
    /// printing is unknown.
    /// </summary>
    public async Task<IReadOnlyList<(CardIdentifier Identifier, CardInfo? Card)>> GetCollectionAsync(
        IReadOnlyList<CardIdentifier> identifiers, CancellationToken ct = default)
    {
        var results = new List<(CardIdentifier, CardInfo?)>(identifiers.Count);
        foreach (var batch in identifiers.Chunk(CollectionBatchSize))
        {
            var request = new CollectionRequestDto(batch.Select(ToDto).ToList());
            var response = await SendAsync(() => _http.PostAsJsonAsync("cards/collection", request, Json, ct), ct);
            var body = response is null ? null : await response.Content.ReadFromJsonAsync<CollectionDto>(Json, ct);
            var found = body?.Data.Select(ToCardInfo).ToList() ?? [];

            foreach (var identifier in batch)
                results.Add((identifier, found.FirstOrDefault(card => Matches(identifier, card))));
        }
        return results;
    }

    private static bool Matches(CardIdentifier identifier, CardInfo card)
    {
        if (identifier.Id is { } id) return card.Id == id;
        if (identifier.Set is { } set && identifier.CollectorNumber is { } number)
            return string.Equals(card.SetCode, set, StringComparison.OrdinalIgnoreCase)
                && string.Equals(card.CollectorNumber, number, StringComparison.OrdinalIgnoreCase);
        return identifier.Name is { } name
            && (string.Equals(card.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(card.FrontName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static IdentifierDto ToDto(CardIdentifier i) =>
        i.Id is { } id ? new IdentifierDto(Id: id)
        : i.Set is not null && i.CollectorNumber is not null ? new IdentifierDto(Set: i.Set.ToLowerInvariant(), CollectorNumber: i.CollectorNumber)
        : new IdentifierDto(Name: i.Name);

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        var response = await SendAsync(() => _http.GetAsync(url, ct), ct);
        return response is null ? null : await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    /// <summary>Sends a request after waiting out the rate limit. Returns null for "not found".</summary>
    private async Task<HttpResponseMessage?> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequest + MinGap - _clock.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _clock, ct);
            _lastRequest = _clock.GetUtcNow();
            var response = await send();
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                var error = await TryReadErrorAsync(response, ct);
                throw new ScryfallException(error ?? $"Scryfall returned {(int)response.StatusCode}.", response.StatusCode);
            }
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return (await response.Content.ReadFromJsonAsync<ErrorDto>(Json, ct))?.Details; }
        catch (JsonException) { return null; }
    }

    internal static CardInfo ToCardInfo(CardDto c)
    {
        var front = c.CardFaces?.FirstOrDefault();
        var images = c.ImageUris ?? front?.ImageUris;
        return new CardInfo
        {
            Id = c.Id,
            OracleId = c.OracleId ?? front?.OracleId ?? Guid.Empty,
            Name = c.Name,
            ManaCost = c.ManaCost ?? front?.ManaCost ?? "",
            ManaValue = c.Cmc,
            TypeLine = c.TypeLine ?? string.Join(" // ", c.CardFaces?.Select(f => f.TypeLine) ?? []),
            OracleText = c.OracleText ?? string.Join("\n//\n", c.CardFaces?.Select(f => f.OracleText) ?? []),
            ColorIdentity = c.ColorIdentity ?? [],
            Keywords = c.Keywords ?? [],
            Legalities = c.Legalities ?? new Dictionary<string, string>(),
            SetCode = c.Set ?? "",
            SetName = c.SetName ?? "",
            CollectorNumber = c.CollectorNumber ?? "",
            Rarity = c.Rarity ?? "",
            ImageSmall = images?.Small,
            ImageNormal = images?.Normal,
            ArtCrop = images?.ArtCrop,
            ScryfallUri = c.ScryfallUri,
            Prices = new CardPrices
            {
                Usd = ParsePrice(c.Prices?.Usd),
                UsdFoil = ParsePrice(c.Prices?.UsdFoil),
                UsdEtched = ParsePrice(c.Prices?.UsdEtched),
                Eur = ParsePrice(c.Prices?.Eur),
                EurFoil = ParsePrice(c.Prices?.EurFoil),
                Tix = ParsePrice(c.Prices?.Tix),
            },
        };
    }

    /// <summary>Scryfall sends prices as strings like "0.25", or null when there's no price.</summary>
    private static decimal? ParsePrice(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) ? price : null;

    // ----- Scryfall JSON shapes (only the fields we use) -----

    private sealed record CatalogDto(List<string> Data);
    private sealed record ListDto(List<CardDto> Data, int TotalCards, bool HasMore);
    private sealed record CollectionRequestDto(List<IdentifierDto> Identifiers);
    private sealed record CollectionDto(List<CardDto> Data);
    private sealed record ErrorDto(string? Details);

    private sealed record IdentifierDto(Guid? Id = null, string? Name = null, string? Set = null, string? CollectorNumber = null);

    internal sealed record ImageUrisDto(string? Small, string? Normal, string? ArtCrop);

    internal sealed record PricesDto(string? Usd, string? UsdFoil, string? UsdEtched, string? Eur, string? EurFoil, string? Tix);

    internal sealed record CardFaceDto(string? Name, string? ManaCost, string? TypeLine, string? OracleText, Guid? OracleId, ImageUrisDto? ImageUris);

    internal sealed record CardDto(
        Guid Id,
        Guid? OracleId,
        string Name,
        string? ManaCost,
        decimal Cmc,
        string? TypeLine,
        string? OracleText,
        List<string>? ColorIdentity,
        List<string>? Keywords,
        Dictionary<string, string>? Legalities,
        string? Set,
        string? SetName,
        string? CollectorNumber,
        string? Rarity,
        string? ScryfallUri,
        ImageUrisDto? ImageUris,
        List<CardFaceDto>? CardFaces,
        PricesDto? Prices = null);
}

public sealed class ScryfallException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
