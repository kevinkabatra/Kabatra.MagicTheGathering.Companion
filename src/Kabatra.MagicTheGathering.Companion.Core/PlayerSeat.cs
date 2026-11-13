namespace Kabatra.MagicTheGathering.Companion.Core;

/// <summary>Who sits down at the start of a game, and optionally which deck they brought.</summary>
public sealed record PlayerSeat(string Name, Guid? DeckId = null, string? DeckName = null, string? DeckImageUrl = null);
