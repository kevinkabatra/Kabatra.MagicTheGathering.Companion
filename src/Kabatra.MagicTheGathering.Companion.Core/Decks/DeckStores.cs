using System.Text.Json;
using System.Text.Json.Serialization;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;

namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

/// <summary>Where decks are kept: the decks of everyone in a playgroup, or a guest's decks on this device.</summary>
public interface IDeckStore
{
    /// <summary>Shown to the user, e.g. the playgroup's name.</summary>
    string Description { get; }

    Task<IReadOnlyList<Deck>> ListAsync(CancellationToken ct = default);

    /// <summary>Any deck the user can see, whether or not its owner is in this playgroup.</summary>
    Task<Deck?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Creates or replaces the deck and returns it as stored.</summary>
    Task<Deck> SaveAsync(Deck deck, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Decks saved in this browser only: where a guest's decks live. Signing in offers to move them to the player's profile.
/// </summary>
public sealed class LocalDeckStore(IKeyValueStore store, TimeProvider? clock = null) : IDeckStore
{
    private const string Key = "mtg-companion.decks";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Description => "This device";

    /// <summary>How many decks are saved here, without loading them all into a list for display.</summary>
    public async Task<int> CountAsync() => (await LoadAsync()).Count;

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

/// <summary>
/// Decks in the Supabase "decks" table. A deck belongs to a player profile, so it shows up in every playgroup
/// that player is in; this store lists the ones whose owner is in <paramref name="group"/>, or with no group,
/// the signed-in player's own. Only whoever
/// manages the owner profile can change a deck (row-level security, see supabase/schema.sql).
/// </summary>
/// <param name="defaultOwner">The profile a new deck without an owner goes to: the signed-in user's own.</param>
public sealed class SupabaseDeckStore(SupabaseClient supabase, Playgroup? group, Guid defaultOwner) : IDeckStore
{
    private const string Columns = "id,owner_profile_id,name,format,notes,cover_image_url,cards,updated_at";
    private const string WithOwnerName = Columns + ",owner:player_profiles(name)";

    public string Description => group?.Name ?? "Your decks";
    public Playgroup? Group => group;

    public async Task<IReadOnlyList<Deck>> ListAsync(CancellationToken ct = default)
    {
        var rows = await supabase.GetAsync<List<DeckRow>>(group is null
            ? $"decks?select={WithOwnerName}&owner_profile_id=eq.{defaultOwner}&order=name"
            : $"decks?select={Columns},owner:player_profiles!inner(name,playgroup_profiles!inner(playgroup_id))" +
              $"&owner.playgroup_profiles.playgroup_id=eq.{group.Id}&order=name", ct);
        return rows.Select(r => r.ToDeck()).ToList();
    }

    public async Task<Deck?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await supabase.GetAsync<List<DeckRow>>($"decks?select={WithOwnerName}&id=eq.{id}", ct);
        return rows.FirstOrDefault()?.ToDeck();
    }

    public async Task<Deck> SaveAsync(Deck deck, CancellationToken ct = default)
    {
        var row = new DeckWrite(
            deck.Id, deck.OwnerProfileId ?? defaultOwner, deck.Name.Trim(), deck.Format, deck.Notes ?? "", deck.CoverImageUrl,
            deck.Cards.Select(CardRow.From).ToList());
        var rows = await supabase.UpsertAsync<DeckRow>($"decks?select={WithOwnerName}", row, ct);
        return rows.FirstOrDefault()?.ToDeck()
               ?? throw new SupabaseException("Only the deck's owner (or whoever looks after their profile) can change it.", 403);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => supabase.DeleteAsync($"decks?id=eq.{id}", ct);

    /// <summary>The database row. Cards are stored as a JSON array in one column; the owner's name comes from their profile.</summary>
    internal sealed record DeckRow(
        Guid Id,
        Guid? OwnerProfileId,
        string Name,
        string? Format,
        string? Notes,
        string? CoverImageUrl,
        List<CardRow>? Cards,
        DateTimeOffset UpdatedAt,
        OwnerRow? Owner)
    {
        public Deck ToDeck() => new()
        {
            Id = Id,
            Name = Name,
            OwnerName = Owner?.Name ?? "",
            OwnerProfileId = OwnerProfileId,
            Format = Format ?? DeckFormat.Commander.Id,
            Notes = Notes ?? "",
            CoverImageUrl = CoverImageUrl,
            UpdatedAt = UpdatedAt,
            Cards = (Cards ?? []).Select(c => c.ToDeckCard()).ToList(),
        };
    }

    internal sealed record OwnerRow(string Name);

    /// <summary>What's sent when saving. The owner's name isn't: it comes from their profile.</summary>
    internal sealed record DeckWrite(
        Guid Id,
        Guid OwnerProfileId,
        string Name,
        string Format,
        string Notes,
        // Sent even when null, so clearing the cover on an existing deck sticks.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CoverImageUrl,
        List<CardRow> Cards);

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
