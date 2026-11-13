using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;
using Kabatra.MagicTheGathering.Companion.Core.History;

namespace Kabatra.MagicTheGathering.Companion.Services;

/// <summary>
/// Records finished games in the playgroup's play history. Games are queued on the device first,
/// so one finished offline or while signed out is saved the next time the history can be reached.
/// </summary>
public sealed class PlayHistory(DeckLibrary library, GameRecorder recorder, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Records the game if it's worth keeping (see <see cref="GameRecord.IsWorthRecording"/>).</summary>
    public async Task RecordAsync(Game game)
    {
        if (!GameRecord.IsWorthRecording(game)) return;
        await recorder.QueueAsync(GameRecord.From(game, _clock.GetUtcNow()), await library.HistoryGroupAsync());
        await TrySyncAsync();
    }

    /// <summary>Sends any queued games to the current history store. Never throws; returns how many were sent.</summary>
    public async Task<int> TrySyncAsync()
    {
        try
        {
            await library.InitializeAsync();
            if (library.History is not { } history) return 0;
            var group = await library.HistoryGroupAsync();
            var saved = await recorder.FlushAsync(history, group);
            // Games finished before this device had ever joined a group go to the first group it uses.
            if (group is { } id && id != Guid.Empty) saved += await recorder.FlushAsync(history, Guid.Empty);
            return saved;
        }
        catch (Exception e) when (e is SupabaseException or HttpRequestException)
        {
            return 0;
        }
    }

    /// <summary>Games finished here that haven't reached the history yet.</summary>
    public async Task<int> PendingAsync() => await recorder.CountPendingAsync(await library.HistoryGroupAsync());
}
