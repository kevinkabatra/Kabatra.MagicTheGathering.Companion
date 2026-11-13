namespace Kabatra.MagicTheGathering.Companion.Core;

/// <summary>
/// Describes the starting conditions and loss conditions for a game.
/// </summary>
public sealed record GameFormat(
    string Name,
    int StartingLife,
    bool TracksCommanderDamage,
    int CommanderDamageToLose = 21,
    int PoisonToLose = 10)
{
    public static GameFormat Standard { get; } = new("Standard / 60-card", 20, false);
    public static GameFormat Commander { get; } = new("Commander", 40, true);
    public static GameFormat Brawl { get; } = new("Brawl", 25, false);
    public static GameFormat Oathbreaker { get; } = new("Oathbreaker", 20, false);

    public static IReadOnlyList<GameFormat> Presets { get; } = [Standard, Commander, Brawl, Oathbreaker];

    public static GameFormat Custom(int startingLife, bool tracksCommanderDamage) =>
        new("Custom", startingLife, tracksCommanderDamage);
}
