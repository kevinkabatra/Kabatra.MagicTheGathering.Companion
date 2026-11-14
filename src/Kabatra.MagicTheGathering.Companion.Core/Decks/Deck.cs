using Kabatra.MagicTheGathering.Companion.Core.Cards;

namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

public enum DeckSection
{
    Main,
    Sideboard,
    /// <summary>The commander(s), or the oathbreaker and signature spell in Oathbreaker.</summary>
    Commander,
}

/// <summary>One line of a deck: a number of copies of a card in one section.</summary>
public sealed class DeckCard
{
    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public DeckSection Section { get; set; } = DeckSection.Main;

    /// <summary>The Scryfall printing this line uses, once the card has been looked up.</summary>
    public Guid? ScryfallId { get; set; }

    /// <summary>Set code and collector number from an imported list, used to find the exact printing.</summary>
    public string? SetCode { get; set; }
    public string? CollectorNumber { get; set; }

    public DeckCard Clone() => (DeckCard)MemberwiseClone();
}

/// <summary>A deck list, owned by a player profile and visible in every playgroup that player is in.</summary>
public sealed class Deck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>The owner's profile name, for showing on screen. Read from the profile; not saved with the deck.</summary>
    public string OwnerName { get; set; } = "";

    /// <summary>
    /// The player profile that owns this deck. Anyone can play it; only whoever manages that profile can change it.
    /// Null only for a deck that hasn't been saved yet, which then goes to the signed-in user's own profile.
    /// </summary>
    public Guid? OwnerProfileId { get; set; }

    /// <summary>A <see cref="DeckFormat.Id"/>.</summary>
    public string Format { get; set; } = DeckFormat.Commander.Id;

    public string Notes { get; set; } = "";

    /// <summary>Art shown on deck tiles and on the player's card in a game, usually the commander's.</summary>
    public string? CoverImageUrl { get; set; }

    public List<DeckCard> Cards { get; set; } = [];

    /// <summary>
    /// Whether this deck's cards were added to its owner's collection with "Add its cards to the collection",
    /// so they aren't added twice. Not shown on screen. Cleared when the deck changes owner, which also takes
    /// the cards back out of the previous owner's collection.
    /// </summary>
    public bool CardsInCollection { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DeckFormat GetFormat() => DeckFormat.FromId(Format);

    public int Count(DeckSection section) => Cards.Where(c => c.Section == section).Sum(c => c.Quantity);

    public IEnumerable<DeckCard> In(DeckSection section) => Cards.Where(c => c.Section == section);

    public IEnumerable<DeckCard> Commanders => In(DeckSection.Commander);

    /// <summary>
    /// Adds copies of a card to a section, merging with an existing line for the same card.
    /// </summary>
    public DeckCard Add(CardInfo card, DeckSection section = DeckSection.Main, int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));

        var existing = Cards.FirstOrDefault(c => c.Section == section && (c.ScryfallId == card.Id || SameName(c.Name, card)));
        if (existing is not null)
        {
            existing.Quantity += quantity;
            existing.ScryfallId ??= card.Id;
            return existing;
        }

        var line = new DeckCard
        {
            Name = card.Name,
            Quantity = quantity,
            Section = section,
            ScryfallId = card.Id,
            SetCode = card.SetCode,
            CollectorNumber = card.CollectorNumber,
        };
        Cards.Add(line);
        return line;
    }

    /// <summary>Sets how many copies a line has; zero or less removes it.</summary>
    public void SetQuantity(DeckCard line, int quantity)
    {
        if (quantity <= 0) Cards.Remove(line);
        else line.Quantity = quantity;
    }

    /// <summary>Moves a whole line to another section, merging with a line already there.</summary>
    public void Move(DeckCard line, DeckSection to)
    {
        if (line.Section == to) return;
        var target = Cards.FirstOrDefault(c => c != line && c.Section == to
            && (line.ScryfallId is not null ? c.ScryfallId == line.ScryfallId : string.Equals(c.Name, line.Name, StringComparison.OrdinalIgnoreCase)));
        if (target is not null)
        {
            target.Quantity += line.Quantity;
            Cards.Remove(line);
        }
        else
        {
            line.Section = to;
        }
    }

    public Deck Clone()
    {
        var copy = (Deck)MemberwiseClone();
        copy.Cards = Cards.Select(c => c.Clone()).ToList();
        return copy;
    }

    private static bool SameName(string name, CardInfo card) =>
        string.Equals(name, card.Name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, card.FrontName, StringComparison.OrdinalIgnoreCase);
}
