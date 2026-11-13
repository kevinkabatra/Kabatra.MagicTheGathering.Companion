namespace Kabatra.MagicTheGathering.Companion.Core;

/// <summary>
/// Simple string storage that survives a page reload (the browser's localStorage in the app).
/// Implementations should swallow storage errors: losing a cache must never break the app.
/// </summary>
public interface IKeyValueStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
}

/// <summary>An in-memory store, for tests and for when nothing should persist.</summary>
public sealed class MemoryKeyValueStore : IKeyValueStore
{
    private readonly Dictionary<string, string> _items = [];

    public Task<string?> GetAsync(string key) => Task.FromResult(_items.TryGetValue(key, out var v) ? v : null);
    public Task SetAsync(string key, string value) { _items[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key) { _items.Remove(key); return Task.CompletedTask; }
}
