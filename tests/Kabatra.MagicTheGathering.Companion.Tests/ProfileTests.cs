using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Cards;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;
using Kabatra.MagicTheGathering.Companion.Core.Decks;
using Kabatra.MagicTheGathering.Companion.Core.History;
using Kabatra.MagicTheGathering.Companion.Core.Profiles;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class ProfileTests
{
    private static readonly SupabaseOptions Options = new("https://example.supabase.co", "sb_publishable_test123");
    private static readonly Playgroup Group = new(Guid.NewGuid(), "Friday Night", Guid.NewGuid());

    // ----- Collection -----

    [Fact]
    public void Collection_MergesTheSamePrinting_AndKeepsOtherPrintingsApart()
    {
        var profile = new PlayerProfile { Name = "Sam" };
        var otherBolt = TestCards.Bolt with { Id = Guid.NewGuid(), SetCode = "2x2", CollectorNumber = "117" };

        profile.AddToCollection(TestCards.Bolt);
        profile.AddToCollection(TestCards.Bolt, 3);
        profile.AddToCollection(otherBolt);

        Assert.Equal(2, profile.Collection.Count);
        Assert.Equal(4, profile.Collection.Single(c => c.ScryfallId == TestCards.Bolt.Id).Quantity);
        Assert.Equal(5, profile.CardCount);
    }

    [Fact]
    public void Collection_PastedLinesWithoutIdsMergeByNameAndPrinting()
    {
        var profile = new PlayerProfile();
        profile.AddToCollection(new DeckCard { Name = "Sol Ring", Quantity = 1, SetCode = "C21", CollectorNumber = "263" });
        profile.AddToCollection(new DeckCard { Name = "sol ring", Quantity = 2, SetCode = "c21", CollectorNumber = "263" });
        profile.AddToCollection(new DeckCard { Name = "Sol Ring", Quantity = 1 });

        Assert.Equal(2, profile.Collection.Count);
        Assert.Equal(3, profile.Collection[0].Quantity);
    }

    [Fact]
    public void Collection_AddingADeckCountsEverySection_AndCopiesTheLines()
    {
        var deck = new Deck { Name = "Goblins" };
        deck.Add(TestCards.Krenko, DeckSection.Commander);
        deck.Add(TestCards.Mountain, DeckSection.Main, 30);
        deck.Add(TestCards.Bolt, DeckSection.Sideboard, 2);
        var profile = new PlayerProfile();

        profile.AddDeckToCollection(deck);
        profile.SetQuantity(profile.Collection.Single(c => c.Name == "Mountain"), 10);

        Assert.Equal(13, profile.CardCount);
        Assert.All(profile.Collection, c => Assert.Equal(DeckSection.Main, c.Section));
        Assert.Equal(30, deck.Count(DeckSection.Main)); // the deck itself is untouched
    }

    [Fact]
    public void Collection_RemovingADeckUndoesAddingIt_AndKeepsOtherCopies()
    {
        var deck = new Deck { Name = "Goblins" };
        deck.Add(TestCards.Krenko, DeckSection.Commander);
        deck.Add(TestCards.Mountain, DeckSection.Main, 30);
        deck.Add(TestCards.Bolt, DeckSection.Sideboard, 2);
        var profile = new PlayerProfile();
        profile.AddToCollection(TestCards.Bolt, 3); // owned before the deck was added

        profile.AddDeckToCollection(deck);
        profile.RemoveDeckFromCollection(deck);

        Assert.Equal(3, profile.Collection.Single().Quantity);
        Assert.Equal(TestCards.Bolt.Id, profile.Collection.Single().ScryfallId);
    }

    [Fact]
    public void Collection_RemovingADeckNeverGoesBelowZero_AndMatchesLinesNotYetLookedUp()
    {
        var deck = new Deck { Name = "Burn" };
        deck.Add(TestCards.Bolt, DeckSection.Main, 4);
        var profile = new PlayerProfile();
        // The collection line was pasted without an id; the deck's line has one.
        profile.AddToCollection(new DeckCard
        {
            Name = TestCards.Bolt.Name, Quantity = 2, SetCode = TestCards.Bolt.SetCode, CollectorNumber = TestCards.Bolt.CollectorNumber,
        });
        profile.AddToCollection(TestCards.Mountain, 5);

        profile.RemoveDeckFromCollection(deck);

        Assert.Equal("Mountain", profile.Collection.Single().Name);
        Assert.Equal(5, profile.CardCount);
    }

    [Fact]
    public void Collection_ValueUsesEachPrintingsPrice()
    {
        var cheap = TestCards.Bolt with { Prices = new CardPrices { Usd = 1.50m } };
        var pricey = TestCards.Bolt with { Id = Guid.NewGuid(), SetCode = "lea", Prices = new CardPrices { Usd = 400m } };
        var profile = new PlayerProfile();
        profile.AddToCollection(cheap, 2);
        profile.AddToCollection(pricey);
        profile.AddToCollection(TestCards.Mountain);

        var value = DeckValue.Of(profile.Collection, new Dictionary<Guid, CardInfo> { [cheap.Id] = cheap, [pricey.Id] = pricey });

        Assert.Equal(403m, value.Usd);
        Assert.Equal(1, value.UnpricedCards);
    }

    // ----- Games and history -----

    [Fact]
    public void GameRecord_KeepsProfilesDecksAndTheWinner()
    {
        var sam = Guid.NewGuid();
        var deckId = Guid.NewGuid();
        var game = Game.Start(GameFormat.Standard, [
            new PlayerSeat("Sam", deckId, "Burn", ProfileId: sam),
            new PlayerSeat("Guest"),
        ]);
        game.AdjustLife(game.Players[1].Id, -20);

        var record = GameRecord.From(game, game.StartedAt.AddMinutes(30));

        Assert.Equal(game.Id, record.Id);
        Assert.Equal(GameFormat.Standard.Name, record.Format);
        Assert.Equal(TimeSpan.FromMinutes(30), record.Duration);
        Assert.Equal(sam, record.Winner!.ProfileId);
        Assert.Equal("Burn", record.Winner.DeckName);
        Assert.Null(record.Players[1].ProfileId);
        Assert.True(record.Players[1].Eliminated);
        Assert.Equal(0, record.Players[1].FinalLife);
    }

    [Fact]
    public void GameRecord_OnlyGamesThatGotGoingAreWorthRecording()
    {
        var game = Game.Start(GameFormat.Standard, ["A", "B"]);
        Assert.False(GameRecord.IsWorthRecording(game)); // set up and abandoned

        game.NextTurn();
        Assert.True(GameRecord.IsWorthRecording(game)); // played, no winner

        Assert.False(GameRecord.IsWorthRecording(Game.Start(GameFormat.Standard, ["Solo"])));
    }

    [Fact]
    public void Game_ProfileAndGameIdSurviveASaveAndReload()
    {
        var sam = Guid.NewGuid();
        var game = Game.Start(GameFormat.Commander, [new PlayerSeat("Sam", ProfileId: sam), new PlayerSeat("Alex")]);

        var restored = Game.FromJson(game.ToJson());

        Assert.Equal(game.Id, restored.Id);
        Assert.Equal(sam, restored.Players[0].ProfileId);
        Assert.Null(restored.Players[1].ProfileId);
    }

    [Fact]
    public void Stats_CountGamesWinsAndDecksForAProfile_EvenWithBorrowedDecks()
    {
        var sam = Guid.NewGuid();
        var alex = Guid.NewGuid();
        var samsDeck = Guid.NewGuid();
        var alexsDeck = Guid.NewGuid();
        GameRecord Played(Guid winner, Guid samDeck) => new()
        {
            Id = Guid.NewGuid(),
            Players =
            [
                new() { ProfileId = sam, Name = "Sam", DeckId = samDeck, DeckName = samDeck == samsDeck ? "Burn" : "Elves", Won = winner == sam },
                new() { ProfileId = alex, Name = "Alex", DeckId = alexsDeck, DeckName = "Elves", Won = winner == alex },
            ],
        };
        // Sam borrows Alex's deck for the last game: anyone can play any deck.
        var games = new[] { Played(sam, samsDeck), Played(alex, samsDeck), Played(sam, alexsDeck) };

        var stats = PlayerStats.For(sam, games);

        Assert.Equal(3, stats.Games);
        Assert.Equal(2, stats.Wins);
        Assert.Equal(2.0 / 3, stats.WinRate!.Value, 3);
        Assert.Equal(new DeckRecord(samsDeck, "Burn", 2, 1), stats.Decks[0]);
        Assert.Equal(new DeckRecord(alexsDeck, "Elves", 1, 1), stats.Decks[1]);

        // Alex's deck was played four times, three by Alex and once by Sam, and won twice (once each).
        Assert.Equal(new DeckRecord(alexsDeck, "Elves", 4, 2), PlayerStats.ForDeck(alexsDeck, "Elves", games));

        Assert.Null(PlayerStats.For(Guid.NewGuid(), games).WinRate);
    }

    // ----- Stores -----

    private const string ProfileJson = """
        { "id": "6f0c5b8e-0000-4000-8000-000000000001", "name": "Sam", "user_id": null,
          "created_by": "6f0c5b8e-0000-4000-8000-0000000000aa", "player_code": "PLAY2345",
          "collection": [{ "name": "Krenko, Mob Boss", "quantity": 2, "section": "main" }],
          "updated_at": "2026-01-01T00:00:00+00:00", "playgroup_profiles": [{ "playgroup_id": "6f0c5b8e-0000-4000-8000-0000000000bb" }] }
        """;

    [Fact]
    public async Task SupabaseProfileStore_ListsTheGroupsProfilesThroughTheMembershipTable()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json($"[{ProfileJson}]"));
        var store = new SupabaseProfileStore(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore()), Group);

        var profile = (await store.ListAsync()).Single();

        var query = Uri.UnescapeDataString(handler.Requests.Single().Request.RequestUri!.Query);
        Assert.Contains("playgroup_profiles!inner(playgroup_id)", query);
        Assert.Contains($"playgroup_profiles.playgroup_id=eq.{Group.Id}", query);
        Assert.DoesNotContain("claim_code", query); // only the profile's admin may read it, through profile_claim_code()
        Assert.Equal("Sam", profile.Name);
        Assert.False(profile.IsClaimed);
        Assert.Equal("PLAY2345", profile.PlayerCode);
        Assert.Equal(2, profile.CardCount);
    }

    [Fact]
    public async Task SupabaseProfileStore_SavePatchesOnlyTheNameAndCollection()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json($"[{ProfileJson}]"));
        var store = new SupabaseProfileStore(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore()), Group);
        var profile = new PlayerProfile { Id = Guid.NewGuid(), Name = " Sam ", UserId = Guid.NewGuid(), PlayerCode = "X" };
        profile.AddToCollection(TestCards.Krenko, 2);

        var saved = await store.SaveAsync(profile);

        var (request, body) = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Contains($"id=eq.{profile.Id}", request.RequestUri!.Query);
        Assert.Contains("\"name\":\"Sam\"", body);
        Assert.Contains("\"collection\":[{\"name\":\"Krenko, Mob Boss\",\"quantity\":2", body);
        // Ownership is never sent from the browser; the database decides it.
        Assert.DoesNotContain("user_id", body);
        Assert.DoesNotContain("created_by", body);
        Assert.DoesNotContain("playgroup_id", body);
        Assert.Equal(2, saved.CardCount);
    }

    [Fact]
    public async Task SupabaseProfileStore_SavingAProfileYouCantChangeFails()
    {
        // Row-level security filters out rows the user may not update, so PostgREST returns none.
        var handler = new FakeHandler((_, _) => FakeHandler.Json("[]"));
        var store = new SupabaseProfileStore(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore()), Group);

        var error = await Assert.ThrowsAsync<SupabaseException>(() => store.SaveAsync(new PlayerProfile { Name = "Sam" }));
        Assert.Equal(403, error.Status);
    }

    [Fact]
    public async Task SupabaseProfileStore_CreatesUnclaimedProfilesInTheGroup()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(ProfileJson));
        var store = new SupabaseProfileStore(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore()), Group);

        var created = await store.CreateAsync("  Sam ");

        var (request, body) = handler.Requests.Single();
        Assert.Equal("/rest/v1/rpc/create_profile", request.RequestUri!.AbsolutePath);
        Assert.Contains("\"profile_name\":\"Sam\"", body);
        Assert.Contains($"\"group_id\":\"{Group.Id}\"", body);
        Assert.Equal("Sam", created.Name);
    }

    [Fact]
    public void Profiles_AreManagedByTheirPlayer_OrByTheirMakerUntilClaimed()
    {
        var me = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();

        Assert.True(new PlayerProfile { UserId = me, CreatedBy = someoneElse }.CanBeManagedBy(me));
        Assert.True(new PlayerProfile { UserId = null, CreatedBy = me }.CanBeManagedBy(me));
        // Once claimed, the maker loses control of it.
        Assert.False(new PlayerProfile { UserId = someoneElse, CreatedBy = me }.CanBeManagedBy(me));
        Assert.False(new PlayerProfile { UserId = null, CreatedBy = someoneElse }.CanBeManagedBy(me));
        Assert.False(new PlayerProfile { UserId = null, CreatedBy = me }.CanBeManagedBy(null));
    }

    [Fact]
    public async Task SupabaseGameHistoryStore_SavesSeatsAndReadsThemBack()
    {
        string? sent = null;
        var handler = new FakeHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Post) { sent = body; return FakeHandler.Json($"[{body}]"); }
            Assert.Contains("order=ended_at.desc", request.RequestUri!.Query);
            return FakeHandler.Json($"[{sent}]");
        });
        var store = new SupabaseGameHistoryStore(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore()), Group);
        var sam = Guid.NewGuid();
        var record = new GameRecord
        {
            Id = Guid.NewGuid(),
            Format = "Commander",
            Turns = 7,
            Players = [new() { ProfileId = sam, Name = "Sam", DeckName = "Goblins", Won = true }, new() { Name = "Guest", Eliminated = true }],
        };

        await store.SaveAsync(record);
        var loaded = (await store.ListAsync()).Single();

        Assert.Contains("\"won\":true", sent);
        Assert.Contains("\"profile_id\":null", sent); // the guest seat
        Assert.Equal(sam, loaded.Winner!.ProfileId);
        Assert.Equal(7, loaded.Turns);
        Assert.True(loaded.Players[1].Eliminated);
    }

    // ----- Recording games reliably -----

    [Fact]
    public async Task Recorder_SendsQueuedGames_AndKeepsThemWhenSavingFails()
    {
        var device = new MemoryKeyValueStore();
        var recorder = new GameRecorder(device);
        var history = new FlakyHistory { Fail = true };
        var game = new GameRecord { Id = Guid.NewGuid() };

        await recorder.QueueAsync(game, null);
        await Assert.ThrowsAsync<HttpRequestException>(() => recorder.FlushAsync(history, null));
        Assert.Equal(1, await recorder.CountPendingAsync(null));

        history.Fail = false;
        // A new recorder (e.g. after a reload) still finds the queued game.
        Assert.Equal(1, await new GameRecorder(device).FlushAsync(history, null));
        Assert.Equal(game.Id, history.Saved.Single().Id);
        Assert.Equal(0, await recorder.CountPendingAsync(null));
    }

    [Fact]
    public async Task Recorder_OnlySendsGamesToTheirOwnPlaygroup_AndQueuesAGameOnce()
    {
        var recorder = new GameRecorder(new MemoryKeyValueStore());
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();
        var game = new GameRecord { Id = Guid.NewGuid() };
        await recorder.QueueAsync(game, groupA);
        await recorder.QueueAsync(game, groupA);
        await recorder.QueueAsync(new GameRecord { Id = Guid.NewGuid() }, groupB);

        var history = new FlakyHistory();
        Assert.Equal(1, await recorder.FlushAsync(history, groupA));
        Assert.Equal(game.Id, history.Saved.Single().Id);
        Assert.Equal(1, await recorder.CountPendingAsync(groupB));
    }

    private sealed class FlakyHistory : IGameHistoryStore
    {
        public bool Fail { get; set; }
        public List<GameRecord> Saved { get; } = [];

        public Task<IReadOnlyList<GameRecord>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GameRecord>>(Saved);

        public Task SaveAsync(GameRecord record, CancellationToken ct = default)
        {
            if (Fail) throw new HttpRequestException("offline");
            Saved.Add(record);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }
}

public class SchemaTests
{
    private static readonly SupabaseOptions Options = new("https://example.supabase.co", "sb_publishable_test123");

    [Fact]
    public void SchemaProblems_AreRecognised()
    {
        Assert.True(new SupabaseException("Could not find the table 'public.player_profiles' in the schema cache", 404).NeedsSchemaUpdate);
        Assert.False(new SupabaseException("permission denied for table decks", 403).NeedsSchemaUpdate);
    }
}
