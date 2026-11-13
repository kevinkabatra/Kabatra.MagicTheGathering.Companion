using System.Text.Json;

namespace Kabatra.MagicTheGathering.Companion.Core.History;

/// <summary>
/// Saves finished games to play history without losing any. Each game is first written to this device,
/// then sent to the history store; if that fails (offline, signed out) it stays queued and is sent next time.
/// A queued game remembers its playgroup, so it never lands in a different group's history.
/// </summary>
public sealed class GameRecorder(IKeyValueStore store)
{
    private const string Key = "mtg-companion.unsaved-games";
    private const int MaxQueued = 200;

    /// <summary>Queues a game for history. <paramref name="playgroupId"/> is null for on-device history.</summary>
    public async Task QueueAsync(GameRecord record, Guid? playgroupId)
    {
        ArgumentNullException.ThrowIfNull(record);
        var queue = await LoadAsync();
        queue.RemoveAll(q => q.Record.Id == record.Id);
        queue.Add(new Queued(record.Clone(), playgroupId));
        await WriteAsync(queue.TakeLast(MaxQueued).ToList());
    }

    /// <summary>How many games are still waiting to be saved to the given playgroup (or the device).</summary>
    public async Task<int> CountPendingAsync(Guid? playgroupId) => (await LoadAsync()).Count(q => q.PlaygroupId == playgroupId);

    /// <summary>
    /// Sends every game queued for this playgroup to its history store, and returns how many were saved.
    /// Games that fail stay queued; the first failure is rethrown after the rest have been tried.
    /// </summary>
    public async Task<int> FlushAsync(IGameHistoryStore history, Guid? playgroupId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        var queue = await LoadAsync();
        var saved = 0;
        Exception? failure = null;
        foreach (var item in queue.Where(q => q.PlaygroupId == playgroupId).ToList())
        {
            try
            {
                await history.SaveAsync(item.Record, ct);
                queue.Remove(item);
                saved++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failure ??= e;
            }
        }
        if (saved > 0) await WriteAsync(queue);
        if (failure is not null) throw failure;
        return saved;
    }

    private async Task<List<Queued>> LoadAsync()
    {
        var json = await store.GetAsync(Key);
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<List<Queued>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private Task WriteAsync(List<Queued> queue) =>
        queue.Count == 0 ? store.RemoveAsync(Key) : store.SetAsync(Key, JsonSerializer.Serialize(queue));

    private sealed record Queued(GameRecord Record, Guid? PlaygroupId);
}
