namespace Kabatra.MagicTheGathering.Companion.Core.Cloud;

/// <summary>A playgroup. Its admin is a signed-in player's profile; the join code is only shown to the admin.</summary>
public sealed record Playgroup(Guid Id, string Name, Guid AdminProfileId);

/// <summary>
/// Playgroups are the sharing boundary: everyone in a group sees its players, their decks, and its play history.
/// Profiles and playgroups are many-to-many. Each group has one admin, who alone can rename, delete or hand
/// over the group, add or remove players, and see or reset its join code. Anyone can leave a group (except
/// the admin, who hands it over first). The rules live in supabase/schema.sql; these are the calls.
/// </summary>
public sealed class PlaygroupService(SupabaseClient supabase)
{
    private const string Columns = "id,name,admin_profile_id";

    /// <summary>The groups the signed-in user's profile is in.</summary>
    public Task<List<Playgroup>> ListMineAsync(CancellationToken ct = default) =>
        supabase.GetAsync<List<Playgroup>>($"playgroups?select={Columns}&order=name", ct);

    /// <summary>Makes a group with the signed-in user as its admin and first member.</summary>
    public Task<Playgroup> CreateAsync(string name, CancellationToken ct = default) =>
        supabase.RpcAsync<Playgroup>("create_playgroup", new { group_name = name.Trim() }, ct);

    /// <summary>Adds the signed-in user's profile to the group with this join code.</summary>
    public Task<Playgroup> JoinAsync(string joinCode, CancellationToken ct = default) =>
        supabase.RpcAsync<Playgroup>("join_playgroup", new { code = NormalizeCode(joinCode) }, ct);

    /// <summary>Admin: renames the group.</summary>
    public async Task<Playgroup> RenameAsync(Guid groupId, string name, CancellationToken ct = default)
    {
        var rows = await supabase.PatchAsync<Playgroup>($"playgroups?id=eq.{groupId}&select={Columns}", new { name = name.Trim() }, ct);
        return rows.FirstOrDefault() ?? throw NotAdmin();
    }

    /// <summary>Admin: deletes the group and its play history. Its players and their decks stay.</summary>
    public Task DeleteAsync(Guid groupId, CancellationToken ct = default) =>
        supabase.DeleteAsync($"playgroups?id=eq.{groupId}", ct);

    /// <summary>Admin: hands the group to another signed-in player in it.</summary>
    public Task<Playgroup> TransferAsync(Guid groupId, Guid newAdminProfileId, CancellationToken ct = default) =>
        supabase.RpcAsync<Playgroup>("transfer_playgroup", new { group_id = groupId, new_admin = newAdminProfileId }, ct);

    /// <summary>Admin: the join code to share; <paramref name="reset"/> issues a new one and the old one stops working.</summary>
    public Task<string> GetJoinCodeAsync(Guid groupId, bool reset = false, CancellationToken ct = default) =>
        supabase.RpcAsync<string>("playgroup_join_code", new { group_id = groupId, reset }, ct);

    /// <summary>Admin: adds someone's profile using the player code they shared.</summary>
    public async Task<Guid> AddByPlayerCodeAsync(Guid groupId, string playerCode, CancellationToken ct = default)
    {
        var row = await supabase.RpcAsync<IdOnly>("add_profile_to_playgroup", new { group_id = groupId, code = NormalizeCode(playerCode) }, ct);
        return row.Id;
    }

    /// <summary>Takes a profile out of the group: the admin removing anyone else, or a player leaving.</summary>
    public Task RemoveProfileAsync(Guid groupId, Guid profileId, CancellationToken ct = default) =>
        supabase.DeleteAsync($"playgroup_profiles?playgroup_id=eq.{groupId}&profile_id=eq.{profileId}", ct);

    private static string NormalizeCode(string code) => Profiles.ProfileAccount.NormalizeCode(code);

    private static SupabaseException NotAdmin() => new("Only the playgroup's admin can do that.", 403);

    private sealed record IdOnly(Guid Id);
}
