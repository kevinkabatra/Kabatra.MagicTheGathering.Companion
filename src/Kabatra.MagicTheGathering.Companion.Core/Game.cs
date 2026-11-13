using System.Text.Json;

namespace Kabatra.MagicTheGathering.Companion.Core;

/// <summary>
/// A game in progress: life totals, counters, turn order, history, and undo.
/// All changes go through methods on this class so they are logged and undoable.
/// </summary>
public sealed class Game
{
    public const int MinPlayers = 1;
    public const int MaxPlayers = 8;
    private const int MaxUndoSteps = 500;

    public static IReadOnlyList<string> DefaultColors { get; } =
        ["#d97706", "#2563eb", "#16a34a", "#dc2626", "#7c3aed", "#0891b2", "#db2777", "#4b5563"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly TimeProvider _clock;
    private readonly Stack<string> _undo = new();
    private GameState _state;

    private Game(GameState state, TimeProvider? clock)
    {
        _state = state;
        _clock = clock ?? TimeProvider.System;
    }

    public GameFormat Format => _state.Format;
    public IReadOnlyList<Player> Players => _state.Players;
    public int TurnNumber => _state.TurnNumber;
    public DateTimeOffset StartedAt => _state.StartedAt;
    public IReadOnlyList<GameLogEntry> Log => _state.Log;
    public Player ActivePlayer => _state.Players[_state.ActivePlayerIndex];
    public bool CanUndo => _undo.Count > 0;

    /// <summary>The last player standing, or null while the game is still going (or in a 1-player game).</summary>
    public Player? Winner
    {
        get
        {
            if (_state.Players.Count < 2) return null;
            var alive = _state.Players.Where(p => !p.IsEliminated(Format)).ToList();
            return alive.Count == 1 ? alive[0] : null;
        }
    }

    public static Game Start(GameFormat format, IEnumerable<string> playerNames, int firstPlayerIndex = 0, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        var names = playerNames.ToList();
        if (names.Count is < MinPlayers or > MaxPlayers)
            throw new ArgumentOutOfRangeException(nameof(playerNames), $"A game needs {MinPlayers}-{MaxPlayers} players.");
        if (firstPlayerIndex < 0 || firstPlayerIndex >= names.Count)
            throw new ArgumentOutOfRangeException(nameof(firstPlayerIndex));

        var state = new GameState
        {
            Format = format,
            ActivePlayerIndex = firstPlayerIndex,
            Players = names.Select((name, i) => new Player
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"Player {i + 1}" : name.Trim(),
                Color = DefaultColors[i % DefaultColors.Count],
                Life = format.StartingLife,
            }).ToList(),
        };

        var game = new Game(state, clock);
        state.StartedAt = game._clock.GetUtcNow();
        game.AddLog($"Game started ({format.Name}, {format.StartingLife} life). {game.ActivePlayer.Name} goes first.");
        return game;
    }

    public Player GetPlayer(Guid playerId) =>
        _state.Players.FirstOrDefault(p => p.Id == playerId)
        ?? throw new ArgumentException($"No player with id {playerId}.", nameof(playerId));

    public void AdjustLife(Guid playerId, int delta) => AdjustCounter(playerId, CounterKind.Life, delta);

    public void AdjustCounter(Guid playerId, CounterKind counter, int delta)
    {
        if (counter == CounterKind.CommanderDamage)
            throw new ArgumentException("Use AdjustCommanderDamage for commander damage.", nameof(counter));
        if (delta == 0) return;

        var player = GetPlayer(playerId);
        var before = Read(player, counter);
        // Life can go negative; the other counters cannot.
        var after = counter == CounterKind.Life ? before + delta : Math.Max(0, before + delta);
        if (after == before) return;

        var wasOut = player.IsEliminated(Format);
        Checkpoint();
        Write(player, counter, after);
        LogCounterChange(player, counter, null, before, after, null, null);
        ReportIfNewlyEliminated(player, wasOut);
    }

    /// <summary>
    /// Records commander damage dealt to <paramref name="targetPlayerId"/> by the commander of
    /// <paramref name="sourcePlayerId"/>. By default the same amount is also applied as life loss.
    /// </summary>
    public void AdjustCommanderDamage(Guid targetPlayerId, Guid sourcePlayerId, int delta, bool alsoAdjustLife = true)
    {
        if (delta == 0) return;
        if (targetPlayerId == sourcePlayerId)
            throw new ArgumentException("A player's own commander damage to themselves is not tracked.", nameof(sourcePlayerId));

        var target = GetPlayer(targetPlayerId);
        var source = GetPlayer(sourcePlayerId);
        var before = target.CommanderDamageFrom(sourcePlayerId);
        var after = Math.Max(0, before + delta);
        if (after == before) return;

        var wasOut = target.IsEliminated(Format);
        Checkpoint();
        target.CommanderDamageTaken[sourcePlayerId] = after;

        int? lifeBefore = null, lifeAfter = null;
        if (alsoAdjustLife)
        {
            lifeBefore = target.Life;
            // Life moves opposite to damage: more damage, less life.
            target.Life -= after - before;
            lifeAfter = target.Life;
        }

        LogCounterChange(target, CounterKind.CommanderDamage, source, before, after, lifeBefore, lifeAfter);
        ReportIfNewlyEliminated(target, wasOut);
    }

    public void SetConceded(Guid playerId, bool conceded)
    {
        var player = GetPlayer(playerId);
        if (player.Conceded == conceded) return;
        var wasEliminated = player.IsEliminated(Format);
        Checkpoint();
        player.Conceded = conceded;
        if (conceded)
        {
            AddLog($"{player.Name} conceded.");
            // Don't leave the turn on a player who is out.
            if (ActivePlayer.Id == playerId) AdvanceToNextLivingPlayer();
        }
        else
        {
            AddLog($"{player.Name} is back in the game.");
        }
        if (!wasEliminated) ReportWinner();
    }

    public void RenamePlayer(Guid playerId, string name)
    {
        var player = GetPlayer(playerId);
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed == player.Name) return;
        Checkpoint();
        AddLog($"{player.Name} is now called {trimmed}.");
        player.Name = trimmed;
    }

    /// <summary>Passes the turn to the next player who is still in the game.</summary>
    public void NextTurn()
    {
        if (_state.Players.All(p => p.IsEliminated(Format))) return;
        Checkpoint();
        _state.TurnNumber++;
        AdvanceToNextLivingPlayer();
        AddLog($"Turn {_state.TurnNumber}: {ActivePlayer.Name}'s turn.");
    }

    /// <summary>Adds a free-form note to the log, e.g. the result of a die roll.</summary>
    public void Note(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        AddLog(message.Trim());
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _state = Deserialize(_undo.Pop());
        return true;
    }

    public string ToJson() => JsonSerializer.Serialize(_state, JsonOptions);

    /// <summary>Restores a saved game. Undo history is not saved, so a restored game starts with none.</summary>
    public static Game FromJson(string json, TimeProvider? clock = null)
    {
        var state = Deserialize(json);
        if (state.Players.Count == 0) throw new InvalidOperationException("Saved game has no players.");
        state.ActivePlayerIndex = Math.Clamp(state.ActivePlayerIndex, 0, state.Players.Count - 1);
        return new Game(state, clock);
    }

    private static GameState Deserialize(string json) =>
        JsonSerializer.Deserialize<GameState>(json, JsonOptions)
        ?? throw new InvalidOperationException("Could not read saved game.");

    private void Checkpoint()
    {
        _undo.Push(ToJson());
        if (_undo.Count > MaxUndoSteps)
        {
            // Stack has no "drop oldest", so rebuild without the bottom item. Rare enough not to matter.
            var kept = _undo.Take(MaxUndoSteps).Reverse().ToList();
            _undo.Clear();
            foreach (var s in kept) _undo.Push(s);
        }
    }

    private void AdvanceToNextLivingPlayer()
    {
        var count = _state.Players.Count;
        for (var step = 1; step <= count; step++)
        {
            var index = (_state.ActivePlayerIndex + step) % count;
            if (!_state.Players[index].IsEliminated(Format))
            {
                _state.ActivePlayerIndex = index;
                return;
            }
        }
    }

    private void ReportIfNewlyEliminated(Player player, bool wasOut)
    {
        if (wasOut || !player.IsEliminated(Format)) return;
        AddLog($"{player.Name} is out of the game.");
        ReportWinner();
    }

    private void ReportWinner()
    {
        if (Winner is { } winner) AddLog($"{winner.Name} wins!");
    }

    private void AddLog(string message) =>
        _state.Log.Add(new GameLogEntry { Timestamp = _clock.GetUtcNow(), Turn = TurnNumber, Message = message });

    private void LogCounterChange(Player player, CounterKind counter, Player? source, int before, int after, int? lifeBefore, int? lifeAfter)
    {
        var last = _state.Log.Count > 0 ? _state.Log[^1] : null;
        if (last is not null && last.PlayerId == player.Id && last.Counter == counter
            && last.SourcePlayerId == source?.Id && last.Turn == TurnNumber && last.From is int from
            && last.LifeFrom.HasValue == lifeBefore.HasValue)
        {
            if (from == after)
            {
                _state.Log.RemoveAt(_state.Log.Count - 1); // changes cancelled out
                return;
            }
            last.To = after;
            last.LifeTo = lifeAfter;
            last.Timestamp = _clock.GetUtcNow();
            last.Message = DescribeChange(player, counter, source, from, after, last.LifeFrom, lifeAfter);
            return;
        }

        _state.Log.Add(new GameLogEntry
        {
            Timestamp = _clock.GetUtcNow(),
            Turn = TurnNumber,
            PlayerId = player.Id,
            Counter = counter,
            SourcePlayerId = source?.Id,
            From = before,
            To = after,
            LifeFrom = lifeBefore,
            LifeTo = lifeAfter,
            Message = DescribeChange(player, counter, source, before, after, lifeBefore, lifeAfter),
        });
    }

    private static string DescribeChange(Player player, CounterKind counter, Player? source, int from, int to, int? lifeFrom, int? lifeTo)
    {
        var text = DescribeChange(player, counter, source, from, to);
        return lifeFrom is int lf && lifeTo is int lt ? $"{text}, life {lf} → {lt}" : text;
    }

    private static string DescribeChange(Player player, CounterKind counter, Player? source, int from, int to)
    {
        var delta = to - from;
        var signed = delta > 0 ? $"+{delta}" : delta.ToString();
        var what = counter switch
        {
            CounterKind.Life => "life",
            CounterKind.Poison => "poison",
            CounterKind.Energy => "energy",
            CounterKind.Experience => "experience",
            CounterKind.CommanderDamage => $"commander damage from {source?.Name}",
            _ => counter.ToString(),
        };
        return $"{player.Name}: {what} {signed} ({from} → {to})";
    }

    private static int Read(Player p, CounterKind counter) => counter switch
    {
        CounterKind.Life => p.Life,
        CounterKind.Poison => p.Poison,
        CounterKind.Energy => p.Energy,
        CounterKind.Experience => p.Experience,
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static void Write(Player p, CounterKind counter, int value)
    {
        switch (counter)
        {
            case CounterKind.Life: p.Life = value; break;
            case CounterKind.Poison: p.Poison = value; break;
            case CounterKind.Energy: p.Energy = value; break;
            case CounterKind.Experience: p.Experience = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(counter));
        }
    }
}
