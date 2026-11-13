using System.Net;
using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class PreconCatalogTests
{
    private const string DeckListJson = """
        {
          "meta": { "date": "2026-11-12", "version": "5.3.0" },
          "data": [
            { "code": "10E", "fileName": "ArcanisSGuile_10E", "name": "Arcanis's Guile", "releaseDate": "2007-07-13", "type": "Theme Deck", "source": "http://example" },
            { "code": "ZNC", "fileName": "SneakAttack_ZNC", "name": "Sneak Attack", "releaseDate": "2020-09-25", "type": "Commander Deck" },
            { "code": "ZNC", "fileName": "LandSWrath_ZNC", "name": "Land's Wrath", "releaseDate": "2020-09-25", "type": "Commander Deck" },
            { "code": "XXX", "fileName": "", "name": "Broken entry", "type": "Theme Deck" }
          ]
        }
        """;

    // Trimmed from a real MTGJSON deck file; the real ones carry many more fields per card.
    private const string SneakAttackJson = """
        {
          "meta": { "date": "2026-11-12", "version": "5.3.0" },
          "data": {
            "code": "ZNC", "name": "Sneak Attack", "type": "Commander Deck", "releaseDate": "2020-09-25",
            "commander": [
              { "name": "Anowon, the Ruin Thief", "count": 1, "isFoil": true, "setCode": "ZNC", "number": "1",
                "rarity": "mythic", "text": "Other Rogues you control get +1/+0.",
                "identifiers": { "scryfallId": "bca84fc4-4710-44c8-b90a-73ef888714d8", "mtgjsonV4Id": "x" } }
            ],
            "displayCommander": [],
            "mainBoard": [
              { "name": "Sol Ring", "count": 1, "isFoil": false, "setCode": "ZNC", "number": "125",
                "identifiers": { "scryfallId": "003dc436-a3af-4e65-b4f8-387155fbcb85" } },
              { "name": "Swamp", "count": 9, "isFoil": false, "setCode": "ZNR", "number": "270",
                "identifiers": { "scryfallId": "3536a97f-565b-45c6-a54b-96afd2a268ec" } },
              { "name": "Swamp", "count": 1, "isFoil": true, "setCode": "ZNR", "number": "270",
                "identifiers": { "scryfallId": "3536a97f-565b-45c6-a54b-96afd2a268ec" } },
              { "name": "Mystery Card", "count": 2, "setCode": "ZNC", "number": "", "identifiers": {} }
            ],
            "sideBoard": [],
            "tokens": [ { "name": "Rogue", "count": 1 } ],
            "planes": [], "schemes": []
          }
        }
        """;

    private static (PreconCatalog Catalog, FakeHandler Handler) NewCatalog(
        Func<HttpRequestMessage, string?, HttpResponseMessage> respond, IKeyValueStore? store = null, TimeProvider? clock = null)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = PreconCatalog.DefaultBaseAddress };
        return (new PreconCatalog(http, store ?? new MemoryKeyValueStore(), clock), handler);
    }

    [Fact]
    public async Task List_IsNewestFirstAndSkipsBrokenEntries()
    {
        var (catalog, handler) = NewCatalog((_, _) => FakeHandler.Json(DeckListJson));

        var list = await catalog.ListAsync();

        Assert.Equal(["Land's Wrath", "Sneak Attack", "Arcanis's Guile"], list.Select(p => p.Name));
        Assert.Equal(new PreconSummary("SneakAttack_ZNC", "Sneak Attack", "ZNC", "Commander Deck", new DateOnly(2020, 9, 25)), list[1]);
        Assert.Equal("https://mtgjson.com/api/v5/DeckList.json", handler.Requests[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task List_IsCachedOnTheDeviceForAWeek()
    {
        var store = new MemoryKeyValueStore();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-13T00:00:00Z"));
        var (first, firstHandler) = NewCatalog((_, _) => FakeHandler.Json(DeckListJson), store, clock);
        await first.ListAsync();

        // A new page load inside the week: no network.
        var (second, secondHandler) = NewCatalog((_, _) => throw new HttpRequestException("offline"), store, clock);
        Assert.Equal(3, (await second.ListAsync()).Count);
        Assert.Empty(secondHandler.Requests);

        // After a week it refetches.
        clock.Now += PreconCatalog.ListLifetime + TimeSpan.FromHours(1);
        var (third, thirdHandler) = NewCatalog((_, _) => FakeHandler.Json(DeckListJson), store, clock);
        await third.ListAsync();
        Assert.Single(thirdHandler.Requests);
        Assert.Single(firstHandler.Requests);
    }

    [Fact]
    public async Task List_FallsBackToAStaleCacheWhenOffline()
    {
        var store = new MemoryKeyValueStore();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-13T00:00:00Z"));
        await NewCatalog((_, _) => FakeHandler.Json(DeckListJson), store, clock).Catalog.ListAsync();
        clock.Now += TimeSpan.FromDays(30);

        var (offline, _) = NewCatalog((_, _) => throw new HttpRequestException("offline"), store, clock);

        Assert.Equal(3, (await offline.ListAsync()).Count);
    }

    [Fact]
    public async Task List_WithNoCacheAndAServerError_Throws()
    {
        var (catalog, _) = NewCatalog((_, _) => FakeHandler.Json("{}", HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<PreconException>(() => catalog.ListAsync());
    }

    [Fact]
    public async Task GetDeck_BuildsACommanderDeckWithExactPrintings()
    {
        var (catalog, handler) = NewCatalog((_, _) => FakeHandler.Json(SneakAttackJson));
        var summary = new PreconSummary("SneakAttack_ZNC", "Sneak Attack", "ZNC", "Commander Deck", null);

        var deck = await catalog.GetDeckAsync(summary);

        Assert.Equal("https://mtgjson.com/api/v5/decks/SneakAttack_ZNC.json", handler.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal("Sneak Attack", deck.Name);
        Assert.Equal(DeckFormat.Commander.Id, deck.Format);

        var commander = Assert.Single(deck.Commanders);
        Assert.Equal(("Anowon, the Ruin Thief", Guid.Parse("bca84fc4-4710-44c8-b90a-73ef888714d8"), "ZNC", "1"),
            (commander.Name, commander.ScryfallId, commander.SetCode, commander.CollectorNumber));

        // Foil and non-foil Swamps merge into one line; a card with no Scryfall id is kept by name.
        Assert.Equal(13, deck.Count(DeckSection.Main));
        Assert.Equal(10, Assert.Single(deck.In(DeckSection.Main), c => c.Name == "Swamp").Quantity);
        var mystery = Assert.Single(deck.In(DeckSection.Main), c => c.Name == "Mystery Card");
        Assert.Equal((2, (Guid?)null, (string?)null), (mystery.Quantity, mystery.ScryfallId, mystery.CollectorNumber));
        Assert.Equal(0, deck.Count(DeckSection.Sideboard));
    }

    [Fact]
    public async Task GetDeck_UnknownDeck_Throws()
    {
        var (catalog, _) = NewCatalog((_, _) => FakeHandler.Json("", HttpStatusCode.NotFound));
        var error = await Assert.ThrowsAsync<PreconException>(() =>
            catalog.GetDeckAsync(new PreconSummary("Nope_XXX", "Nope", "XXX", "Theme Deck", null)));
        Assert.Contains("Nope", error.Message);
    }

    [Theory]
    [InlineData("Commander Deck", true, "commander")]
    [InlineData("MTGO Commander Deck", true, "commander")]
    [InlineData("Brawl Deck", true, "brawl")]
    [InlineData("Historic Brawl Precon Deck", true, "historicbrawl")]
    [InlineData("Theme Deck", false, "casual")]
    [InlineData("Duel Deck", false, "casual")]
    public void FormatFor_MatchesTheProduct(string type, bool hasCommander, string expected) =>
        Assert.Equal(expected, PreconCatalog.FormatFor(type, hasCommander).Id);
}
