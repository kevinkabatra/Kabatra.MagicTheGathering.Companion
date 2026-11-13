namespace Kabatra.MagicTheGathering.Companion.Core;

/// <summary>Plain, serializable snapshot of everything about a game in progress.</summary>
public sealed class GameState
{
    /// <summary>Identifies the game in play history. Saves from before this existed get a new id when loaded.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public GameFormat Format { get; set; } = GameFormat.Standard;
    public List<Player> Players { get; set; } = [];
    public int ActivePlayerIndex { get; set; }
    public int TurnNumber { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; }
    public List<GameLogEntry> Log { get; set; } = [];
}
