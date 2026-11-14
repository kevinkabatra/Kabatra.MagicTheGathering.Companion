using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class BackupFileTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T20:00:00Z");

    private static Deck Goblins()
    {
        var deck = new Deck { Name = "Goblin Gang", OwnerName = "Sam", Format = DeckFormat.Commander.Id, Notes = "Go wide", OwnerProfileId = Guid.NewGuid() };
        deck.Add(TestCards.Krenko, DeckSection.Commander);
        deck.Add(TestCards.Bolt, DeckSection.Main, 1);
        return deck;
    }

    [Fact]
    public void RoundTrip_KeepsDecksAndTheCollection()
    {
        var deck = Goblins();
        var collection = new List<DeckCard> { new() { Name = "Sol Ring", Quantity = 2, SetCode = "c21", CollectorNumber = "263" } };

        var file = BackupFile.Read(BackupFile.Write([deck], collection, Now));

        var restored = file.Decks.Single();
        Assert.Equal("Goblin Gang", restored.Name);
        Assert.Equal("Sam", restored.OwnerName);
        Assert.Equal("Go wide", restored.Notes);
        Assert.Equal("Krenko, Mob Boss", restored.Commanders.Single().Name);
        Assert.Equal(1, restored.Count(DeckSection.Main));
        Assert.Equal(2, file.Collection!.Single().Quantity);
        Assert.Equal(Now, file.ExportedAt);
        Assert.Equal("mtg-decks-2026-11-13.json", BackupFile.FileName(Now));
    }

    [Fact]
    public void AGuestsBackupHasNoCollection()
    {
        var file = BackupFile.Read(BackupFile.Write([Goblins()], null, Now));
        Assert.Null(file.Collection);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "some": "other file" }""")]
    [InlineData("[1, 2, 3]")]
    public void OtherFilesAreRefused(string json) =>
        Assert.Throws<BackupException>(() => BackupFile.Read(json));

    [Fact]
    public void BackupsFromANewerVersionAreRefused()
    {
        var json = BackupFile.Write([Goblins()], null, Now).Replace("\"version\": 1", "\"version\": 99");
        var error = Assert.Throws<BackupException>(() => BackupFile.Read(json));
        Assert.Contains("newer version", error.Message);
    }

    [Fact]
    public void Restoring_GivesNewIds_DropsTheOwnerProfile_AndSkipsDecksAlreadyThere()
    {
        var goblins = Goblins();
        var burn = new Deck { Name = "Burn", OwnerProfileId = Guid.NewGuid(), CardsInCollection = true };
        burn.Add(TestCards.Bolt, DeckSection.Main, 4);
        var file = BackupFile.Read(BackupFile.Write([goblins, burn, goblins], null, Now));

        // Goblins is already on this device (same name and cards); the copy in the file is listed twice.
        var toRestore = file.DecksToRestore([goblins]);

        var restored = Assert.Single(toRestore);
        Assert.Equal("Burn", restored.Name);
        Assert.NotEqual(burn.Id, restored.Id); // never overwrites a deck
        Assert.Null(restored.OwnerProfileId);  // goes to whoever restores it
        Assert.False(restored.CardsInCollection); // so its cards can go into their collection
    }
}
