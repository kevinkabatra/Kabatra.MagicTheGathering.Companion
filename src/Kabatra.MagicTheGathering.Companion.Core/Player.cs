namespace Kabatra.MagicTheGathering.Companion.Core;

public enum CounterKind
{
    Life,
    Poison,
    Energy,
    Experience,
    CommanderDamage,
}

public sealed class Player
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>A CSS color used to tell players apart on screen.</summary>
    public string Color { get; set; } = "#6b7280";

    /// <summary>The deck this player brought, if they picked one at setup.</summary>
    public Guid? DeckId { get; set; }
    public string? DeckName { get; set; }

    /// <summary>Art for the player's card, usually their commander's.</summary>
    public string? DeckImageUrl { get; set; }

    public int Life { get; set; }
    public int Poison { get; set; }
    public int Energy { get; set; }
    public int Experience { get; set; }

    /// <summary>Commander damage this player has taken, keyed by the id of the player whose commander dealt it.</summary>
    public Dictionary<Guid, int> CommanderDamageTaken { get; set; } = [];

    /// <summary>True when the player has conceded or been knocked out manually.</summary>
    public bool Conceded { get; set; }

    public int CommanderDamageFrom(Guid sourcePlayerId) =>
        CommanderDamageTaken.TryGetValue(sourcePlayerId, out var damage) ? damage : 0;

    public bool IsEliminated(GameFormat format) =>
        Conceded
        || Life <= 0
        || Poison >= format.PoisonToLose
        || (format.TracksCommanderDamage && CommanderDamageTaken.Values.Any(d => d >= format.CommanderDamageToLose));
}
