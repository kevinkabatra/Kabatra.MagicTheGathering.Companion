using Kabatra.MagicTheGathering.Companion.Core;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class GameDeckTests
{
    [Fact]
    public void Start_WithSeats_RecordsDecksAndLogsThem()
    {
        var deckId = Guid.NewGuid();
        var game = Game.Start(GameFormat.Commander,
        [
            new PlayerSeat("Sam", deckId, "Goblin Gang", "https://img/krenko.jpg"),
            new PlayerSeat("Alex"),
        ]);

        var sam = game.Players[0];
        Assert.Equal((deckId, "Goblin Gang", "https://img/krenko.jpg"), (sam.DeckId, sam.DeckName, sam.DeckImageUrl));
        Assert.Null(game.Players[1].DeckName);
        Assert.Contains(game.Log, e => e.Message == "Sam is playing Goblin Gang.");
    }

    [Fact]
    public void DeckChoices_SurviveSaveAndLoad()
    {
        var game = Game.Start(GameFormat.Commander, [new PlayerSeat("Sam", Guid.NewGuid(), "Goblin Gang"), new PlayerSeat("Alex")]);
        var restored = Game.FromJson(game.ToJson());
        Assert.Equal("Goblin Gang", restored.Players[0].DeckName);
        Assert.Equal(game.Players[0].DeckId, restored.Players[0].DeckId);
    }
}
