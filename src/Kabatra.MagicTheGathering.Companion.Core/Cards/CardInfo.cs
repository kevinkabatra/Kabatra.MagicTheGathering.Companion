using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kabatra.MagicTheGathering.Companion.Core.Cards;

/// <summary>
/// The parts of a Scryfall card the app needs: enough to show it, sort it, and check deck legality.
/// </summary>
public sealed partial record CardInfo
{
    /// <summary>Scryfall's id for this printing.</summary>
    public Guid Id { get; init; }

    /// <summary>Scryfall's id shared by every printing of the same card.</summary>
    public Guid OracleId { get; init; }

    /// <summary>Full name; double-faced and split cards are "Front // Back".</summary>
    public string Name { get; init; } = "";

    public string ManaCost { get; init; } = "";
    public decimal ManaValue { get; init; }
    public string TypeLine { get; init; } = "";
    public string OracleText { get; init; } = "";

    /// <summary>Color identity as WUBRG letters, e.g. ["W", "U"].</summary>
    public IReadOnlyList<string> ColorIdentity { get; init; } = [];

    /// <summary>Keyword abilities, e.g. "Partner", "Flying".</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Scryfall format key (e.g. "commander") to "legal", "not_legal", "banned" or "restricted".</summary>
    public IReadOnlyDictionary<string, string> Legalities { get; init; } = new Dictionary<string, string>();

    public string SetCode { get; init; } = "";
    public string SetName { get; init; } = "";
    public string CollectorNumber { get; init; } = "";
    public string Rarity { get; init; } = "";

    public string? ImageSmall { get; init; }
    public string? ImageNormal { get; init; }
    public string? ArtCrop { get; init; }
    public string? ScryfallUri { get; init; }

    /// <summary>Scryfall's market prices for this printing. Null for cards cached before prices were kept.</summary>
    public CardPrices? Prices { get; init; }

    /// <summary>
    /// The card's value in US dollars: the non-foil price, or the foil/etched price for printings
    /// that only exist as foils. Null when Scryfall has no price.
    /// </summary>
    [JsonIgnore]
    public decimal? UsdPrice => Prices is { } p ? p.Usd ?? p.UsdFoil ?? p.UsdEtched : null;

    /// <summary>The name of the front face, which is what most deck lists use for double-faced cards.</summary>
    [JsonIgnore]
    public string FrontName => Name.Split(" // ")[0];

    [JsonIgnore]
    public bool IsBasicLand => TypeLine.Contains("Basic", StringComparison.Ordinal) && TypeLine.Contains("Land", StringComparison.Ordinal);
    [JsonIgnore]
    public bool IsLegendary => FrontType.Contains("Legendary", StringComparison.Ordinal);
    [JsonIgnore]
    public bool IsCreature => FrontType.Contains("Creature", StringComparison.Ordinal);
    [JsonIgnore]
    public bool IsPlaneswalker => FrontType.Contains("Planeswalker", StringComparison.Ordinal);
    [JsonIgnore]
    public bool IsBackground => FrontType.Contains("Background", StringComparison.Ordinal);
    [JsonIgnore]
    public bool IsInstantOrSorcery => FrontType.Contains("Instant", StringComparison.Ordinal) || FrontType.Contains("Sorcery", StringComparison.Ordinal);

    /// <summary>True for legendary creatures and cards whose text says they can be your commander.</summary>
    [JsonIgnore]
    public bool CanBeCommander =>
        (IsLegendary && IsCreature)
        || OracleText.Contains("can be your commander", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How many copies of this card a constructed deck may hold, ignoring format bans:
    /// null means any number (basic lands, Relentless Rats and friends).
    /// </summary>
    [JsonIgnore]
    public int? CopyLimitOverride
    {
        get
        {
            if (IsBasicLand) return null;
            if (OracleText.Contains("A deck can have any number of cards named", StringComparison.OrdinalIgnoreCase)) return null;
            var upTo = UpToRegex().Match(OracleText);
            if (upTo.Success && WordNumbers.TryGetValue(upTo.Groups[1].Value.ToLowerInvariant(), out var n)) return n;
            return 0; // 0 = use the format's normal limit
        }
    }

    /// <summary>Format legality for a Scryfall format key; cards without data count as not legal.</summary>
    public string LegalityIn(string formatKey) =>
        Legalities.TryGetValue(formatKey, out var value) ? value : "not_legal";

    /// <summary>A broad card type used to group cards in a deck list.</summary>
    [JsonIgnore]
    public string MainType
    {
        get
        {
            // Order matters: an artifact creature is listed with creatures, a land creature with lands.
            foreach (var type in TypeOrder)
                if (FrontType.Contains(type, StringComparison.Ordinal)) return type;
            return "Other";
        }
    }

    public static IReadOnlyList<string> TypeOrder { get; } =
        ["Land", "Creature", "Planeswalker", "Battle", "Instant", "Sorcery", "Artifact", "Enchantment"];

    private string FrontType => TypeLine.Split(" // ")[0];

    private static readonly Dictionary<string, int> WordNumbers = new()
    {
        ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
    };

    [GeneratedRegex(@"A deck can have up to (\w+) cards named", RegexOptions.IgnoreCase)]
    private static partial Regex UpToRegex();
}

/// <summary>Market prices from Scryfall (sourced from TCGplayer, Cardmarket and MTGO), updated daily.</summary>
public sealed record CardPrices
{
    public decimal? Usd { get; init; }
    public decimal? UsdFoil { get; init; }
    public decimal? UsdEtched { get; init; }
    public decimal? Eur { get; init; }
    public decimal? EurFoil { get; init; }
    public decimal? Tix { get; init; }
}
