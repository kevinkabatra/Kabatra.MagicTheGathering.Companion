using Kabatra.MagicTheGathering.Companion.Core.Cloud;
using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Core.Profiles;

/// <summary>The player profiles in a playgroup.</summary>
public interface IProfileStore
{
    Task<IReadOnlyList<PlayerProfile>> ListAsync(CancellationToken ct = default);

    /// <summary>Any profile the user can see, whether or not it's in this playgroup.</summary>
    Task<PlayerProfile?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Makes an unclaimed profile in the playgroup (playgroup admin only) and returns it.</summary>
    Task<PlayerProfile> CreateAsync(string name, CancellationToken ct = default);

    /// <summary>Saves an existing profile's name and collection, and returns it as stored.</summary>
    Task<PlayerProfile> SaveAsync(PlayerProfile profile, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Profiles in a playgroup, stored in the Supabase "player_profiles" table and linked to playgroups through
/// "playgroup_profiles". Who may change what is decided by row-level security (see supabase/schema.sql).
/// </summary>
public sealed class SupabaseProfileStore(SupabaseClient supabase, Playgroup group) : IProfileStore
{
    /// <summary>Readable columns. The claim code isn't one: only its admin can read it, with profile_claim_code().</summary>
    internal const string Columns = "id,name,user_id,created_by,player_code,collection,updated_at";

    public async Task<IReadOnlyList<PlayerProfile>> ListAsync(CancellationToken ct = default)
    {
        var rows = await supabase.GetAsync<List<ProfileRow>>(
            $"player_profiles?select={Columns},playgroup_profiles!inner(playgroup_id)&playgroup_profiles.playgroup_id=eq.{group.Id}&order=name", ct);
        return rows.Select(r => r.ToProfile()).ToList();
    }

    public async Task<PlayerProfile?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await supabase.GetAsync<List<ProfileRow>>($"player_profiles?select={Columns}&id=eq.{id}", ct);
        return rows.FirstOrDefault()?.ToProfile();
    }

    public async Task<PlayerProfile> CreateAsync(string name, CancellationToken ct = default)
    {
        var row = await supabase.RpcAsync<ProfileRow>("create_profile", new { profile_name = name.Trim(), group_id = group.Id }, ct);
        return row.ToProfile();
    }

    public async Task<PlayerProfile> SaveAsync(PlayerProfile profile, CancellationToken ct = default)
    {
        var changes = new
        {
            name = profile.Name.Trim(),
            collection = profile.Collection.Select(SupabaseDeckStore.CardRow.From).ToList(),
        };
        var rows = await supabase.PatchAsync<ProfileRow>($"player_profiles?id=eq.{profile.Id}&select={Columns}", changes, ct);
        // Row-level security hides rows the user can't change, so nothing coming back means "not allowed".
        return rows.FirstOrDefault()?.ToProfile()
               ?? throw new SupabaseException("Only the player (or whoever made the profile, until it's claimed) can change it.", 403);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => supabase.DeleteAsync($"player_profiles?id=eq.{id}", ct);

    /// <summary>The database row. The collection is a JSON array in one column, in the same shape as a deck's cards.</summary>
    internal sealed record ProfileRow(
        Guid Id,
        string Name,
        Guid? UserId,
        Guid? CreatedBy,
        string? PlayerCode,
        List<SupabaseDeckStore.CardRow>? Collection,
        DateTimeOffset UpdatedAt)
    {
        public PlayerProfile ToProfile() => new()
        {
            Id = Id,
            Name = Name,
            UserId = UserId,
            CreatedBy = CreatedBy,
            PlayerCode = PlayerCode ?? "",
            UpdatedAt = UpdatedAt,
            Collection = (Collection ?? []).Select(c => c.ToDeckCard()).ToList(),
        };
    }
}
