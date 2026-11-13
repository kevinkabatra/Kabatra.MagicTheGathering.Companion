namespace Kabatra.MagicTheGathering.Companion.Core;

/// <summary>
/// Who sits down at the start of a game, optionally which player profile they are, and which deck they brought.
/// Any deck can be played by anyone, so the deck doesn't have to belong to the profile.
/// </summary>
public sealed record PlayerSeat(string Name, Guid? DeckId = null, string? DeckName = null, string? DeckImageUrl = null, Guid? ProfileId = null);
