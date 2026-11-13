namespace Kabatra.MagicTheGathering.Companion.Core;

/// <summary>
/// One line in the game history. Consecutive changes to the same counter are merged
/// into a single entry so that tapping "-1" five times reads as one "-5".
/// </summary>
public sealed class GameLogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public int Turn { get; set; }
    public string Message { get; set; } = "";

    // Merge key fields (null for non-counter events such as "next turn").
    public Guid? PlayerId { get; set; }
    public CounterKind? Counter { get; set; }
    public Guid? SourcePlayerId { get; set; }
    public int? From { get; set; }
    public int? To { get; set; }

    // For commander damage that also caused life loss.
    public int? LifeFrom { get; set; }
    public int? LifeTo { get; set; }
}
