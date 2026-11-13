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
    IKeyValueStore storage,
    NavigationManager nav,
    IReadOnlyList<AuthProvider> providers)
{
    private const string GroupKey = "mtg-companion.playgroup";
    private const string DisplayNameKey = "mtg-companion.display-name";

    private Task? _init;

    /// <summary>False when appsettings.json has no usable Supabase project; the app can't be used then.</summary>
    public bool Configured => supabase.IsConfigured;

    /// <summary>The sign-in buttons to offer, in order.</summary>
    public IReadOnlyList<AuthProvider> Providers => providers;
    public bool SignedIn => supabase.IsSignedIn;
    public string? Email => supabase.Session?.Email;
    public IReadOnlyList<Playgroup> Groups { get; private set; } = [];
    public Playgroup? CurrentGroup { get; private set; }
    public string? DisplayName { get; private set; }
    public bool Ready { get; private set; }

    /// <summary>A problem to show the user, e.g. an expired sign-in link.</summary>
    public string? Error { get; set; }

    /// <summary>Where decks are read and saved right now, or null if the user still has to sign in or pick a group.</summary>
    public IDeckStore? Store => CurrentGroup is { } group && SignedIn ? new SupabaseDeckStore(supabase, group) : null;

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
            DisplayName = await storage.GetAsync(DisplayNameKey);
            if (!Configured) return;
            supabase.SessionChanged += () =>
            {
                // e.g. the refresh token expired mid-session
                if (!SignedIn) { Groups = []; CurrentGroup = null; }
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

            if (SignedIn) await LoadGroupsAsync();
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
        Groups = [];
        CurrentGroup = null;
    });

    public Task CreateGroupAsync(string name, string displayName) => Run(async () =>
    {
        await RememberDisplayNameAsync(displayName);
        var group = await playgroups.CreateAsync(name, displayName);
        await LoadGroupsAsync(group.Id);
    });

    public Task JoinGroupAsync(string code, string displayName) => Run(async () =>
    {
        await RememberDisplayNameAsync(displayName);
        var group = await playgroups.JoinAsync(code, displayName);
        await LoadGroupsAsync(group.Id);
    });

    public Task LeaveGroupAsync() => Run(async () =>
    {
        if (CurrentGroup is null) return;
        await playgroups.LeaveAsync(CurrentGroup.Id);
        await LoadGroupsAsync();
    });

    public Task ResetInviteCodeAsync() => Run(async () =>
    {
        if (CurrentGroup is null) return;
        var code = await playgroups.ResetInviteCodeAsync(CurrentGroup.Id);
        CurrentGroup = CurrentGroup with { InviteCode = code };
        Groups = Groups.Select(g => g.Id == CurrentGroup.Id ? CurrentGroup : g).ToList();
    });

    public async Task SelectGroupAsync(Guid groupId)
    {
        CurrentGroup = Groups.FirstOrDefault(g => g.Id == groupId);
        if (CurrentGroup is not null) await storage.SetAsync(GroupKey, CurrentGroup.Id.ToString());
        Changed?.Invoke();
    }

    public bool IsCreator(Playgroup group) => group.CreatedBy == supabase.Session?.UserId;

    private async Task LoadGroupsAsync(Guid? select = null)
    {
        Groups = await playgroups.ListMineAsync();
        var saved = Guid.TryParse(await storage.GetAsync(GroupKey), out var id) ? id : (Guid?)null;
        CurrentGroup = Groups.FirstOrDefault(g => g.Id == (select ?? saved)) ?? Groups.FirstOrDefault();
        if (CurrentGroup is not null) await storage.SetAsync(GroupKey, CurrentGroup.Id.ToString());
    }

    private async Task RememberDisplayNameAsync(string name)
    {
        DisplayName = name.Trim();
        await storage.SetAsync(DisplayNameKey, DisplayName);
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
