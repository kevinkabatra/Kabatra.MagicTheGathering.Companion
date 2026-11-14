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

    private DeckSnapshot(Dictionary<DeckCard, (string, int, DeckSection)> lines) => _lines = lines;

    public static DeckSnapshot Of(Deck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        var lines = new Dictionary<DeckCard, (string, int, DeckSection)>(ReferenceEqualityComparer.Instance);
        foreach (var line in deck.Cards) lines[line] = (line.Name, line.Quantity, line.Section);
        return new DeckSnapshot(lines);
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
