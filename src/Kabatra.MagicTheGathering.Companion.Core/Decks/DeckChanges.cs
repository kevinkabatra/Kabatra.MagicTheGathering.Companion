namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

/// <summary>A net change to one card in one section since the deck was saved: copies added (positive) or removed.</summary>
public sealed record DeckChange(string Name, DeckSection Section, int Delta)
{
    public bool IsAddition => Delta > 0;
}

/// <summary>Copies of one line added (positive) or removed in one section, with the printing it was for.</summary>
internal readonly record struct LineDelta(string Name, Guid? ScryfallId, string? SetCode, string? CollectorNumber, DeckSection Section, int Delta);

/// <summary>
/// Card lines as they were when loaded or last saved, to list what's changed since and to put them back.
/// Lines are tracked by identity rather than name, because looking a card up can rename its line (to the card's
/// full Scryfall name) and a list can hold several printings of one name.
/// </summary>
internal sealed class CardLinesSnapshot
{
    private sealed record Saved(string Name, Guid? ScryfallId, string? SetCode, string? CollectorNumber, int Quantity, DeckSection Section);

    private readonly Dictionary<DeckCard, Saved> _lines = new(ReferenceEqualityComparer.Instance);
    private readonly List<DeckCard> _order;

    public CardLinesSnapshot(IEnumerable<DeckCard> lines)
    {
        _order = [.. lines];
        foreach (var line in _order)
        {
            _lines[line] = new Saved(line.Name, line.ScryfallId, line.SetCode, line.CollectorNumber, line.Quantity, line.Section);
        }
    }

    /// <summary>
    /// Per line, copies added or removed since the snapshot. A line moved to another section shows as removed from
    /// the old one and added to the new one. Lines that didn't change are left out.
    /// </summary>
    public IEnumerable<LineDelta> Deltas(IEnumerable<DeckCard> current)
    {
        var now = new HashSet<DeckCard>(current, ReferenceEqualityComparer.Instance);
        foreach (var (line, saved) in _lines)
        {
            if (!now.Contains(line))
            {
                yield return new LineDelta(saved.Name, saved.ScryfallId, saved.SetCode, saved.CollectorNumber, saved.Section, -saved.Quantity);
            }
            else if (line.Section == saved.Section)
            {
                if (line.Quantity != saved.Quantity) yield return Now(line, line.Quantity - saved.Quantity);
            }
            else
            {
                yield return Now(line, -saved.Quantity) with { Section = saved.Section };
                yield return Now(line, line.Quantity);
            }
        }
        foreach (var line in now.Where(l => !_lines.ContainsKey(l)))
        {
            yield return Now(line, line.Quantity);
        }
    }

    /// <summary>The lines as they were, in their old order, with their old copies and sections put back.</summary>
    public List<DeckCard> Restore()
    {
        foreach (var line in _order)
        {
            var saved = _lines[line];
            line.Quantity = saved.Quantity;
            line.Section = saved.Section;
        }
        return [.. _order];
    }

    private static LineDelta Now(DeckCard line, int delta) =>
        new(line.Name, line.ScryfallId, line.SetCode, line.CollectorNumber, line.Section, delta);
}

/// <summary>A deck as it was when loaded or last saved, to list what's changed since and to discard the changes.</summary>
public sealed class DeckSnapshot
{
    private readonly CardLinesSnapshot _lines;
    private readonly (string Name, string OwnerName, Guid? OwnerProfileId, string Format, string Notes) _details;

    private DeckSnapshot(Deck deck)
    {
        _lines = new CardLinesSnapshot(deck.Cards);
        _details = (deck.Name, deck.OwnerName, deck.OwnerProfileId, deck.Format, deck.Notes);
    }

    public static DeckSnapshot Of(Deck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        return new DeckSnapshot(deck);
    }

    /// <summary>
    /// Puts the deck back as it was in the snapshot, undoing every unsaved change: cards, name, owner, format and
    /// notes. Lines keep anything learned by looking their card up since (its full name, its printing).
    /// </summary>
    public void Restore(Deck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        deck.Cards = _lines.Restore();
        (deck.Name, deck.OwnerName, deck.OwnerProfileId, deck.Format, deck.Notes) = _details;
    }

    /// <summary>
    /// Cards added, removed or moved since the snapshot, netted per card name and section and sorted by section then
    /// name. A move shows as copies removed from one section and added to another.
    /// </summary>
    public IReadOnlyList<DeckChange> ChangesIn(Deck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        return _lines.Deltas(deck.Cards)
            .GroupBy(d => (Name: d.Name.ToUpperInvariant(), d.Section))
            .Select(g => new DeckChange(g.First().Name, g.Key.Section, g.Sum(d => d.Delta)))
            .Where(c => c.Delta != 0)
            .OrderBy(c => SectionOrder(c.Section))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int SectionOrder(DeckSection section) => section switch
    {
        DeckSection.Commander => 0,
        DeckSection.Main => 1,
        _ => 2,
    };
}
