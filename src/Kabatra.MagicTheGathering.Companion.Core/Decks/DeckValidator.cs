using Kabatra.MagicTheGathering.Companion.Core.Cards;

namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

public enum IssueSeverity
{
    /// <summary>The deck can't be played in this format as it stands.</summary>
    Error,
    /// <summary>Worth a look, but may be fine (e.g. a card we couldn't look up).</summary>
    Warning,
}

public sealed record DeckIssue(IssueSeverity Severity, string Message, string? CardName = null);

/// <summary>
/// Checks a deck against a format's construction rules: deck size, sideboard size, copy limits,
/// banned and restricted cards, and (for commander formats) the commander and color identity.
/// </summary>
public static class DeckValidator
{
    /// <param name="cards">Card data for the deck's lines, keyed by Scryfall id.</param>
    public static IReadOnlyList<DeckIssue> Validate(Deck deck, IReadOnlyDictionary<Guid, CardInfo> cards) =>
        Validate(deck, deck.GetFormat(), cards);

    public static IReadOnlyList<DeckIssue> Validate(Deck deck, DeckFormat format, IReadOnlyDictionary<Guid, CardInfo> cards)
    {
        var issues = new List<DeckIssue>();
        CardInfo? Lookup(DeckCard line) => line.ScryfallId is { } id && cards.TryGetValue(id, out var c) ? c : null;

        foreach (var line in deck.Cards.Where(l => Lookup(l) is null))
            issues.Add(new(IssueSeverity.Warning, $"Couldn't find \"{line.Name}\" on Scryfall, so it wasn't checked.", line.Name));

        if (format.ScryfallKey is null) return issues; // Casual: anything goes.

        CheckSizes(deck, format, issues);
        CheckCopiesAndLegality(deck, format, Lookup, issues);
        if (format.HasCommander) CheckCommanders(deck, format, Lookup, issues);

        return issues;
    }

    private static void CheckSizes(Deck deck, DeckFormat format, List<DeckIssue> issues)
    {
        var main = deck.Count(DeckSection.Main);
        var commanders = deck.Count(DeckSection.Commander);
        var side = deck.Count(DeckSection.Sideboard);

        if (format.ExactDeckSize is int exact)
        {
            var total = main + commanders;
            if (total != exact)
                issues.Add(new(IssueSeverity.Error, $"{format.Name} decks have exactly {exact} cards including the commander; this one has {total}."));
        }
        else if (main < format.MinMainDeck)
        {
            issues.Add(new(IssueSeverity.Error, $"The main deck needs at least {format.MinMainDeck} cards; it has {main}."));
        }

        if (!format.HasCommander && commanders > 0)
            issues.Add(new(IssueSeverity.Error, $"{format.Name} doesn't use a commander. Move those cards to the main deck or sideboard."));

        if (format.MaxSideboard == 0 && side > 0)
            issues.Add(new(IssueSeverity.Warning, $"{format.Name} has no sideboard; the {side} sideboard card(s) aren't part of the deck."));
        else if (side > format.MaxSideboard)
            issues.Add(new(IssueSeverity.Error, $"The sideboard can have at most {format.MaxSideboard} cards; it has {side}."));
    }

    private static void CheckCopiesAndLegality(Deck deck, DeckFormat format, Func<DeckCard, CardInfo?> lookup, List<DeckIssue> issues)
    {
        var key = format.ScryfallKey!;
        var inPlay = format.MaxSideboard == 0 ? deck.Cards.Where(c => c.Section != DeckSection.Sideboard) : deck.Cards;

        // Group by the card itself (all printings together) so 2 + 2 of different arts count as 4.
        var groups = inPlay
            .Select(line => (Line: line, Card: lookup(line)))
            .Where(x => x.Card is not null)
            .GroupBy(x => x.Card!.OracleId != Guid.Empty ? x.Card.OracleId.ToString() : x.Card!.Name);

        foreach (var group in groups)
        {
            var card = group.First().Card!;
            var copies = group.Sum(x => x.Line.Quantity);

            switch (card.LegalityIn(key))
            {
                case "banned":
                    issues.Add(new(IssueSeverity.Error, $"{card.Name} is banned in {format.Name}.", card.Name));
                    continue;
                case "not_legal":
                    issues.Add(new(IssueSeverity.Error, $"{card.Name} isn't legal in {format.Name}.", card.Name));
                    continue;
                case "restricted" when copies > 1:
                    issues.Add(new(IssueSeverity.Error, $"{card.Name} is restricted in {format.Name}: only 1 copy allowed (has {copies}).", card.Name));
                    continue;
            }

            var limit = card.CopyLimitOverride switch
            {
                null => int.MaxValue,
                0 => format.MaxCopies,
                int n => Math.Max(n, format.MaxCopies),
            };
            if (copies > limit)
            {
                var allowed = limit == 1 ? "only 1 copy is allowed" : $"at most {limit} copies are allowed";
                issues.Add(new(IssueSeverity.Error, $"{card.Name}: {allowed} (has {copies}).", card.Name));
            }
        }
    }

    private static void CheckCommanders(Deck deck, DeckFormat format, Func<DeckCard, CardInfo?> lookup, List<DeckIssue> issues)
    {
        var lines = deck.Commanders.ToList();
        var count = lines.Sum(l => l.Quantity);
        var commanders = lines.Select(lookup).OfType<CardInfo>().ToList();

        if (count == 0)
        {
            issues.Add(new(IssueSeverity.Error, format.IsOathbreaker
                ? "Choose an oathbreaker (a planeswalker) and a signature spell."
                : "Choose a commander."));
            return;
        }

        if (format.IsOathbreaker)
            CheckOathbreaker(commanders, count, issues);
        else
            CheckCommanderPair(commanders, count, format, issues);

        // Every card must fit within the commanders' combined color identity.
        if (commanders.Count == 0) return;
        var identity = commanders.SelectMany(c => c.ColorIdentity).ToHashSet();
        var outside = deck.Cards
            .Where(l => l.Section != DeckSection.Sideboard)
            .Select(lookup)
            .OfType<CardInfo>()
            .Where(c => c.ColorIdentity.Any(color => !identity.Contains(color)))
            .DistinctBy(c => c.Name);
        foreach (var card in outside)
            issues.Add(new(IssueSeverity.Error,
                $"{card.Name} ({string.Join("", card.ColorIdentity)}) is outside the commander's color identity ({(identity.Count == 0 ? "colorless" : string.Join("", OrderWubrg(identity)))}).",
                card.Name));
    }

    private static void CheckCommanderPair(List<CardInfo> commanders, int count, DeckFormat format, List<DeckIssue> issues)
    {
        // Brawl also allows any legendary planeswalker as a commander.
        var walkersAllowed = format.Id is "brawl" or "historicbrawl";
        bool Allowed(CardInfo c) =>
            c.CanBeCommander
            || (walkersAllowed && c.IsLegendary && c.IsPlaneswalker)
            || (commanders.Count == 2 && c.IsBackground);

        foreach (var c in commanders.Where(c => !Allowed(c)))
            issues.Add(new(IssueSeverity.Error,
                $"{c.Name} can't be a commander (it isn't a legendary {(walkersAllowed ? "creature or planeswalker" : "creature")}).", c.Name));

        if (count > 2)
        {
            issues.Add(new(IssueSeverity.Error, $"A deck can have at most two commanders; this one has {count}."));
        }
        else if (count == 2 && commanders.Count == 2 && !ArePartners(commanders[0], commanders[1]))
        {
            issues.Add(new(IssueSeverity.Error,
                $"{commanders[0].Name} and {commanders[1].Name} can't both be commanders: two commanders need Partner, Friends forever, Choose a Background, or Doctor's companion."));
        }
    }

    private static void CheckOathbreaker(List<CardInfo> cards, int count, List<DeckIssue> issues)
    {
        var walkers = cards.Count(c => c.IsPlaneswalker);
        var spells = cards.Count(c => c.IsInstantOrSorcery);
        if (count > 2 || walkers != 1 || spells > 1)
            issues.Add(new(IssueSeverity.Error, "The commander slot should hold one planeswalker (the oathbreaker) and one instant or sorcery (the signature spell)."));
        else if (spells == 0)
            issues.Add(new(IssueSeverity.Warning, "Add a signature spell (an instant or sorcery) next to the oathbreaker."));
    }

    private static bool ArePartners(CardInfo a, CardInfo b)
    {
        bool Has(CardInfo c, string keyword) =>
            c.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase)
            || c.OracleText.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        // "Partner with X" names a specific partner; plain Partner pairs with any other Partner.
        if (Has(a, "Partner") && Has(b, "Partner"))
        {
            var aWith = a.OracleText.Contains("Partner with ", StringComparison.OrdinalIgnoreCase);
            var bWith = b.OracleText.Contains("Partner with ", StringComparison.OrdinalIgnoreCase);
            if (!aWith && !bWith) return true;
            return a.OracleText.Contains($"Partner with {b.FrontName}", StringComparison.OrdinalIgnoreCase)
                && b.OracleText.Contains($"Partner with {a.FrontName}", StringComparison.OrdinalIgnoreCase);
        }
        if (Has(a, "Friends forever") && Has(b, "Friends forever")) return true;
        if ((Has(a, "Choose a Background") && b.IsBackground) || (Has(b, "Choose a Background") && a.IsBackground)) return true;
        if ((Has(a, "Doctor's companion") && IsDoctor(b)) || (Has(b, "Doctor's companion") && IsDoctor(a))) return true;
        return false;
    }

    private static bool IsDoctor(CardInfo c) => c.TypeLine.Contains("Time Lord Doctor", StringComparison.Ordinal);

    private static IEnumerable<string> OrderWubrg(IEnumerable<string> colors) =>
        colors.OrderBy(c => "WUBRG".IndexOf(c, StringComparison.Ordinal));
}
