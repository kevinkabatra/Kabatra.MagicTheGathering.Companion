namespace Kabatra.MagicTheGathering.Companion.Core.Cloud;

public sealed record Playgroup(Guid Id, string Name, string InviteCode, Guid? CreatedBy);

public sealed record PlaygroupMember(Guid PlaygroupId, Guid UserId, string? DisplayName);

/// <summary>
/// Playgroups are the sharing boundary: everyone in a group can see and edit its decks.
/// Creating and joining go through database functions (see supabase/schema.sql) because a
/// non-member isn't allowed to read a group directly.
/// </summary>
public sealed class PlaygroupService(SupabaseClient supabase)
{
    public Task<List<Playgroup>> ListMineAsync(CancellationToken ct = default) =>
        supabase.GetAsync<List<Playgroup>>("playgroups?select=id,name,invite_code,created_by&order=name", ct);

    public Task<List<PlaygroupMember>> ListMembersAsync(Guid groupId, CancellationToken ct = default) =>
        supabase.GetAsync<List<PlaygroupMember>>($"playgroup_members?select=playgroup_id,user_id,display_name&playgroup_id=eq.{groupId}&order=display_name", ct);

    public Task<Playgroup> CreateAsync(string name, string displayName, CancellationToken ct = default) =>
        supabase.RpcAsync<Playgroup>("create_playgroup", new { group_name = name.Trim(), display_name = displayName.Trim() }, ct);

    public Task<Playgroup> JoinAsync(string inviteCode, string displayName, CancellationToken ct = default) =>
        supabase.RpcAsync<Playgroup>("join_playgroup", new { code = inviteCode.Trim().ToUpperInvariant(), display_name = displayName.Trim() }, ct);

    /// <summary>Issues a new invite code (creator only) and returns it.</summary>
    public Task<string> ResetInviteCodeAsync(Guid groupId, CancellationToken ct = default) =>
        supabase.RpcAsync<string>("reset_invite_code", new { group_id = groupId }, ct);

    public Task LeaveAsync(Guid groupId, CancellationToken ct = default) =>
        supabase.DeleteAsync($"playgroup_members?playgroup_id=eq.{groupId}&user_id=eq.{supabase.Session?.UserId}", ct);
}
