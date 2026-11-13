using Kabatra.MagicTheGathering.Companion.Core;
using Microsoft.JSInterop;

namespace Kabatra.MagicTheGathering.Companion.Services;

/// <summary>
/// Holds the current game for the app and saves it in the browser's localStorage,
/// so closing the tab or a phone locking mid-game doesn't lose the life totals.
/// </summary>
public sealed class GameSession(IJSRuntime js)
{
    private const string StorageKey = "mtg-companion.current-game";
    private const string LastPlayersKey = "mtg-companion.last-players";
    private const string LastProfilesKey = "mtg-companion.last-profiles";

    public Game? Current { get; private set; }
    public Randomizer Random { get; } = new();

    /// <summary>Raised after any change so components can re-render.</summary>
    public event Action? Changed;

    public async Task LoadAsync()
    {
        var json = await TryGetAsync(StorageKey);
        if (json is null) return;
        try
        {
            Current = Game.FromJson(json);
        }
        catch
        {
            // A save from an older/incompatible version: start fresh rather than crash.
            await TryRemoveAsync(StorageKey);
        }
    }

    public async Task StartAsync(Game game)
    {
        Current = game;
        await TrySetAsync(LastPlayersKey, string.Join("\n", game.Players.Select(p => p.Name)));
        await TrySetAsync(LastProfilesKey, string.Join("\n", game.Players.Select(p => p.ProfileId?.ToString() ?? "")));
        await SaveAsync();
    }

    /// <summary>Who sat down last game, so the setup form can offer the same table again.</summary>
    public async Task<IReadOnlyList<(string Name, Guid? ProfileId)>> GetLastPlayersAsync()
    {
        var names = await TryGetAsync(LastPlayersKey);
        if (string.IsNullOrEmpty(names)) return [];
        var profiles = (await TryGetAsync(LastProfilesKey))?.Split('\n') ?? [];
        return names.Split('\n')
            .Select((name, i) => (name, i < profiles.Length && Guid.TryParse(profiles[i], out var id) ? id : (Guid?)null))
            .ToList();
    }

    /// <summary>Applies a change to the current game, saves it, and notifies listeners.</summary>
    public async Task UpdateAsync(Action<Game> change)
    {
        if (Current is null) return;
        change(Current);
        await SaveAsync();
    }

    public async Task EndAsync()
    {
        Current = null;
        await TryRemoveAsync(StorageKey);
        Changed?.Invoke();
    }

    private async Task SaveAsync()
    {
        if (Current is not null) await TrySetAsync(StorageKey, Current.ToJson());
        Changed?.Invoke();
    }

    // localStorage can be unavailable (private browsing, blocked storage). The app still works, it just won't persist.
    private async Task<string?> TryGetAsync(string key)
    {
        try { return await js.InvokeAsync<string?>("localStorage.getItem", key); }
        catch (JSException) { return null; }
    }

    private async Task TrySetAsync(string key, string value)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", key, value); }
        catch (JSException) { }
    }

    private async Task TryRemoveAsync(string key)
    {
        try { await js.InvokeVoidAsync("localStorage.removeItem", key); }
        catch (JSException) { }
    }
}
