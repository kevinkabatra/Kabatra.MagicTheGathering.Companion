namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

/// <summary>Wizards of the Coast's Commander Brackets: how strong a Commander deck is, from 1 to 5.</summary>
public enum CommanderBracket
{
    Exhibition = 1,
    Core = 2,
    Upgraded = 3,
    Optimized = 4,
    Competitive = 5,
}

/// <summary>A deck's bracket, the cards that set it, and whether the owner chose it.</summary>
/// <param name="Bracket">The bracket to show: the owner's choice when it stands, otherwise the estimate.</param>
public sealed record BracketAssessment(
    CommanderBracket Bracket,
    IReadOnlyList<string> GameChangers,
    IReadOnlyList<string> MassLandDenial,
    IReadOnlyList<string> ExtraTurns,
    IReadOnlyList<TwoCardCombo> Combos,
    IReadOnlyList<string> Reasons)
{
    /// <summary>The bracket the cards alone point to (2, 3 or 4).</summary>
    public CommanderBracket Estimated { get; init; } = Bracket;

    /// <summary>The bracket the owner chose (1 or 5), whether or not it stands. Null when they didn't choose one.</summary>
    public CommanderBracket? Declared { get; init; }

    /// <summary>True when the owner's choice is the bracket shown.</summary>
    public bool IsDeclared => Declared is { } d && d == Bracket;

    public int Number => (int)Bracket;
    public string Name => CommanderBrackets.NameOf(Bracket);

    /// <summary>"Bracket 3 · Upgraded".</summary>
    public string Label => $"Bracket {Number} · {Name}";

    public IEnumerable<TwoCardCombo> EarlyCombos => Combos.Where(c => c.IsEarly);
    public IEnumerable<TwoCardCombo> LateCombos => Combos.Where(c => !c.IsEarly);
}

/// <summary>
/// Estimates a Commander deck's bracket from its card names, following the Commander Brackets rules
/// (beta, as updated February 9, 2026). The lowest bracket whose rules the deck fits wins:
/// <list type="bullet">
/// <item>Bracket 4 (Optimized): 4+ Game Changers, any mass land denial, an early two-card combo,
/// or more extra-turn cards than "low quantities".</item>
/// <item>Bracket 3 (Upgraded): 1–3 Game Changers, or a two-card combo that only comes together late.</item>
/// <item>Bracket 2 (Core): everything else.</item>
/// </list>
/// Brackets 1 (Exhibition) and 5 (cEDH) depend on what the deck is built to do, not on its cards,
/// so they're never estimated; the owner can choose them instead (<see cref="Deck.DeclaredBracket"/>).
/// cEDH has no card limits beyond Bracket 4's, so choosing it always stands. Exhibition stands only when the
/// deck keeps to Bracket 1's limits: no Game Changers, mass land denial, two-card combos or extra turns.
/// Tutors stopped counting in the October 21, 2025 update.
/// </summary>
public static class CommanderBrackets
{
    public static string NameOf(CommanderBracket bracket) => bracket switch
    {
        CommanderBracket.Exhibition => "Exhibition",
        CommanderBracket.Core => "Core",
        CommanderBracket.Upgraded => "Upgraded",
        CommanderBracket.Optimized => "Optimized",
        CommanderBracket.Competitive => "cEDH",
        _ => bracket.ToString(),
    };

    /// <summary>Wizards' one-line description of each bracket.</summary>
    public static string DescriptionOf(CommanderBracket bracket) => bracket switch
    {
        CommanderBracket.Exhibition => "Ultra-casual decks built around a theme. Games last at least nine turns.",
        CommanderBracket.Core => "About as strong as a typical precon. No Game Changers, combos or mass land denial. Games last at least eight turns.",
        CommanderBracket.Upgraded => "Stronger than a precon, with up to three Game Changers and no early two-card combos. Games last at least six turns.",
        CommanderBracket.Optimized => "High power with no limits beyond the banned list. Games last at least four turns.",
        CommanderBracket.Competitive => "Built to win in the competitive (cEDH) metagame. Games can end on any turn.",
        _ => "",
    };

    /// <summary>Whether brackets apply to this deck's format. They're written for Commander only.</summary>
    public static bool AppliesTo(Deck deck) => deck.Format == DeckFormat.Commander.Id;

    /// <summary>The brackets an owner can choose for a deck, because its cards can't show them.</summary>
    public static IReadOnlyList<CommanderBracket> Declarable { get; } = [CommanderBracket.Exhibition, CommanderBracket.Competitive];

    public static bool CanDeclare(CommanderBracket bracket) => Declarable.Contains(bracket);

    /// <summary>The deck's bracket, or null for decks that aren't Commander decks.</summary>
    public static BracketAssessment? Assess(Deck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        if (!AppliesTo(deck)) return null;
        var estimate = Assess(deck.Cards.Where(c => c.Section != DeckSection.Sideboard).Select(c => c.Name));
        return deck.DeclaredBracket is { } declared ? WithDeclared(estimate, declared) : estimate;
    }

    /// <summary>Applies the owner's choice of Bracket 1 or 5 to an estimate. Other brackets are ignored.</summary>
    public static BracketAssessment WithDeclared(BracketAssessment estimate, CommanderBracket declared)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (!CanDeclare(declared)) return estimate;

        var reasons = new List<string>();
        var bracket = declared;
        if (declared == CommanderBracket.Competitive)
        {
            reasons.Add("Marked as cEDH by its owner");
        }
        else if (ExhibitionBreaks(estimate) is { Count: > 0 } breaks)
        {
            bracket = estimate.Bracket;
            reasons.Add($"Marked as Exhibition, but Bracket 1 doesn't allow {JoinOr(breaks)}");
        }
        else
        {
            reasons.Add("Marked as Exhibition by its owner");
        }
        reasons.Add($"Estimated from its cards: Bracket {(int)estimate.Bracket} · {NameOf(estimate.Bracket)}");
        reasons.AddRange(estimate.Reasons);

        return estimate with { Bracket = bracket, Reasons = reasons, Estimated = estimate.Bracket, Declared = declared };
    }

    /// <summary>What keeps a deck out of Bracket 1, e.g. ["Game Changers", "extra turns"].</summary>
    private static List<string> ExhibitionBreaks(BracketAssessment a)
    {
        var breaks = new List<string>();
        if (a.GameChangers.Count > 0) breaks.Add("Game Changers");
        if (a.MassLandDenial.Count > 0) breaks.Add("mass land denial");
        if (a.Combos.Count > 0) breaks.Add("two-card combos");
        if (a.ExtraTurns.Count > 0) breaks.Add("extra turns");
        return breaks;
    }

    private static string JoinOr(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1];

    /// <summary>Rates a set of card names (the commander and main deck) as a Commander deck.</summary>
    public static BracketAssessment Assess(IEnumerable<string> cardNames)
    {
        // One entry per card, keeping the deck's spelling for display, and every face name for matching.
        var cards = cardNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .DistinctBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => (Name: n, Faces: FacesOf(n)))
            .ToList();

        List<string> Matching(IReadOnlySet<string> list) =>
            cards.Where(c => c.Faces.Any(list.Contains)).Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var gameChangers = Matching(BracketCardLists.GameChangers);
        var landDenial = Matching(BracketCardLists.MassLandDenial);
        var extraTurns = Matching(BracketCardLists.ExtraTurns);

        var faces = cards.SelectMany(c => c.Faces).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combos = BracketCardLists.TwoCardCombos
            .Where(c => faces.Contains(c.First) && faces.Contains(c.Second))
            .OrderBy(c => c.Cost)
            .ToList();

        var reasons = new List<string>();
        var bracket = CommanderBracket.Core;
        void AtLeast(CommanderBracket b, string reason)
        {
            if (b > bracket) bracket = b;
            reasons.Add(reason);
        }

        if (gameChangers.Count > 3)
            AtLeast(CommanderBracket.Optimized, $"{gameChangers.Count} Game Changers (Bracket 3 allows up to 3)");
        else if (gameChangers.Count > 0)
            AtLeast(CommanderBracket.Upgraded, $"{gameChangers.Count} Game Changer{(gameChangers.Count == 1 ? "" : "s")} (Brackets 1–2 allow none)");

        if (landDenial.Count > 0)
            AtLeast(CommanderBracket.Optimized, "Mass land denial (not allowed below Bracket 4)");

        var early = combos.Where(c => c.IsEarly).ToList();
        if (early.Count > 0)
            AtLeast(CommanderBracket.Optimized, $"Early-game two-card combo{(early.Count == 1 ? "" : "s")} (not allowed below Bracket 4)");
        else if (combos.Count > 0)
            AtLeast(CommanderBracket.Upgraded, $"Late-game two-card combo{(combos.Count == 1 ? "" : "s")} (not allowed below Bracket 3)");

        if (extraTurns.Count > BracketCardLists.MaxCasualExtraTurns)
            AtLeast(CommanderBracket.Optimized, $"{extraTurns.Count} extra-turn cards (Brackets 2–3 allow only a few, not chained)");
        else if (extraTurns.Count > 0)
            reasons.Add($"{extraTurns.Count} extra-turn card{(extraTurns.Count == 1 ? "" : "s")} (fine in small numbers from Bracket 2)");

        if (reasons.Count == 0)
            reasons.Add("No Game Changers, mass land denial, two-card combos or extra turns");

        return new(bracket, gameChangers, landDenial, extraTurns, combos, reasons);
    }

    /// <summary>"Fire // Ice" matches as "Fire // Ice", "Fire" and "Ice".</summary>
    private static string[] FacesOf(string name)
    {
        var parts = name.Split(" // ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? [name, .. parts] : [name];
    }
}
