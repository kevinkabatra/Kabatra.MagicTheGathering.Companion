using Kabatra.MagicTheGathering.Companion.Core;
using Microsoft.JSInterop;

namespace Kabatra.MagicTheGathering.Companion.Services;

/// <summary>
/// The browser's localStorage. It can be unavailable (private browsing, blocked storage, full quota);
/// then reads return nothing and writes are dropped, so the app keeps working without persistence.
/// </summary>
public sealed class BrowserStorage(IJSRuntime js) : IKeyValueStore
{
    public async Task<string?> GetAsync(string key)
    {
        try { return await js.InvokeAsync<string?>("localStorage.getItem", key); }
        catch (JSException) { return null; }
    }

    public async Task SetAsync(string key, string value)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", key, value); }
        catch (JSException) { }
    }

    public async Task RemoveAsync(string key)
    {
        try { await js.InvokeVoidAsync("localStorage.removeItem", key); }
        catch (JSException) { }
    }
}
