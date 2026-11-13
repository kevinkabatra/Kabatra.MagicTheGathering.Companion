namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

/// <summary>
/// Deck-building rules for a format: size, copy limit, sideboard, commanders, and which
/// Scryfall legality list to check against.
/// </summary>
public sealed record DeckFormat(
    string Id,
    string Name,
    string? ScryfallKey,
    int MinMainDeck,
    int? ExactDeckSize,
    int MaxCopies,
    int MaxSideboard,
    bool HasCommander,
    GameFormat GameFormat)
{
    /// <summary>Oathbreaker uses the commander slot for a planeswalker and a signature spell.</summary>
    public bool IsOathbreaker => Id == "oathbreaker";

    public bool IsSingleton => MaxCopies == 1;

    /// <summary>Label for the commander section in this format.</summary>
    public string CommanderLabel => IsOathbreaker ? "Oathbreaker & signature spell" : "Commander";

    public static DeckFormat Standard { get; } = new("standard", "Standard", "standard", 60, null, 4, 15, false, GameFormat.Standard);
    public static DeckFormat Pioneer { get; } = new("pioneer", "Pioneer", "pioneer", 60, null, 4, 15, false, GameFormat.Standard);
    public static DeckFormat Modern { get; } = new("modern", "Modern", "modern", 60, null, 4, 15, false, GameFormat.Standard);
    public static DeckFormat Legacy { get; } = new("legacy", "Legacy", "legacy", 60, null, 4, 15, false, GameFormat.Standard);
    public static DeckFormat Vintage { get; } = new("vintage", "Vintage", "vintage", 60, null, 4, 15, false, GameFormat.Standard);
    public static DeckFormat Pauper { get; } = new("pauper", "Pauper", "pauper", 60, null, 4, 15, false, GameFormat.Standard);
    public static DeckFormat Commander { get; } = new("commander", "Commander", "commander", 100, 100, 1, 0, true, GameFormat.Commander);
    public static DeckFormat Brawl { get; } = new("brawl", "Brawl (Standard)", "standardbrawl", 60, 60, 1, 0, true, GameFormat.Brawl);
    public static DeckFormat HistoricBrawl { get; } = new("historicbrawl", "Historic Brawl", "brawl", 100, 100, 1, 0, true, GameFormat.Brawl);
    public static DeckFormat Oathbreaker { get; } = new("oathbreaker", "Oathbreaker", "oathbreaker", 60, 60, 1, 0, true, GameFormat.Oathbreaker);
    public static DeckFormat Casual { get; } = new("casual", "Casual (no rules)", null, 0, null, int.MaxValue, int.MaxValue, false, GameFormat.Standard);

    public static IReadOnlyList<DeckFormat> All { get; } =
        [Commander, Standard, Pioneer, Modern, Legacy, Vintage, Pauper, Brawl, HistoricBrawl, Oathbreaker, Casual];

    /// <summary>Finds a format by id; unknown ids are treated as Casual so old data still opens.</summary>
    public static DeckFormat FromId(string? id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Casual;
}
