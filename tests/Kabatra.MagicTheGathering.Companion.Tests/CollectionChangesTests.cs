using Kabatra.MagicTheGathering.Companion.Core.Decks;
using Kabatra.MagicTheGathering.Companion.Core.Profiles;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class CollectionChangesTests
{
    private static PlayerProfile Starter()
    {
        var profile = new PlayerProfile { Name = "Sam" };
        profile.AddToCollection(TestCards.Bolt);
        profile.AddToCollection(TestCards.Mountain, 4);
        return profile;
    }

    private static DeckCard Printing(string name, string set, string number, int quantity = 1) =>
        new() { Name = name, SetCode = set, CollectorNumber = number, Quantity = quantity };

    [Fact]
    public void NoChanges_IsEmpty()
    {
        var profile = Starter();
        Assert.Empty(CollectionSnapshot.Of(profile).ChangesIn(profile));
    }

    [Fact]
    public void AddingTheSamePrintingTwice_ShowsOneLineWithBothCopies()
    {
        var profile = Starter();
        var snapshot = CollectionSnapshot.Of(profile);
        profile.AddToCollection(TestCards.Counterspell);
        profile.AddToCollection(TestCards.Counterspell);

        var change = Assert.Single(snapshot.ChangesIn(profile));
        Assert.Equal(("Counterspell", 2), (change.Name, change.Delta));
    }

    [Fact]
    public void DifferentPrintingsOfOneCard_AreListedSeparately()
    {
        var profile = new PlayerProfile();
        profile.AddToCollection(Printing("Lightning Bolt", "m10", "146", 2));
        var snapshot = CollectionSnapshot.Of(profile);

        profile.AddToCollection(Printing("Lightning Bolt", "fdc", "163", 3));
        profile.SetQuantity(profile.Collection[0], 1);

        var changes = snapshot.ChangesIn(profile);
        Assert.Equal(
            [("FDC #163", 3), ("M10 #146", -1)],
            changes.Select(c => (c.Printing, c.Delta)).ToList());
    }

    [Fact]
    public void RemovingALine_ShowsWhatWasRemoved()
    {
        var profile = Starter();
        var snapshot = CollectionSnapshot.Of(profile);
        profile.SetQuantity(profile.Collection.Single(c => c.Name == "Mountain"), 0);

        var change = Assert.Single(snapshot.ChangesIn(profile));
        Assert.Equal(("Mountain", -4, false), (change.Name, change.Delta, change.IsAddition));
    }

    [Fact]
    public void UndoingAChange_ClearsIt()
    {
        var profile = Starter();
        var snapshot = CollectionSnapshot.Of(profile);
        var bolt = profile.Collection.Single(c => c.Name == "Lightning Bolt");
        profile.SetQuantity(bolt, 5);
        profile.SetQuantity(bolt, 1);

        Assert.Empty(snapshot.ChangesIn(profile));
    }

    [Fact]
    public void Restore_UndoesCardChangesAndTheName()
    {
        var profile = Starter();
        var original = profile.Collection.Select(c => (c.Name, c.Quantity)).ToList();
        var snapshot = CollectionSnapshot.Of(profile);

        profile.AddToCollection(TestCards.Counterspell, 2);
        profile.SetQuantity(profile.Collection.Single(c => c.Name == "Mountain"), 0);
        profile.SetQuantity(profile.Collection.Single(c => c.Name == "Lightning Bolt"), 3);
        profile.Name = "Samantha";

        snapshot.Restore(profile);

        Assert.Equal(original, profile.Collection.Select(c => (c.Name, c.Quantity)).ToList());
        Assert.Equal("Sam", profile.Name);
        Assert.Empty(snapshot.ChangesIn(profile));
    }

    [Fact]
    public void Printing_IsEmptyWhenUnknown()
    {
        Assert.Equal("", new CollectionChange("Island", null, null, 1).Printing);
        Assert.Equal("LEA", new CollectionChange("Island", "lea", null, 1).Printing);
    }
}
