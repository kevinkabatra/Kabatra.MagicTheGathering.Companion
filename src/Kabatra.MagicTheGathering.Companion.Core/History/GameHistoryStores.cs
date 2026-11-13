using System.Text.Json.Serialization;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;

namespace Kabatra.MagicTheGathering.Companion.Core.History;

/// <summary>Where finished games are kept: a playgroup in Supabase, or this device.</summary>
public interface IGameHistoryStore
{
    /// <summary>The most recent games, newest first.</summary>
    Task<IReadOnlyList<GameRecord>> ListAsync(CancellationToken ct = default);

    /// <summary>Creates or replaces the record (records are keyed by the game's id).</summary>
    Task SaveAsync(GameRecord record, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Play history saved in this browser only, for when cloud sync isn't set up.</summary>
public sealed class LocalGameHistoryStore(IKeyValueStore store) : IGameHistoryStore
{
    /// <summary>Oldest games are dropped past this, so localStorage doesn't fill up.</summary>
    public const int MaxGames = 1000;

    private readonly LocalJsonList<GameRecord> _list = new(store, "mtg-companion.game-history", g => g.Id);

    public async Task<IReadOnlyList<GameRecord>> ListAsync(CancellationToken ct = default) =>
        (await _list.LoadAsync()).OrderByDescending(g => g.EndedAt).ToList();

    public async Task SaveAsync(GameRecord record, CancellationToken ct = default)
    {
        var games = await _list.LoadAsync();
        games.RemoveAll(g => g.Id == record.Id);
        games.Add(record.Clone());
        await _list.WriteAsync(games.OrderByDescending(g => g.EndedAt).Take(MaxGames).ToList());
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _list.DeleteAsync(id);
}

/// <summary>A playgroup's play history, stored in the Supabase "game_records" table.</summary>
public sealed class SupabaseGameHistoryStore(SupabaseClient supabase, Playgroup group) : IGameHistoryStore
{
    /// <summary>How many recent games are loaded. Plenty for a playgroup's stats.</summary>
    public const int MaxGames = 1000;

    private const string Columns = "id,playgroup_id,format,started_at,ended_at,turns,players";

    public async Task<IReadOnlyList<GameRecord>> ListAsync(CancellationToken ct = default)
    {
        var rows = await supabase.GetAsync<List<GameRow>>(
            $"game_records?select={Columns}&playgroup_id=eq.{group.Id}&order=ended_at.desc&limit={MaxGames}", ct);
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public Task SaveAsync(GameRecord record, CancellationToken ct = default) =>
        supabase.UpsertAsync<GameRow>("game_records", GameRow.From(record, group.Id), ct);

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => supabase.DeleteAsync($"game_records?id=eq.{id}", ct);

    /// <summary>The database row. Seats are a JSON array in one column.</summary>
    internal sealed record GameRow(
        Guid Id,
        Guid PlaygroupId,
        string Format,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        int Turns,
        List<SeatRow> Players)
    {
        public static GameRow From(GameRecord g, Guid groupId) => new(
            g.Id, groupId, g.Format, g.StartedAt, g.EndedAt, g.Turns,
            g.Players.Select(p => new SeatRow(p.ProfileId, p.Name, p.DeckId, p.DeckName, p.Won, p.Eliminated, p.FinalLife)).ToList());

        public GameRecord ToRecord() => new()
        {
            Id = Id,
            Format = Format,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            Turns = Turns,
            Players = (Players ?? []).Select(p => new GameRecordPlayer
            {
                ProfileId = p.ProfileId,
                Name = p.Name,
                DeckId = p.DeckId,
                DeckName = p.DeckName,
                Won = p.Won,
                Eliminated = p.Eliminated,
                FinalLife = p.FinalLife,
            }).ToList(),
        };
    }

    // Nulls are written too, so every seat in the JSON has the same keys.
    internal sealed record SeatRow(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? ProfileId,
        string Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? DeckId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DeckName,
        bool Won,
        bool Eliminated,
        int FinalLife);
}
