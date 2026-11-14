using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Core.Profiles;

/// <summary>A net change to one printing in a collection since it was saved: copies added (positive) or removed.</summary>
public sealed record CollectionChange(string Name, string? SetCode, string? CollectorNumber, int Delta)
{
    public bool IsAddition => Delta > 0;

    /// <summary>e.g. "FDC #163", or empty when the printing isn't known.</summary>
    public string Printing =>
        $"{SetCode?.ToUpperInvariant()}{(CollectorNumber is null ? "" : $" #{CollectorNumber}")}".Trim();
}

/// <summary>A player profile as it was when loaded or last saved, to list collection changes and to discard them.</summary>
public sealed class CollectionSnapshot
{
    private readonly CardLinesSnapshot _lines;
    private readonly string _name;

    private CollectionSnapshot(PlayerProfile profile)
    {
        _lines = new CardLinesSnapshot(profile.Collection);
        _name = profile.Name;
    }

    public static CollectionSnapshot Of(PlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new CollectionSnapshot(profile);
    }

    /// <summary>Cards added or removed since the snapshot, netted per printing and sorted by name then set.</summary>
    public IReadOnlyList<CollectionChange> ChangesIn(PlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _lines.Deltas(profile.Collection)
            .GroupBy(d => d.ScryfallId?.ToString()
                ?? $"{d.Name.ToUpperInvariant()}|{d.SetCode?.ToUpperInvariant()}|{d.CollectorNumber?.ToUpperInvariant()}")
            .Select(g =>
            {
                var first = g.First();
                return new CollectionChange(first.Name, first.SetCode, first.CollectorNumber, g.Sum(d => d.Delta));
            })
            .Where(c => c.Delta != 0)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Printing, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Puts the profile back as it was in the snapshot, undoing every unsaved change: the collection and the name.
    /// Lines keep anything learned by looking their card up since.
    /// </summary>
    public void Restore(PlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Collection = _lines.Restore();
        profile.Name = _name;
    }
}
