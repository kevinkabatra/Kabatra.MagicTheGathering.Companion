using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class DeckChangesTests
{
    private static Deck Starter()
    {
        var deck = new Deck();
        deck.Add(TestCards.Krenko, DeckSection.Commander);
        deck.Add(TestCards.Mountain, quantity: 10);
        deck.Add(TestCards.Bolt);
        return deck;
    }

    [Fact]
    public void NoChanges_IsEmpty()
    {
        var deck = Starter();
        Assert.Empty(DeckSnapshot.Of(deck).ChangesIn(deck));
    }

    [Fact]
    public void AddingTheSameCardTwice_ShowsOneLineWithBothCopies()
    {
        var deck = Starter();
        var snapshot = DeckSnapshot.Of(deck);
        deck.Add(TestCards.Counterspell);
        deck.Add(TestCards.Counterspell);
        deck.Add(TestCards.Bolt);

        Assert.Equal(
            [new DeckChange("Counterspell", DeckSection.Main, 2), new DeckChange("Lightning Bolt", DeckSection.Main, 1)],
            snapshot.ChangesIn(deck));
    }

    [Fact]
    public void RemovingCards_ShowsWhatWasRemoved()
    {
        var deck = Starter();
        var snapshot = DeckSnapshot.Of(deck);
        deck.SetQuantity(deck.Cards.Single(c => c.Name == "Lightning Bolt"), 0);
        deck.SetQuantity(deck.Cards.Single(c => c.Name == "Mountain"), 7);

        var changes = snapshot.ChangesIn(deck);

        Assert.Equal([new DeckChange("Lightning Bolt", DeckSection.Main, -1), new DeckChange("Mountain", DeckSection.Main, -3)], changes);
        Assert.All(changes, c => Assert.False(c.IsAddition));
    }

    [Fact]
    public void MovingCards_ShowsRemovedFromOneSectionAndAddedToAnother()
    {
        var deck = Starter();
        var snapshot = DeckSnapshot.Of(deck);
        deck.Move(deck.Cards.Single(c => c.Name == "Lightning Bolt"), DeckSection.Sideboard);

        Assert.Equal(
            [new DeckChange("Lightning Bolt", DeckSection.Main, -1), new DeckChange("Lightning Bolt", DeckSection.Sideboard, 1)],
            snapshot.ChangesIn(deck));
    }

    [Fact]
    public void UndoingAChange_ClearsIt()
    {
        var deck = Starter();
        var snapshot = DeckSnapshot.Of(deck);
        var bolt = deck.Cards.Single(c => c.Name == "Lightning Bolt");
        deck.SetQuantity(bolt, 3);
        deck.SetQuantity(bolt, 1);

        Assert.Empty(snapshot.ChangesIn(deck));
    }

    [Fact]
    public void RenamingALineWhenLookedUp_IsNotAChange()
    {
        var deck = new Deck { Cards = [new DeckCard { Name = "lightning bolt", Quantity = 2 }] };
        var snapshot = DeckSnapshot.Of(deck);
        deck.Cards[0].Name = "Lightning Bolt";

        Assert.Empty(snapshot.ChangesIn(deck));
    }

    [Fact]
    public void SeveralPrintingsOfOneName_AreNettedTogether()
    {
        var deck = new Deck
        {
            Cards =
            [
                new DeckCard { Name = "Island", Quantity = 3, ScryfallId = Guid.NewGuid() },
                new DeckCard { Name = "Island", Quantity = 2, ScryfallId = Guid.NewGuid() },
            ],
        };
        var snapshot = DeckSnapshot.Of(deck);
        deck.SetQuantity(deck.Cards[1], 0);

        Assert.Equal([new DeckChange("Island", DeckSection.Main, -2)], snapshot.ChangesIn(deck));
    }

    [Fact]
    public void Restore_UndoesAddsRemovesMovesAndDetails()
    {
        var deck = Starter();
        deck.Name = "Goblins";
        var original = deck.Cards.Select(c => (c.Name, c.Quantity, c.Section)).ToList();
        var snapshot = DeckSnapshot.Of(deck);

        deck.Add(TestCards.Counterspell);
        deck.SetQuantity(deck.Cards.Single(c => c.Name == "Mountain"), 0);
        deck.Move(deck.Cards.Single(c => c.Name == "Lightning Bolt"), DeckSection.Sideboard);
        deck.Name = "Something else";
        deck.Format = DeckFormat.Modern.Id;

        snapshot.Restore(deck);

        Assert.Equal(original, deck.Cards.Select(c => (c.Name, c.Quantity, c.Section)).ToList());
        Assert.Equal("Goblins", deck.Name);
        Assert.Equal(DeckFormat.Commander.Id, deck.Format);
        Assert.Empty(snapshot.ChangesIn(deck));
    }

    [Fact]
    public void Restore_UndoesAMoveThatMergedIntoAnotherLine()
    {
        var deck = Starter();
        deck.Add(TestCards.Bolt, DeckSection.Sideboard, 2);
        var snapshot = DeckSnapshot.Of(deck);

        deck.Move(deck.Cards.Single(c => c.Name == "Lightning Bolt" && c.Section == DeckSection.Main), DeckSection.Sideboard);
        Assert.Equal(3, deck.Count(DeckSection.Sideboard));

        snapshot.Restore(deck);

        Assert.Equal((1, 2), (deck.In(DeckSection.Main).Single(c => c.Name == "Lightning Bolt").Quantity, deck.Count(DeckSection.Sideboard)));
    }
}
