using Kabatra.MagicTheGathering.Companion.Core.Cloud;

namespace Kabatra.MagicTheGathering.Companion.Services;

/// <summary>
/// The one place that decides whether a player's own decks are kept in Supabase (and so follow them to every
/// device) or stay on this device. <see cref="DeckLibrary"/> asks this instead of checking sign-in itself, so a
/// later rule (for example, an entitlement for syncing across devices) only has to change <see cref="CanSync"/>.
/// <para>
/// Playgroup data (who's in a group, its play history, profiles and claiming) doesn't go through this: it always
/// lives in Supabase, because playgroups are free for everyone who signs in. Today a signed-in player's decks are
/// also how their playgroups see them, so a rule that turns sync off for someone would first need decks to be
/// shared with a group separately from being synced.
/// </para>
/// </summary>
public sealed class SyncPolicy(SupabaseClient supabase)
{
    /// <summary>Whether the player's own decks are kept in Supabase. For now, everyone who signs in.</summary>
    public bool CanSync => supabase.IsSignedIn;
}
