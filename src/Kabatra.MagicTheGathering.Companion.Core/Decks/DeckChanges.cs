namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

/// <summary>A net change to one card in one section since the deck was saved: copies added (positive) or removed.</summary>
public sealed record DeckChange(string Name, DeckSection Section, int Delta)
{
    public bool IsAddition => Delta > 0;
}

/// <summary>
/// A deck's card lines as they were when it was loaded or last saved, to list what's changed since.
/// Lines are tracked by identity rather than name, because looking a card up can rename its line (to the card's
/// full Scryfall name) and a deck can hold several printings of one name.
/// </summary>
public sealed class DeckSnapshot
{
    private readonly Dictionary<DeckCard, (string Name, int Quantity, DeckSection Section)> _lines;
    private readonly List<DeckCard> _order;
    private readonly (string Name, string OwnerName, Guid? OwnerProfileId, string Format, string Notes) _details;

    private DeckSnapshot(Deck deck)
    {
        _lines = new Dictionary<DeckCard, (string, int, DeckSection)>(ReferenceEqualityComparer.Instance);
        foreach (var line in deck.Cards) _lines[line] = (line.Name, line.Quantity, line.Section);
        _order = [.. deck.Cards];
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
        foreach (var line in _order)
        {
            var (_, quantity, section) = _lines[line];
            line.Quantity = quantity;
            line.Section = section;
        }
        deck.Cards = [.. _order];
        (deck.Name, deck.OwnerName, deck.OwnerProfileId, deck.Format, deck.Notes) = _details;
    }

    /// <summary>
    /// Cards added, removed or moved since the snapshot, netted per card name and section and sorted by section then
    /// name. A move shows as copies removed from one section and added to another.
    /// </summary>
    public IReadOnlyList<DeckChange> ChangesIn(Deck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        var current = new HashSet<DeckCard>(deck.Cards, ReferenceEqualityComparer.Instance);
        var deltas = new List<(string Name, DeckSection Section, int Delta)>();

        foreach (var (line, saved) in _lines)
        {
            if (!current.Contains(line))
            {
                deltas.Add((saved.Name, saved.Section, -saved.Quantity));
            }
            else if (line.Section == saved.Section)
            {
                deltas.Add((line.Name, line.Section, line.Quantity - saved.Quantity));
            }
            else
            {
                deltas.Add((line.Name, saved.Section, -saved.Quantity));
                deltas.Add((line.Name, line.Section, line.Quantity));
            }
        }
        foreach (var line in deck.Cards.Where(l => !_lines.ContainsKey(l)))
        {
            deltas.Add((line.Name, line.Section, line.Quantity));
        }

        return deltas
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
