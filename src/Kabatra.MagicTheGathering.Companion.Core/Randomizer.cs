namespace Kabatra.MagicTheGathering.Companion.Core;

/// <summary>Dice, coins, and random picks. Accepts a seeded <see cref="Random"/> for tests.</summary>
public sealed class Randomizer(Random? random = null)
{
    private readonly Random _random = random ?? Random.Shared;

    public int RollDie(int sides)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 2);
        return _random.Next(1, sides + 1);
    }

    /// <returns>True for heads, false for tails.</returns>
    public bool FlipCoin() => _random.Next(2) == 0;

    public T Pick<T>(IReadOnlyList<T> items)
    {
        if (items.Count == 0) throw new ArgumentException("Cannot pick from an empty list.", nameof(items));
        return items[_random.Next(items.Count)];
    }
}
