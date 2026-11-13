using System.Text.Json.Serialization;
using Kabatra.MagicTheGathering.Companion.Core.Cards;
using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Core.Profiles;

/// <summary>
/// Someone in the playgroup: the cards they own, the decks they built, and the games they've played.
/// Profiles aren't tied to sign-in accounts, so a friend without an account can still have one.
/// </summary>
public sealed class PlayerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>
    /// Every card the player owns, one line per printing. <see cref="DeckCard.Section"/> isn't used here;
    /// lines share the deck type so the same pricing, lookup and list-import code works on both.
    /// </summary>
    public List<DeckCard> Collection { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore] public int CardCount => Collection.Sum(c => c.Quantity);

    /// <summary>Adds copies of a printing, merging with a line for the same printing.</summary>
    public DeckCard AddToCollection(CardInfo card, int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(card);
        return AddToCollection(new DeckCard
        {
            Name = card.Name,
            Quantity = quantity,
            ScryfallId = card.Id,
            SetCode = card.SetCode,
            CollectorNumber = card.CollectorNumber,
        });
    }

    /// <summary>Adds a line, e.g. from a pasted list, merging with a line for the same printing.</summary>
    public DeckCard AddToCollection(DeckCard line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Quantity < 1) throw new ArgumentOutOfRangeException(nameof(line), "Quantity must be at least 1.");

        var existing = Collection.FirstOrDefault(c => SamePrinting(c, line));
        if (existing is not null)
        {
            existing.Quantity += line.Quantity;
            existing.ScryfallId ??= line.ScryfallId;
            return existing;
        }

        var copy = line.Clone();
        copy.Section = DeckSection.Main;
        Collection.Add(copy);
        return copy;
    }

    /// <summary>Adds every card in a deck (commander, main deck and sideboard) to the collection.</summary>
    public void AddDeckToCollection(Deck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        foreach (var line in deck.Cards) AddToCollection(line);
    }

    /// <summary>Sets how many copies a line has; zero or less removes it.</summary>
    public void SetQuantity(DeckCard line, int quantity)
    {
        if (quantity <= 0) Collection.Remove(line);
        else line.Quantity = quantity;
    }

    public PlayerProfile Clone()
    {
        var copy = (PlayerProfile)MemberwiseClone();
        copy.Collection = Collection.Select(c => c.Clone()).ToList();
        return copy;
    }

    /// <summary>
    /// Same Scryfall printing, or (for lines that haven't been looked up yet) the same name and printing details.
    /// Different printings stay apart because they're worth different amounts.
    /// </summary>
    private static bool SamePrinting(DeckCard a, DeckCard b) =>
        a.ScryfallId is { } x && b.ScryfallId is { } y
            ? x == y
            : string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
              && string.Equals(a.SetCode, b.SetCode, StringComparison.OrdinalIgnoreCase)
              && string.Equals(a.CollectorNumber, b.CollectorNumber, StringComparison.OrdinalIgnoreCase);
}
