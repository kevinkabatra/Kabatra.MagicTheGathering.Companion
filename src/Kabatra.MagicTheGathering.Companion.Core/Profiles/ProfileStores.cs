using System.Text.Json.Serialization;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;
using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Core.Profiles;

/// <summary>Where player profiles are kept: a playgroup in Supabase, or this device.</summary>
public interface IProfileStore
{
    Task<IReadOnlyList<PlayerProfile>> ListAsync(CancellationToken ct = default);
    Task<PlayerProfile?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Creates or replaces the profile and returns it as stored.</summary>
    Task<PlayerProfile> SaveAsync(PlayerProfile profile, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Profiles saved in this browser only, for when cloud sync isn't set up.</summary>
public sealed class LocalProfileStore(IKeyValueStore store, TimeProvider? clock = null) : IProfileStore
{
    private readonly LocalJsonList<PlayerProfile> _list = new(store, "mtg-companion.profiles", p => p.Id);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IReadOnlyList<PlayerProfile>> ListAsync(CancellationToken ct = default) =>
        (await _list.LoadAsync()).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task<PlayerProfile?> GetAsync(Guid id, CancellationToken ct = default) => _list.GetAsync(id);

    public async Task<PlayerProfile> SaveAsync(PlayerProfile profile, CancellationToken ct = default)
    {
        var saved = profile.Clone();
        saved.Name = saved.Name.Trim();
        saved.UpdatedAt = _clock.GetUtcNow();
        await _list.SaveAsync(saved);
        return saved.Clone();
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _list.DeleteAsync(id);
}

/// <summary>Profiles in a playgroup, stored in the Supabase "player_profiles" table.</summary>
public sealed class SupabaseProfileStore(SupabaseClient supabase, Playgroup group) : IProfileStore
{
    private const string Columns = "id,playgroup_id,name,collection,updated_at";

    public async Task<IReadOnlyList<PlayerProfile>> ListAsync(CancellationToken ct = default)
    {
        var rows = await supabase.GetAsync<List<ProfileRow>>($"player_profiles?select={Columns}&playgroup_id=eq.{group.Id}&order=name", ct);
        return rows.Select(r => r.ToProfile()).ToList();
    }

    public async Task<PlayerProfile?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await supabase.GetAsync<List<ProfileRow>>($"player_profiles?select={Columns}&id=eq.{id}", ct);
        return rows.FirstOrDefault()?.ToProfile();
    }

    public async Task<PlayerProfile> SaveAsync(PlayerProfile profile, CancellationToken ct = default)
    {
        var rows = await supabase.UpsertAsync<ProfileRow>("player_profiles", ProfileRow.From(profile, group.Id), ct);
        return rows.FirstOrDefault()?.ToProfile() ?? throw new SupabaseException("The profile wasn't saved.", 500);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => supabase.DeleteAsync($"player_profiles?id=eq.{id}", ct);

    /// <summary>The database row. The collection is a JSON array in one column, in the same shape as a deck's cards.</summary>
    internal sealed record ProfileRow(
        Guid Id,
        Guid PlaygroupId,
        string Name,
        List<SupabaseDeckStore.CardRow> Collection,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DateTimeOffset UpdatedAt)
    {
        public static ProfileRow From(PlayerProfile p, Guid groupId) => new(
            p.Id, groupId, p.Name.Trim(),
            p.Collection.Select(SupabaseDeckStore.CardRow.From).ToList(),
            default);

        public PlayerProfile ToProfile() => new()
        {
            Id = Id,
            Name = Name,
            UpdatedAt = UpdatedAt,
            Collection = (Collection ?? []).Select(c => c.ToDeckCard()).ToList(),
        };
    }
}
