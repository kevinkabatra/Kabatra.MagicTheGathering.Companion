using System.Net;
using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Cards;
using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class CardLookupTests
{
    private const string BoltId = "77c6fa74-5543-42ac-9ead-0e890b188e99";
    private const string DelverId = "28059d09-2c7d-4c61-af55-8942107a7c1f";

    private static readonly string BoltJson = $$"""
        {
          "object": "card", "id": "{{BoltId}}", "oracle_id": "4457ed35-7c10-48c8-9776-456485fdf070",
          "name": "Lightning Bolt", "mana_cost": "{R}", "cmc": 1.0, "type_line": "Instant",
          "oracle_text": "Lightning Bolt deals 3 damage to any target.",
          "color_identity": ["R"], "keywords": [],
          "legalities": { "standard": "not_legal", "modern": "legal", "commander": "legal" },
          "set": "2x2", "set_name": "Double Masters 2022", "collector_number": "117", "rarity": "uncommon",
          "image_uris": { "small": "https://img/s.jpg", "normal": "https://img/n.jpg", "art_crop": "https://img/a.jpg" }
        }
        """;

    // A double-faced card: images and text live on the faces.
    private static readonly string DelverJson = $$"""
        {
          "object": "card", "id": "{{DelverId}}", "oracle_id": "9a2ef3e8-0a4d-4e1d-a3d9-1d5b7e2b7d55",
          "name": "Delver of Secrets // Insectile Aberration", "cmc": 1.0,
          "type_line": "Creature — Human Wizard // Creature — Human Insect",
          "color_identity": ["U"], "legalities": { "modern": "legal" },
          "set": "isd", "collector_number": "51", "rarity": "common",
          "card_faces": [
            { "name": "Delver of Secrets", "mana_cost": "{U}", "type_line": "Creature — Human Wizard", "oracle_text": "At the beginning of your upkeep...", "image_uris": { "small": "https://img/d.jpg", "normal": "https://img/dn.jpg", "art_crop": "https://img/da.jpg" } },
            { "name": "Insectile Aberration", "mana_cost": "", "type_line": "Creature — Human Insect", "oracle_text": "Flying" }
          ]
        }
        """;

    private static (ScryfallClient Client, FakeHandler Handler) NewClient(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new ScryfallClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Search_MapsCardsIncludingDoubleFacedImages()
    {
        var (client, handler) = NewClient((_, _) => FakeHandler.Json($$"""{ "object": "list", "total_cards": 2, "has_more": false, "data": [{{BoltJson}}, {{DelverJson}}] }"""));

        var page = await client.SearchAsync("t:instant c:r");

        Assert.Equal(2, page.TotalCards);
        var bolt = page.Cards[0];
        Assert.Equal(("Lightning Bolt", "{R}", 1m, "2x2", "117"), (bolt.Name, bolt.ManaCost, bolt.ManaValue, bolt.SetCode, bolt.CollectorNumber));
        Assert.Equal("legal", bolt.LegalityIn("modern"));
        Assert.Equal("not_legal", bolt.LegalityIn("pauper")); // missing means not legal

        var delver = page.Cards[1];
        Assert.Equal("Delver of Secrets", delver.FrontName);
        Assert.Equal("{U}", delver.ManaCost);
        Assert.Equal("https://img/dn.jpg", delver.ImageNormal);
        Assert.Equal("Creature", delver.MainType);

        Assert.Contains("q=t%3Ainstant%20c%3Ar", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task Search_NotFoundMeansNoResults()
    {
        var (client, _) = NewClient((_, _) => FakeHandler.Json("""{ "object": "error", "code": "not_found", "details": "No cards found." }""", HttpStatusCode.NotFound));
        var page = await client.SearchAsync("xyzzy");
        Assert.Empty(page.Cards);
    }

    [Fact]
    public async Task BadRequest_ThrowsWithScryfallsMessage()
    {
        var (client, _) = NewClient((_, _) => FakeHandler.Json("""{ "object": "error", "details": "All of your terms were ignored." }""", HttpStatusCode.BadRequest));
        var error = await Assert.ThrowsAsync<ScryfallException>(() => client.SearchAsync("??"));
        Assert.Equal("All of your terms were ignored.", error.Message);
    }

    [Fact]
    public async Task Collection_SendsSnakeCaseIdentifiersAndMatchesResults()
    {
        var (client, handler) = NewClient((_, _) => FakeHandler.Json($$"""{ "data": [{{BoltJson}}], "not_found": [{ "name": "Nope" }] }"""));

        var results = await client.GetCollectionAsync([CardIdentifier.ByPrinting("2X2", "117"), CardIdentifier.ByName("Nope")]);

        Assert.Equal("Lightning Bolt", results[0].Card?.Name);
        Assert.Null(results[1].Card);
        var body = handler.Requests[0].Body!;
        Assert.Contains("\"set\":\"2x2\"", body);
        Assert.Contains("\"collector_number\":\"117\"", body);
        Assert.Contains("\"name\":\"Nope\"", body);
    }

    [Fact]
    public async Task Catalog_ResolvesDeckInPasses_AndCachesCards()
    {
        var store = new MemoryKeyValueStore();
        var (client, handler) = NewClient((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/cards/collection" && body!.Contains("2x2")) return FakeHandler.Json($$"""{ "data": [{{BoltJson}}] }""");
            if (path == "/cards/collection") return FakeHandler.Json("""{ "data": [] }""");
            if (path == "/cards/named" && request.RequestUri.Query.Contains("Delver")) return FakeHandler.Json(DelverJson);
            return FakeHandler.Json("""{ "object": "error" }""", HttpStatusCode.NotFound);
        });
        var catalog = new CardCatalog(client, store);

        var deck = new Deck
        {
            Cards =
            [
                new DeckCard { Name = "Lightning Bolt", Quantity = 4, SetCode = "2x2", CollectorNumber = "117" },
                new DeckCard { Name = "Delver of Secrets", Quantity = 4 },
                new DeckCard { Name = "Totally Fake", Quantity = 1 },
            ],
        };

        var result = await catalog.ResolveAsync(deck);

        Assert.Equal(2, result.Cards.Count);
        Assert.Equal("Totally Fake", Assert.Single(result.NotFound).Name);
        Assert.Equal(Guid.Parse(DelverId), deck.Cards[1].ScryfallId);
        Assert.Equal("Delver of Secrets // Insectile Aberration", deck.Cards[1].Name); // canonical name filled in
        Assert.Equal("51", deck.Cards[1].CollectorNumber);

        // A second resolve of the same deck (minus the fake card) is answered from the cache.
        deck.Cards.RemoveAt(2);
        var calls = handler.Requests.Count;
        var again = await new CardCatalog(client, store).ResolveAsync(deck);
        Assert.Equal(2, again.Cards.Count);
        Assert.Equal(calls, handler.Requests.Count);
    }

    [Fact]
    public async Task Catalog_DropsExpiredCacheEntries()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new MemoryKeyValueStore();
        var (client, handler) = NewClient((_, _) => FakeHandler.Json(BoltJson));
        var id = Guid.Parse(BoltId);

        await new CardCatalog(client, store, clock).GetAsync(id);
        await new CardCatalog(client, store, clock).GetAsync(id);
        Assert.Single(handler.Requests);

        clock.Now += CardCatalog.CacheLifetime + TimeSpan.FromMinutes(1);
        await new CardCatalog(client, store, clock).GetAsync(id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void CardInfo_CommanderAndTypeRules()
    {
        Assert.True(TestCards.Krenko.CanBeCommander);
        Assert.False(TestCards.Bolt.CanBeCommander);
        Assert.True(TestCards.Make("Teferi, Temporal Archmage", "Legendary Planeswalker — Teferi", text: "Teferi, Temporal Archmage can be your commander.").CanBeCommander);
        Assert.Null(TestCards.Mountain.CopyLimitOverride);
        Assert.Equal(7, TestCards.Dwarves.CopyLimitOverride);
        Assert.Equal("Land", TestCards.Make("Dryad Arbor", "Land Creature — Forest Dryad").MainType);
    }
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
