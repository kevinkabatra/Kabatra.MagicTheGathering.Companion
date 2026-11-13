using Kabatra.MagicTheGathering.Companion.Core;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class GameTests
{
    private static Game NewGame(GameFormat? format = null, params string[] names) =>
        Game.Start(format ?? GameFormat.Standard, names.Length == 0 ? ["Alice", "Bob"] : names);

    [Fact]
    public void Start_SetsStartingLifeFromFormat()
    {
        var game = NewGame(GameFormat.Commander, "A", "B", "C", "D");
        Assert.All(game.Players, p => Assert.Equal(40, p.Life));
        Assert.Equal(1, game.TurnNumber);
        Assert.Equal("A", game.ActivePlayer.Name);
    }

    [Fact]
    public void Start_FillsInBlankNames()
    {
        var game = NewGame(null, "  ", "Bob");
        Assert.Equal("Player 1", game.Players[0].Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Start_RejectsBadPlayerCounts(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Game.Start(GameFormat.Standard, Enumerable.Range(1, count).Select(i => $"P{i}")));
    }

    [Fact]
    public void AdjustLife_ChangesLifeAndCanGoNegative()
    {
        var game = NewGame();
        var alice = game.Players[0];
        game.AdjustLife(alice.Id, -25);
        Assert.Equal(-5, alice.Life);
        Assert.True(alice.IsEliminated(game.Format));
    }

    [Fact]
    public void Poison_CannotGoBelowZero_AndTenPoisonEliminates()
    {
        var game = NewGame();
        var bob = game.Players[1];
        game.AdjustCounter(bob.Id, CounterKind.Poison, -3);
        Assert.Equal(0, bob.Poison);

        game.AdjustCounter(bob.Id, CounterKind.Poison, 10);
        Assert.True(bob.IsEliminated(game.Format));
        Assert.Equal("Alice", game.Winner?.Name);
    }

    [Fact]
    public void CommanderDamage_AlsoReducesLife_AndTwentyOneEliminates()
    {
        var game = NewGame(GameFormat.Commander, "A", "B", "C");
        var (a, b) = (game.Players[0], game.Players[1]);

        game.AdjustCommanderDamage(b.Id, a.Id, 21);

        Assert.Equal(21, b.CommanderDamageFrom(a.Id));
        Assert.Equal(19, b.Life);
        Assert.True(b.IsEliminated(game.Format));
        Assert.Null(game.Winner); // C is still alive
    }

    [Fact]
    public void CommanderDamage_FromDifferentCommandersIsTrackedSeparately()
    {
        var game = NewGame(GameFormat.Commander, "A", "B", "C");
        var (a, b, c) = (game.Players[0], game.Players[1], game.Players[2]);

        game.AdjustCommanderDamage(c.Id, a.Id, 15);
        game.AdjustCommanderDamage(c.Id, b.Id, 15);

        Assert.False(c.IsEliminated(game.Format));
        Assert.Equal(10, c.Life);
    }

    [Fact]
    public void CommanderDamage_CanSkipLifeAdjustment()
    {
        var game = NewGame(GameFormat.Commander);
        var (a, b) = (game.Players[0], game.Players[1]);
        game.AdjustCommanderDamage(b.Id, a.Id, 5, alsoAdjustLife: false);
        Assert.Equal(40, b.Life);
    }

    [Fact]
    public void CommanderDamage_IgnoredForLossInFormatsWithoutIt()
    {
        var game = NewGame(GameFormat.Custom(100, tracksCommanderDamage: false));
        var (a, b) = (game.Players[0], game.Players[1]);
        game.AdjustCommanderDamage(b.Id, a.Id, 30);
        Assert.False(b.IsEliminated(game.Format));
    }

    [Fact]
    public void NextTurn_SkipsEliminatedPlayers_AndCountsTurns()
    {
        var game = NewGame(null, "A", "B", "C");
        game.AdjustLife(game.Players[1].Id, -20);

        game.NextTurn();

        Assert.Equal("C", game.ActivePlayer.Name);
        Assert.Equal(2, game.TurnNumber);
        game.NextTurn();
        Assert.Equal("A", game.ActivePlayer.Name);
    }

    [Fact]
    public void Concede_ByActivePlayer_PassesTheTurn()
    {
        var game = NewGame(null, "A", "B", "C");
        game.SetConceded(game.Players[0].Id, true);
        Assert.Equal("B", game.ActivePlayer.Name);
    }

    [Fact]
    public void Undo_RestoresPreviousState()
    {
        var game = NewGame();
        var aliceId = game.Players[0].Id;
        game.AdjustLife(aliceId, -3);
        game.NextTurn();

        Assert.True(game.Undo());
        Assert.Equal(1, game.TurnNumber);
        Assert.True(game.Undo());
        Assert.Equal(20, game.GetPlayer(aliceId).Life);
        Assert.False(game.Undo());
    }

    [Fact]
    public void Log_MergesRepeatedChangesToSameCounter()
    {
        var game = NewGame();
        var aliceId = game.Players[0].Id;
        var before = game.Log.Count;

        for (var i = 0; i < 5; i++) game.AdjustLife(aliceId, -1);

        Assert.Equal(before + 1, game.Log.Count);
        Assert.Equal("Alice: life -5 (20 → 15)", game.Log[^1].Message);
    }

    [Fact]
    public void Log_DropsEntryWhenChangesCancelOut()
    {
        var game = NewGame();
        var aliceId = game.Players[0].Id;
        var before = game.Log.Count;

        game.AdjustLife(aliceId, -1);
        game.AdjustLife(aliceId, +1);

        Assert.Equal(before, game.Log.Count);
    }

    [Fact]
    public void Log_ReportsEliminationOnceAndWinner()
    {
        var game = NewGame();
        var bobId = game.Players[1].Id;
        game.AdjustLife(bobId, -20);
        game.AdjustLife(bobId, -1);

        Assert.Single(game.Log, e => e.Message == "Bob is out of the game.");
        Assert.Contains(game.Log, e => e.Message == "Alice wins!");
    }

    [Fact]
    public void Json_RoundTripsFullState()
    {
        var game = NewGame(GameFormat.Commander, "A", "B");
        var (a, b) = (game.Players[0], game.Players[1]);
        game.AdjustCommanderDamage(b.Id, a.Id, 7);
        game.AdjustCounter(a.Id, CounterKind.Energy, 3);
        game.NextTurn();

        var restored = Game.FromJson(game.ToJson());

        Assert.Equal(GameFormat.Commander, restored.Format);
        Assert.Equal(2, restored.TurnNumber);
        Assert.Equal("B", restored.ActivePlayer.Name);
        Assert.Equal(7, restored.GetPlayer(b.Id).CommanderDamageFrom(a.Id));
        Assert.Equal(33, restored.GetPlayer(b.Id).Life);
        Assert.Equal(3, restored.GetPlayer(a.Id).Energy);
        Assert.Equal(game.Log.Count, restored.Log.Count);
        Assert.False(restored.CanUndo);
    }
}

public class RandomizerTests
{
    [Fact]
    public void RollDie_StaysInRange()
    {
        var r = new Randomizer(new Random(42));
        for (var i = 0; i < 1000; i++) Assert.InRange(r.RollDie(20), 1, 20);
    }

    [Fact]
    public void RollDie_RejectsFewerThanTwoSides()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Randomizer().RollDie(1));
    }

    [Fact]
    public void FlipCoin_ProducesBothSides()
    {
        var r = new Randomizer(new Random(1));
        var flips = Enumerable.Range(0, 100).Select(_ => r.FlipCoin()).ToList();
        Assert.Contains(true, flips);
        Assert.Contains(false, flips);
    }
}
