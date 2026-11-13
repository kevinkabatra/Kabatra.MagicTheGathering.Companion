using System.Text.Json.Serialization;

namespace Kabatra.MagicTheGathering.Companion.Core.History;

/// <summary>A finished game, kept for play history.</summary>
public sealed class GameRecord
{
    /// <summary>The game's own id, so recording the same game twice replaces the first record.</summary>
    public Guid Id { get; set; }

    /// <summary>The format's display name, e.g. "Commander".</summary>
    public string Format { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }

    /// <summary>The turn the game ended on.</summary>
    public int Turns { get; set; }

    public List<GameRecordPlayer> Players { get; set; } = [];

    [JsonIgnore] public GameRecordPlayer? Winner => Players.FirstOrDefault(p => p.Won);

    [JsonIgnore] public TimeSpan Duration => EndedAt > StartedAt ? EndedAt - StartedAt : TimeSpan.Zero;

    public GameRecordPlayer? PlayerFor(Guid profileId) => Players.FirstOrDefault(p => p.ProfileId == profileId);

    /// <summary>
    /// Whether a game is worth keeping: two or more players, and either someone won or it got past
    /// the first turn. A game set up and abandoned straight away isn't history.
    /// </summary>
    public static bool IsWorthRecording(Game game) =>
        game.Players.Count >= 2 && (game.Winner is not null || game.TurnNumber > 1);

    public static GameRecord From(Game game, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(game);
        var winner = game.Winner;
        return new GameRecord
        {
            Id = game.Id,
            Format = game.Format.Name,
            StartedAt = game.StartedAt,
            EndedAt = endedAt,
            Turns = game.TurnNumber,
            Players = game.Players.Select(p => new GameRecordPlayer
            {
                ProfileId = p.ProfileId,
                Name = p.Name,
                DeckId = p.DeckId,
                DeckName = p.DeckName,
                Won = winner?.Id == p.Id,
                Eliminated = p.IsEliminated(game.Format),
                FinalLife = p.Life,
            }).ToList(),
        };
    }

    public GameRecord Clone()
    {
        var copy = (GameRecord)MemberwiseClone();
        copy.Players = Players.Select(p => p with { }).ToList();
        return copy;
    }
}

/// <summary>One seat in a recorded game.</summary>
public sealed record GameRecordPlayer
{
    /// <summary>The player's profile, or null for a guest.</summary>
    public Guid? ProfileId { get; init; }
    public string Name { get; init; } = "";
    public Guid? DeckId { get; init; }
    public string? DeckName { get; init; }
    public bool Won { get; init; }
    public bool Eliminated { get; init; }
    public int FinalLife { get; init; }
}

/// <summary>How one deck did for a player.</summary>
public sealed record DeckRecord(Guid? DeckId, string DeckName, int Games, int Wins);

/// <summary>Games played and won by one profile, overall and per deck.</summary>
public sealed record PlayerStats(int Games, int Wins, IReadOnlyList<DeckRecord> Decks)
{
    public int Losses => Games - Wins;

    /// <summary>Wins as a fraction of games, or null before the first game.</summary>
    public double? WinRate => Games == 0 ? null : (double)Wins / Games;

    public static PlayerStats For(Guid profileId, IEnumerable<GameRecord> games)
    {
        var seats = games.Select(g => g.PlayerFor(profileId)).OfType<GameRecordPlayer>().ToList();
        var decks = seats
            .GroupBy(s => s.DeckId is { } id ? id.ToString() : "name:" + (s.DeckName ?? ""))
            .Select(g => new DeckRecord(g.First().DeckId, g.Select(s => s.DeckName).LastOrDefault(n => n is not null) ?? "No deck", g.Count(), g.Count(s => s.Won)))
            .OrderByDescending(d => d.Games).ThenBy(d => d.DeckName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new(seats.Count, seats.Count(s => s.Won), decks);
    }

    /// <summary>How a deck has done across everyone who played it.</summary>
    public static DeckRecord ForDeck(Guid deckId, string deckName, IEnumerable<GameRecord> games)
    {
        var seats = games.SelectMany(g => g.Players).Where(p => p.DeckId == deckId).ToList();
        return new(deckId, deckName, seats.Count, seats.Count(s => s.Won));
    }
}
