using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;

namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

/// <summary>Where a list of decks is kept: a playgroup in Supabase, or this device.</summary>
public interface IDeckStore
{
    /// <summary>Shown to the user, e.g. the playgroup's name or "This device".</summary>
    string Description { get; }

    Task<IReadOnlyList<Deck>> ListAsync(CancellationToken ct = default);
    Task<Deck?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Creates or replaces the deck and returns it as stored.</summary>
    Task<Deck> SaveAsync(Deck deck, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Decks saved in this browser only, for when cloud sync isn't set up.</summary>
public sealed class LocalDeckStore(IKeyValueStore store, TimeProvider? clock = null) : IDeckStore
{
    private const string Key = "mtg-companion.decks";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Description => "This device";

    public async Task<IReadOnlyList<Deck>> ListAsync(CancellationToken ct = default) =>
        (await LoadAsync()).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public async Task<Deck?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await LoadAsync()).FirstOrDefault(d => d.Id == id);

    public async Task<Deck> SaveAsync(Deck deck, CancellationToken ct = default)
    {
        var decks = await LoadAsync();
        var saved = deck.Clone();
        saved.UpdatedAt = _clock.GetUtcNow();
        decks.RemoveAll(d => d.Id == deck.Id);
        decks.Add(saved);
        await store.SetAsync(Key, JsonSerializer.Serialize(decks, Json));
        return saved.Clone();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var decks = await LoadAsync();
        if (decks.RemoveAll(d => d.Id == id) > 0) await store.SetAsync(Key, JsonSerializer.Serialize(decks, Json));
    }

    private async Task<List<Deck>> LoadAsync()
    {
        var json = await store.GetAsync(Key);
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<List<Deck>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }
}

/// <summary>Decks shared with a playgroup, stored in the Supabase "decks" table.</summary>
public sealed class SupabaseDeckStore(SupabaseClient supabase, Playgroup group) : IDeckStore
{
    private const string OwnerProfileColumn = "owner_profile_id";
    private const string LegacyColumns = "id,playgroup_id,name,owner_name,format,notes,cover_image_url,cards,updated_at";
    private const string Columns = LegacyColumns + "," + OwnerProfileColumn;

    public string Description => group.Name;
    public Playgroup Group => group;

    public async Task<IReadOnlyList<Deck>> ListAsync(CancellationToken ct = default)
    {
        var rows = await WithOldSchemaFallback(columns => supabase.GetAsync<List<DeckRow>>($"decks?select={columns}&playgroup_id=eq.{group.Id}&order=name", ct));
        return rows.Select(r => r.ToDeck()).ToList();
    }

    public async Task<Deck?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await WithOldSchemaFallback(columns => supabase.GetAsync<List<DeckRow>>($"decks?select={columns}&id=eq.{id}", ct));
        return rows.FirstOrDefault()?.ToDeck();
    }

    public async Task<Deck> SaveAsync(Deck deck, CancellationToken ct = default)
    {
        var row = DeckRow.From(deck, group.Id);
        var rows = await WithOldSchemaFallback(_ => supabase.DecksLackOwnerProfile
            ? supabase.UpsertAsync<DeckRow>("decks", WithoutOwnerProfile(row), ct)
            : supabase.UpsertAsync<DeckRow>("decks", row, ct));
        return rows.FirstOrDefault()?.ToDeck() ?? throw new SupabaseException("The deck wasn't saved.", 500);
    }

    /// <summary>
    /// Runs a request, and if the database predates player profiles (no owner_profile_id column), runs it
    /// again without that column. Decks then work as before, just without an owner profile.
    /// </summary>
    private async Task<T> WithOldSchemaFallback<T>(Func<string, Task<T>> request)
    {
        if (supabase.DecksLackOwnerProfile) return await request(LegacyColumns);
        try
        {
            return await request(Columns);
        }
        catch (SupabaseException e) when (e.NeedsSchemaUpdate && e.Message.Contains(OwnerProfileColumn, StringComparison.OrdinalIgnoreCase))
        {
            supabase.DecksLackOwnerProfile = true;
            return await request(LegacyColumns);
        }
    }

    private static JsonObject WithoutOwnerProfile(DeckRow row)
    {
        var json = JsonSerializer.SerializeToNode(row, SupabaseClient.Json)!.AsObject();
        json.Remove(OwnerProfileColumn);
        return json;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => supabase.DeleteAsync($"decks?id=eq.{id}", ct);

    /// <summary>The database row. Cards are stored as a JSON array in one column.</summary>
    internal sealed record DeckRow(
        Guid Id,
        Guid PlaygroupId,
        string Name,
        // Always send these, even when empty, so clearing a field on an existing deck sticks.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OwnerName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? OwnerProfileId,
        string Format,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Notes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CoverImageUrl,
        List<CardRow> Cards,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DateTimeOffset UpdatedAt)
    {
        public static DeckRow From(Deck d, Guid groupId) => new(
            d.Id, groupId, d.Name.Trim(), d.OwnerName.Trim(), d.OwnerProfileId, d.Format, d.Notes, d.CoverImageUrl,
            d.Cards.Select(CardRow.From).ToList(),
            default);

        public Deck ToDeck() => new()
        {
            Id = Id,
            Name = Name,
            OwnerName = OwnerName ?? "",
            OwnerProfileId = OwnerProfileId,
            Format = Format,
            Notes = Notes ?? "",
            CoverImageUrl = CoverImageUrl,
            UpdatedAt = UpdatedAt,
            Cards = Cards.Select(c => c.ToDeckCard()).ToList(),
        };
    }

    internal sealed record CardRow(string Name, int Quantity, DeckSection Section, Guid? ScryfallId, string? SetCode, string? CollectorNumber)
    {
        public static CardRow From(DeckCard c) => new(c.Name, c.Quantity, c.Section, c.ScryfallId, c.SetCode, c.CollectorNumber);

        public DeckCard ToDeckCard() => new()
        {
            Name = Name,
            Quantity = Quantity,
            Section = Section,
            ScryfallId = ScryfallId,
            SetCode = SetCode,
            CollectorNumber = CollectorNumber,
        };
    }
}
