using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;
using Kabatra.MagicTheGathering.Companion.Core.Decks;
using Kabatra.MagicTheGathering.Companion.Core.History;
using Kabatra.MagicTheGathering.Companion.Core.Profiles;
using Microsoft.AspNetCore.Components;

namespace Kabatra.MagicTheGathering.Companion.Services;

/// <summary>
/// Tracks the signed-in user and their playgroups, and decides where decks, player profiles and play history
/// are read and saved. Everything needs a signed-in user: sign-in goes through an OAuth provider (see
/// <see cref="AuthProviders"/>), never a password the app handles.
/// </summary>
public sealed class DeckLibrary(
    SupabaseClient supabase,
    PlaygroupService playgroups,
    ProfileAccount account,
    IKeyValueStore storage,
    NavigationManager nav,
    IReadOnlyList<AuthProvider> providers)
{
    private const string GroupKey = "mtg-companion.playgroup";
    private const string MyProfileKey = "mtg-companion.my-profile";

    private Task? _init;

    /// <summary>False when appsettings.json has no usable Supabase project; the app can't be used then.</summary>
    public bool Configured => supabase.IsConfigured;

    /// <summary>The sign-in buttons to offer, in order.</summary>
    public IReadOnlyList<AuthProvider> Providers => providers;
    public bool SignedIn => supabase.IsSignedIn;
    public string? Email => supabase.Session?.Email;
    public IReadOnlyList<Playgroup> Groups { get; private set; } = [];
    public Playgroup? CurrentGroup { get; private set; }
    public bool Ready { get; private set; }

    /// <summary>The signed-in user's own profile. Null until they've made or claimed one (see <see cref="Onboarding"/>).</summary>
    public PlayerProfile? MyProfile { get; private set; }

    /// <summary>
    /// Set when the signed-in user has no profile yet and has a choice to make: start a new profile or claim
    /// an existing one. Null once they have a profile, or if their account couldn't be loaded (see <see cref="Error"/>).
    /// </summary>
    public OnboardingState? Onboarding { get; private set; }

    /// <summary>The signed-in user's id.</summary>
    public Guid? UserId => supabase.Session?.UserId;

    /// <summary>Whether the signed-in user may rename, delete or edit this profile (see <see cref="PlayerProfile.CanBeManagedBy"/>).</summary>
    public bool CanManage(PlayerProfile profile) => profile.CanBeManagedBy(UserId);

    /// <summary>A problem to show the user, e.g. an expired sign-in link.</summary>
    public string? Error { get; set; }

    /// <summary>Where decks are read and saved right now, or null if the user still has to sign in or pick a group.</summary>
    public IDeckStore? Store => CurrentGroup is { } group && SignedIn && MyProfile is { } me ? new SupabaseDeckStore(supabase, group, me.Id) : null;

    /// <summary>Where player profiles are read and saved right now; null in the same cases as <see cref="Store"/>.</summary>
    public IProfileStore? Profiles => CurrentGroup is { } group && SignedIn ? new SupabaseProfileStore(supabase, group) : null;

    /// <summary>Where finished games are read and saved right now; null in the same cases as <see cref="Store"/>.</summary>
    public IGameHistoryStore? History => CurrentGroup is { } group && SignedIn ? new SupabaseGameHistoryStore(supabase, group) : null;

    /// <summary>
    /// The playgroup a game finished now belongs to. Without a current group (e.g. offline) it's the last group
    /// used here, or <see cref="Guid.Empty"/> ("whichever group is used next") if there's none.
    /// </summary>
    public async Task<Guid?> HistoryGroupAsync()
    {
        if (CurrentGroup is { } group) return group.Id;
        return Guid.TryParse(await storage.GetAsync(GroupKey), out var saved) ? saved : Guid.Empty;
    }

    public event Action? Changed;

    /// <summary>Restores the session, finishes a sign-in link if the URL carries one, and loads playgroups.</summary>
    public Task InitializeAsync() => _init ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            if (!Configured) return;
            supabase.SessionChanged += () =>
            {
                // e.g. the refresh token expired mid-session
                if (!SignedIn) ForgetAccount();
                Changed?.Invoke();
            };
            await supabase.InitializeAsync();

            var uri = new Uri(nav.Uri);
            try
            {
                if (await supabase.TryCompleteSignInAsync(uri))
                    nav.NavigateTo(WithoutSignInParameters(uri), replace: true); // drop the one-time code from the address bar
            }
            catch (SupabaseException e)
            {
                Error = e.Message == SupabaseClient.BlockedMessage ? e.Message : $"Sign-in didn't work: {e.Message}";
                nav.NavigateTo(WithoutSignInParameters(uri), replace: true);
            }

            if (SignedIn) await LoadAccountAsync();
        }
        catch (Exception e) when (e is SupabaseException or HttpRequestException)
        {
            Error = e.Message;
        }
        finally
        {
            Ready = true;
            Changed?.Invoke();
        }
    }

    /// <summary>Sends the browser to the provider's sign-in page; it comes back to the page it left.</summary>
    public Task SignInAsync(AuthProvider provider) => Run(async () =>
    {
        var url = await supabase.BeginSignInAsync(provider.Id, WithoutSignInParameters(new Uri(nav.Uri)));
        nav.NavigateTo(url, forceLoad: true);
    });

    /// <summary>The address without the parameters a sign-in redirect adds, or any fragment.</summary>
    internal static string WithoutSignInParameters(Uri uri)
    {
        var kept = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !SupabaseClient.SignInParameters.Contains(Uri.UnescapeDataString(p.Split('=', 2)[0])))
            .ToList();
        return uri.GetLeftPart(UriPartial.Path) + (kept.Count > 0 ? "?" + string.Join('&', kept) : "");
    }

    public Task SignOutAsync() => Run(async () =>
    {
        await supabase.SignOutAsync();
        ForgetAccount();
        await storage.RemoveAsync(MyProfileKey);
    });

    private void ForgetAccount()
    {
        Groups = [];
        CurrentGroup = null;
        MyProfile = null;
        Onboarding = null;
    }

    /// <summary>
    /// Finds the user's profile and playgroups. A first sign-in with no unclaimed profiles anywhere gets a profile
    /// straight away; otherwise <see cref="Onboarding"/> is set so the user can choose to start fresh or claim one.
    /// </summary>
    private async Task LoadAccountAsync()
    {
        try
        {
            var state = await account.GetStateAsync();
            if (state.HasProfile)
            {
                await SetMyProfileAsync(await account.GetMineAsync());
            }
            else if (state.Blocked)
            {
                await BlockedAsync();
                return;
            }
            else if (!state.CanClaim)
            {
                await SetMyProfileAsync(await account.CreateMineAsync());
            }
            else
            {
                Onboarding = state;
                return;
            }
            await LoadGroupsAsync();
        }
        catch (HttpRequestException)
        {
            // Offline: carry on with the profile from last time, so the game still works.
            MyProfile = await CachedProfileAsync();
            if (MyProfile is null) throw;
        }
    }

    /// <summary>Tries loading the account again, e.g. after a network problem.</summary>
    public Task RetryAccountAsync() => Run(LoadAccountAsync);

    /// <summary>First sign-in: starts a new profile, named after the sign-in account if no name is given.</summary>
    public Task CreateMyProfileAsync(string? name) => Run(async () =>
    {
        await SetMyProfileAsync(await account.CreateMineAsync(name));
        Onboarding = null;
        await LoadGroupsAsync();
    });

    /// <summary>
    /// First sign-in: claims an existing profile with its claim code. Wrong codes are counted by the database;
    /// the result says how many tries are left, or until when the account has to wait.
    /// </summary>
    public async Task<ClaimResult?> ClaimProfileAsync(string code)
    {
        ClaimResult? result = null;
        await Run(async () =>
        {
            result = await account.ClaimAsync(code);
            switch (result.Status)
            {
                case ClaimStatus.Claimed:
                    await SetMyProfileAsync(await account.GetMineAsync());
                    Onboarding = null;
                    await LoadGroupsAsync();
                    break;
                case ClaimStatus.Blocked:
                    await BlockedAsync();
                    break;
                default:
                    Onboarding = Onboarding is null ? null : Onboarding with { AttemptsLeft = result.AttemptsLeft, LockedUntil = result.LockedUntil };
                    break;
            }
        });
        return result;
    }

    /// <summary>The claim code for an unclaimed profile the user looks after; <paramref name="reset"/> replaces it.</summary>
    public async Task<string?> GetClaimCodeAsync(Guid profileId, bool reset = false)
    {
        string? code = null;
        await Run(async () => code = await account.GetClaimCodeAsync(profileId, reset));
        return code;
    }

    private async Task BlockedAsync()
    {
        await supabase.SignOutAsync();
        ForgetAccount();
        Error = SupabaseClient.BlockedMessage;
    }

    private async Task SetMyProfileAsync(PlayerProfile? profile)
    {
        MyProfile = profile;
        if (profile is null) return;
        await storage.SetAsync(MyProfileKey, System.Text.Json.JsonSerializer.Serialize(
            new CachedProfile(profile.Id, profile.Name, profile.UserId, profile.PlayerCode)));
    }

    private async Task<PlayerProfile?> CachedProfileAsync()
    {
        try
        {
            var json = await storage.GetAsync(MyProfileKey);
            var cached = json is null ? null : System.Text.Json.JsonSerializer.Deserialize<CachedProfile>(json);
            return cached is not null && cached.UserId == UserId
                ? new PlayerProfile { Id = cached.Id, Name = cached.Name, UserId = cached.UserId, CreatedBy = cached.UserId, PlayerCode = cached.PlayerCode }
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record CachedProfile(Guid Id, string Name, Guid? UserId, string PlayerCode);

    // ----- Playgroups -----

    /// <summary>Whether the signed-in user is the playgroup's admin.</summary>
    public bool IsAdmin(Playgroup? group) => group is not null && MyProfile is { } me && group.AdminProfileId == me.Id;

    public Task CreateGroupAsync(string name) => Run(async () =>
    {
        var group = await playgroups.CreateAsync(name);
        await LoadGroupsAsync(group.Id);
    });

    public Task JoinGroupAsync(string joinCode) => Run(async () =>
    {
        var group = await playgroups.JoinAsync(joinCode);
        await LoadGroupsAsync(group.Id);
    });

    /// <summary>Takes the user's own profile out of the current group. The admin has to hand the group over first.</summary>
    public Task LeaveGroupAsync() => Run(async () =>
    {
        if (CurrentGroup is null || MyProfile is null) return;
        if (IsAdmin(CurrentGroup))
            throw new SupabaseException("You're this playgroup's admin. Hand it over to someone else before leaving, or delete it.", 400);
        await playgroups.RemoveProfileAsync(CurrentGroup.Id, MyProfile.Id);
        await LoadGroupsAsync();
    });

    /// <summary>Admin: renames the current group.</summary>
    public Task RenameGroupAsync(string name) => Run(async () =>
    {
        if (CurrentGroup is null) return;
        var renamed = await playgroups.RenameAsync(CurrentGroup.Id, name);
        ReplaceCurrentGroup(renamed);
    });

    /// <summary>Admin: deletes the current group and its play history. Players and their decks stay.</summary>
    public Task DeleteGroupAsync() => Run(async () =>
    {
        if (CurrentGroup is null) return;
        await playgroups.DeleteAsync(CurrentGroup.Id);
        await LoadGroupsAsync();
    });

    /// <summary>Admin: hands the current group to another signed-in player in it.</summary>
    public Task TransferGroupAsync(Guid newAdminProfileId) => Run(async () =>
    {
        if (CurrentGroup is null) return;
        ReplaceCurrentGroup(await playgroups.TransferAsync(CurrentGroup.Id, newAdminProfileId));
    });

    /// <summary>Admin: the current group's join code; <paramref name="reset"/> issues a new one.</summary>
    public async Task<string?> GetJoinCodeAsync(bool reset = false)
    {
        string? code = null;
        await Run(async () => { if (CurrentGroup is { } g) code = await playgroups.GetJoinCodeAsync(g.Id, reset); });
        return code;
    }

    /// <summary>Admin: adds someone's profile to the current group with their player code.</summary>
    public Task AddPlayerByCodeAsync(string playerCode) => Run(async () =>
    {
        if (CurrentGroup is null) return;
        await playgroups.AddByPlayerCodeAsync(CurrentGroup.Id, playerCode);
    });

    /// <summary>Admin: takes a player out of the current group. Their profile and decks stay.</summary>
    public Task RemovePlayerAsync(Guid profileId) => Run(async () =>
    {
        if (CurrentGroup is null) return;
        await playgroups.RemoveProfileAsync(CurrentGroup.Id, profileId);
    });

    public async Task SelectGroupAsync(Guid groupId)
    {
        CurrentGroup = Groups.FirstOrDefault(g => g.Id == groupId);
        if (CurrentGroup is not null) await storage.SetAsync(GroupKey, CurrentGroup.Id.ToString());
        Changed?.Invoke();
    }

    /// <summary>Call after the user saves their own profile, so its new name shows everywhere.</summary>
    public async Task MyProfileSavedAsync(PlayerProfile saved)
    {
        if (saved.Id != MyProfile?.Id) return;
        await SetMyProfileAsync(saved);
        Changed?.Invoke();
    }

    private void ReplaceCurrentGroup(Playgroup group)
    {
        CurrentGroup = group;
        Groups = Groups.Select(g => g.Id == group.Id ? group : g).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task LoadGroupsAsync(Guid? select = null)
    {
        Groups = await playgroups.ListMineAsync();
        var saved = Guid.TryParse(await storage.GetAsync(GroupKey), out var id) ? id : (Guid?)null;
        CurrentGroup = Groups.FirstOrDefault(g => g.Id == (select ?? saved)) ?? Groups.FirstOrDefault();
        if (CurrentGroup is not null) await storage.SetAsync(GroupKey, CurrentGroup.Id.ToString());
    }

    /// <summary>Runs an action, turning failures into <see cref="Error"/> and telling listeners either way.</summary>
    private async Task Run(Func<Task> action)
    {
        Error = null;
        try
        {
            await action();
        }
        catch (Exception e) when (e is SupabaseException or HttpRequestException)
        {
            Error = e is HttpRequestException ? "Couldn't reach the server. Check your connection and try again." : e.Message;
            throw new DeckLibraryException(Error, e);
        }
        finally
        {
            Changed?.Invoke();
        }
    }
}

/// <summary>A failure already described in <see cref="DeckLibrary.Error"/>.</summary>
public sealed class DeckLibraryException(string message, Exception inner) : Exception(message, inner);
