using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;
using Kabatra.MagicTheGathering.Companion.Core.Decks;
using Kabatra.MagicTheGathering.Companion.Core.Profiles;

namespace Kabatra.MagicTheGathering.Companion.Tests;

/// <summary>Signing in is optional: guests keep decks on the device, and signed-in players don't need a playgroup.</summary>
public class GuestModeTests
{
    private static readonly SupabaseOptions Options = new("https://example.supabase.co", "sb_publishable_test123");

    [Fact]
    public async Task LocalDeckStore_SavesListsCountsAndDeletes()
    {
        var store = new LocalDeckStore(new MemoryKeyValueStore());
        var deck = new Deck { Name = "Burn", OwnerName = "Sam", Format = DeckFormat.Modern.Id };
        deck.Add(TestCards.Bolt, DeckSection.Main, 4);

        await store.SaveAsync(deck);
        deck.Name = "Changed after saving"; // the store keeps its own copy
        var loaded = await store.GetAsync(deck.Id);

        Assert.Equal("Burn", loaded!.Name);
        Assert.Equal("Sam", loaded.OwnerName); // a guest's deck owner is just a name
        Assert.Equal(4, loaded.Count(DeckSection.Main));
        Assert.Equal(1, await store.CountAsync());
        Assert.Equal("This device", store.Description);

        await store.DeleteAsync(deck.Id);
        Assert.Empty(await store.ListAsync());
        Assert.Equal(0, await store.CountAsync());
    }

    [Fact]
    public async Task LocalDeckStore_KeepsDecksSavedBeforeSignInExisted()
    {
        // Decks a device saved before this version used the same key and shape, so they're still there.
        var storage = new MemoryKeyValueStore();
        var old = new LocalDeckStore(storage);
        await old.SaveAsync(new Deck { Name = "Elves", OwnerName = "Alex" });

        var decks = await new LocalDeckStore(storage).ListAsync();
        Assert.Equal("Elves", decks.Single().Name);
    }

    [Fact]
    public async Task SupabaseDeckStore_WithoutAPlaygroup_ListsYourOwnDecks()
    {
        var me = Guid.NewGuid();
        var handler = new FakeHandler((_, _) => FakeHandler.Json("[]"));
        var store = new SupabaseDeckStore(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore()), null, me);

        await store.ListAsync();

        var query = Uri.UnescapeDataString(handler.Requests.Single().Request.RequestUri!.Query);
        Assert.Contains($"owner_profile_id=eq.{me}", query);
        Assert.DoesNotContain("playgroup", query);
        Assert.Equal("Your decks", store.Description);
    }

    [Fact]
    public async Task SupabaseProfileStore_WithoutAPlaygroup_ListsYoursAndTheOnesYouLookAfter()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/rpc/")
            ? FakeHandler.Json($$"""{ "id": "{{Guid.NewGuid()}}", "name": "Sam", "updated_at": "2026-01-01T00:00:00Z" }""")
            : FakeHandler.Json("[]"));
        var store = new SupabaseProfileStore(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore()), null);

        await store.ListAsync();
        await store.CreateAsync("Sam");

        var query = Uri.UnescapeDataString(handler.Requests[0].Request.RequestUri!.Query);
        Assert.Contains("or=(user_id.eq.", query);
        Assert.Contains("and(user_id.is.null,created_by.eq.", query);
        // A profile made outside a playgroup isn't put in one.
        Assert.DoesNotContain("group_id", handler.Requests[1].Body);
    }
}
