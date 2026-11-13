using Kabatra.MagicTheGathering.Companion.Core.Cloud;

namespace Kabatra.MagicTheGathering.Companion.Core.Profiles;

/// <summary>
/// Where a signed-in user stands: their profile if they have one, and if not, whether there are unclaimed
/// profiles they could claim and how many wrong claim codes they can still enter.
/// </summary>
public sealed record OnboardingState(
    Guid? ProfileId,
    bool CanClaim,
    bool Blocked,
    DateTimeOffset? LockedUntil,
    int AttemptsLeft)
{
    public bool HasProfile => ProfileId is not null;
}

public enum ClaimStatus
{
    /// <summary>The profile is now the user's.</summary>
    Claimed,

    /// <summary>No unclaimed profile has that code. <see cref="ClaimResult.AttemptsLeft"/> says how many tries remain.</summary>
    WrongCode,

    /// <summary>Too many wrong codes in a row; no more tries until <see cref="ClaimResult.LockedUntil"/>.</summary>
    Locked,

    /// <summary>Too many wrong codes in all; the account is blocked.</summary>
    Blocked,
}

public sealed record ClaimResult(ClaimStatus Status, Guid? ProfileId = null, int AttemptsLeft = 0, DateTimeOffset? LockedUntil = null);

/// <summary>
/// The signed-in user's own profile: making one on first sign-in, or claiming one someone made for them.
/// The rules (one profile per user, single-use claim codes, wrong-code limits) live in the database
/// functions in supabase/schema.sql, so they hold whatever the browser does.
/// </summary>
public sealed class ProfileAccount(SupabaseClient supabase)
{
    /// <summary>
    /// Wrong claim codes allowed in a row before a <see cref="LockoutMinutes"/>-minute wait, and in all before
    /// the account is blocked. These mirror claim_profile in supabase/schema.sql, which enforces them.
    /// </summary>
    public const int StrikesBeforeLockout = 3, LockoutMinutes = 15, StrikesBeforeBlock = 10;

    public Task<OnboardingState> GetStateAsync(CancellationToken ct = default) =>
        supabase.RpcAsync<OnboardingState>("onboarding_state", new { }, ct);

    /// <summary>The user's own profile, or null if they haven't made or claimed one.</summary>
    public async Task<PlayerProfile?> GetMineAsync(CancellationToken ct = default)
    {
        if (supabase.Session?.UserId is not { } me) return null;
        var rows = await supabase.GetAsync<List<SupabaseProfileStore.ProfileRow>>(
            $"player_profiles?select={SupabaseProfileStore.Columns}&user_id=eq.{me}", ct);
        return rows.FirstOrDefault()?.ToProfile();
    }

    /// <summary>Makes the user's own profile. With no name, it's named after their sign-in account.</summary>
    public async Task<PlayerProfile> CreateMineAsync(string? name = null, CancellationToken ct = default)
    {
        var row = await supabase.RpcAsync<SupabaseProfileStore.ProfileRow>(
            "create_my_profile", new { profile_name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() }, ct);
        return row.ToProfile();
    }

    /// <summary>Tries to claim an unclaimed profile with its claim code. Wrong codes count toward the limits.</summary>
    public Task<ClaimResult> ClaimAsync(string code, CancellationToken ct = default) =>
        supabase.RpcAsync<ClaimResult>("claim_profile", new { code = NormalizeCode(code) }, ct);

    /// <summary>
    /// The claim code for an unclaimed profile the user manages, made the first time it's asked for.
    /// <paramref name="reset"/> replaces it, so a code that got out stops working.
    /// </summary>
    public Task<string> GetClaimCodeAsync(Guid profileId, bool reset = false, CancellationToken ct = default) =>
        supabase.RpcAsync<string>("profile_claim_code", new { profile = profileId, reset }, ct);

    /// <summary>Codes are typed by people: ignore case, spaces and dashes (the database does the same).</summary>
    public static string NormalizeCode(string? code) =>
        new((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());
}
