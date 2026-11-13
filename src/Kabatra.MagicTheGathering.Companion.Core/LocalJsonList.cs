using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kabatra.MagicTheGathering.Companion.Core;

/// <summary>A list of items saved as one JSON value in an <see cref="IKeyValueStore"/>, keyed by id.</summary>
internal sealed class LocalJsonList<T>(IKeyValueStore store, string key, Func<T, Guid> idOf)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public async Task<List<T>> LoadAsync()
    {
        var json = await store.GetAsync(key);
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<List<T>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public async Task<T?> GetAsync(Guid id) => (await LoadAsync()).FirstOrDefault(i => idOf(i) == id);

    /// <summary>Adds the item, replacing any item with the same id.</summary>
    public async Task SaveAsync(T item)
    {
        var items = await LoadAsync();
        items.RemoveAll(i => idOf(i) == idOf(item));
        items.Add(item);
        await WriteAsync(items);
    }

    public async Task DeleteAsync(Guid id)
    {
        var items = await LoadAsync();
        if (items.RemoveAll(i => idOf(i) == id) > 0) await WriteAsync(items);
    }

    public Task WriteAsync(List<T> items) => store.SetAsync(key, JsonSerializer.Serialize(items, Json));
}
