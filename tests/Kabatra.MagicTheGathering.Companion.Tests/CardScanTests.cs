using System.Net;
using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Cards;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class CardScanTests
{
    // ----- Reading OCR text -----

    [Theory]
    [InlineData("Lightning Bolt", "Lightning Bolt")]
    [InlineData("Lightning Bolt ©", "Lightning Bolt")]
    [InlineData("| Llanowar Elves @ é", "Llanowar Elves")]
    [InlineData("Uro, Titan of Nature’s Wrath 1@", "Uro, Titan of Nature's Wrath")]
    [InlineData("Sol Ring 1\nxx", "Sol Ring")]
    [InlineData("~ ®", null)]
    [InlineData("", null)]
    public void CleanName_DropsOcrNoise(string raw, string? expected) =>
        Assert.Equal(expected, ScanTextParser.CleanName(raw));

    [Theory]
    [InlineData("0117 U\n2X2 • EN  Christopher Moeller", "2x2", "117")]
    [InlineData("117/274 R\nM21 * EN Artist Name", "m21", "117")]
    [InlineData("0042 M\nMOM « JP", "mom", "42")]
    [InlineData("0280 C\nDMU EN", "dmu", "280")]
    [InlineData("0007 L\nLTR + EN", "ltr", "7")]
    // Real Tesseract output from Scryfall card images, including the newer rarity-first layout.
    [InlineData("C 0100 | WOE «EN % SLAWOMIR MANIAK", "woe", "100")]
    [InlineData("or ong. | U 0200 | MH3 « EN % RicHARD KANE FERGUSO!", "mh3", "200")]
    [InlineData("1s everyt mg ana | S 0001 | CMM +EN »% SEB McKINNON", "cmm", "1")]
    [InlineData("aon't, put that car | 050/302 U | NEO * EN % RYAN PANCOAST", "neo", "50")]
    [InlineData("EERIE TIEN 180A | \\ |  | 280/281 L |  | DMU *EN % MAGALI VILLENEUVE", "dmu", "280")]
    public void ParsesSetAndCollectorNumber(string bottom, string set, string number)
    {
        var reading = ScanTextParser.Parse(new ScanText("Lightning Bolt", bottom));
        Assert.Equal(("Lightning Bolt", set, number), (reading.Name, reading.SetCode, reading.CollectorNumber));
        Assert.True(reading.HasPrinting);
    }

    [Fact]
    public void OldFrame_HasNoPrinting()
    {
        var reading = ScanTextParser.Parse(new ScanText("Counterspell", "Illus. Mark Poole  © 1993 Wizards"));
        Assert.Null(reading.SetCode);
        Assert.False(reading.HasPrinting);
        Assert.Equal("Counterspell", reading.Name);
    }

    [Fact]
    public void NumberWithoutRarity_FallsBackToFirstNumberBeforeSet()
    {
        Assert.Equal("51", ScanTextParser.FindCollectorNumber("051\nISD • EN"));
    }

    [Theory]
    [InlineData("Lightning Bolt", "Lightning Bolt", 1.0)]
    [InlineData("Lightnin Bolt", "Lightning Bolt", 0.9)]
    [InlineData("Llanowar Elves", "Lightning Bolt", 0.0)]
    public void NameSimilarity_ToleratesTypos(string a, string b, double atLeast)
    {
        var score = ScanTextParser.NameSimilarity(a, b);
        if (atLeast == 0) Assert.True(score < CardScanMatcher.PrintingNameAgreement);
        else Assert.True(score >= atLeast, $"{score} < {atLeast}");
    }

    [Fact]
    public void NameSimilarity_UsesTheCommaToTellLookalikesApart()
    {
        const string read = "Elspeth, Snight Brant"; // real OCR of the Duel Decks Elspeth, Knight-Errant
        Assert.True(ScanTextParser.NameSimilarity(read, "Elspeth, Knight-Errant") > ScanTextParser.NameSimilarity(read, "Elspeth's Nightmare"));
    }

    // ----- Matching against Scryfall -----

    private static string CardJson(string name, string set, string number) => $$"""
        {
          "object": "card", "id": "{{Guid.NewGuid()}}", "oracle_id": "{{Guid.NewGuid()}}",
          "name": "{{name}}", "cmc": 1.0, "type_line": "Instant",
          "set": "{{set}}", "set_name": "Set {{set}}", "collector_number": "{{number}}",
          "legalities": {}
        }
        """;

    private static HttpResponseMessage NotFound =>
        FakeHandler.Json("""{ "object": "error", "details": "Not found" }""", HttpStatusCode.NotFound);

    private static (CardScanMatcher Matcher, FakeHandler Handler) NewMatcher(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler((request, _) => respond(request));
        var catalog = new CardCatalog(new ScryfallClient(new HttpClient(handler)), new MemoryKeyValueStore());
        return (new CardScanMatcher(catalog), handler);
    }

    [Fact]
    public async Task ExactPrinting_WinsWhenNameAgrees()
    {
        var (matcher, handler) = NewMatcher(r => r.RequestUri!.AbsolutePath == "/cards/2x2/117"
            ? FakeHandler.Json(CardJson("Lightning Bolt", "2x2", "117"))
            : NotFound);

        var match = await matcher.MatchAsync(new ScanReading("Lightnin Bolt", "2x2", "117"));

        Assert.NotNull(match);
        Assert.True(match.ExactPrinting);
        Assert.Equal(("Lightning Bolt", "2x2"), (match.Card.Name, match.Card.SetCode));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MisreadNumber_FallsBackToNameAndFindsTheCardInTheReadSet()
    {
        var (matcher, _) = NewMatcher(r => r.RequestUri!.AbsolutePath switch
        {
            "/cards/2x2/171" => FakeHandler.Json(CardJson("Some Other Card", "2x2", "171")),
            "/cards/named" => FakeHandler.Json(CardJson("Lightning Bolt", "lea", "161")),
            "/cards/search" when Uri.UnescapeDataString(r.RequestUri.Query).Contains("set:2x2") =>
                FakeHandler.Json($$"""{ "total_cards": 1, "has_more": false, "data": [{{CardJson("Lightning Bolt", "2x2", "117")}}] }"""),
            _ => NotFound,
        });

        var match = await matcher.MatchAsync(new ScanReading("Lightning Bolt", "2x2", "171"));

        Assert.NotNull(match);
        Assert.Equal(("Lightning Bolt", "2x2", "117"), (match.Card.Name, match.Card.SetCode, match.Card.CollectorNumber));
        Assert.True(match.ExactPrinting);
    }

    [Fact]
    public async Task NameOnly_UsesFuzzyMatch()
    {
        var (matcher, handler) = NewMatcher(r => r.RequestUri!.AbsolutePath == "/cards/named"
            ? FakeHandler.Json(CardJson("Counterspell", "mh2", "267"))
            : NotFound);

        var match = await matcher.MatchAsync(new ScanReading("Counterspel", null, null));

        Assert.Equal("Counterspell", match?.Card.Name);
        Assert.False(match!.ExactPrinting);
        Assert.Contains("fuzzy=Counterspel", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task GarbledName_TriesAutocomplete()
    {
        var (matcher, _) = NewMatcher(r =>
        {
            var query = Uri.UnescapeDataString(r.RequestUri!.Query);
            return r.RequestUri.AbsolutePath switch
            {
                "/cards/named" when query.Contains("Llanowar Elves") => FakeHandler.Json(CardJson("Llanowar Elves", "dom", "168")),
                "/cards/autocomplete" => FakeHandler.Json("""{ "data": ["Llanowar Elves", "Llanowar Envoy"] }"""),
                _ => NotFound,
            };
        });

        var match = await matcher.MatchAsync(new ScanReading("Llanowar Elvqz", null, null));

        Assert.Equal("Llanowar Elves", match?.Card.Name);
    }

    [Fact]
    public async Task GarbledEnding_RetriesAutocompleteWithShorterPrefixes()
    {
        // Real Tesseract output for the Duel Decks printing of Elspeth, Knight-Errant.
        var (matcher, handler) = NewMatcher(r =>
        {
            var query = Uri.UnescapeDataString(r.RequestUri!.Query);
            return r.RequestUri.AbsolutePath switch
            {
                "/cards/named" when query.Contains("fuzzy=Elspeth, Knight-Errant") => FakeHandler.Json(CardJson("Elspeth, Knight-Errant", "mma", "13")),
                "/cards/autocomplete" when query.EndsWith("q=Elspeth") =>
                    FakeHandler.Json("""{ "data": ["Elspeth Conquers Death", "Elspeth's Nightmare", "Elspeth, Knight-Errant", "Elspeth, Sun's Champion"] }"""),
                // Scryfall really does offer this lookalike for the shorter prefix.
                "/cards/autocomplete" when query.EndsWith("q=Elspeth, Snight") => FakeHandler.Json("""{ "data": ["Elspeth's Nightmare"] }"""),
                "/cards/autocomplete" => FakeHandler.Json("""{ "data": [] }"""),
                _ => NotFound,
            };
        });

        var match = await matcher.MatchAsync(new ScanReading("Elspeth, Snight Brant", null, null));

        Assert.Equal("Elspeth, Knight-Errant", match?.Card.Name);
        var autocompletes = handler.Requests.Select(r => Uri.UnescapeDataString(r.Request.RequestUri!.Query))
            .Where(q => q.StartsWith("?q=")).ToList();
        Assert.Equal(["?q=Elspeth, Snight Brant", "?q=Elspeth, Snight", "?q=Elspeth"], autocompletes);
    }

    [Fact]
    public async Task UnrelatedSuggestions_AreNotTaken()
    {
        var (matcher, _) = NewMatcher(r => r.RequestUri!.AbsolutePath == "/cards/autocomplete"
            ? FakeHandler.Json("""{ "data": ["Elspeth Conquers Death"] }""")
            : NotFound);

        Assert.Null(await matcher.MatchAsync(new ScanReading("Elspeth Qqqqqq Zzzzzzzzz Xxxxxx", null, null)));
    }

    [Fact]
    public async Task PrintingWithoutReadableName_IsAccepted()
    {
        var (matcher, _) = NewMatcher(r => r.RequestUri!.AbsolutePath == "/cards/dmu/280"
            ? FakeHandler.Json(CardJson("Plains", "dmu", "280"))
            : NotFound);

        var match = await matcher.MatchAsync(new ScanReading(null, "dmu", "280"));

        Assert.Equal("Plains", match?.Card.Name);
    }

    [Fact]
    public async Task GarbledTitle_FallsBackToThePrinting()
    {
        var (matcher, _) = NewMatcher(r => r.RequestUri!.AbsolutePath == "/cards/cmm/1"
            ? FakeHandler.Json(CardJson("The Prismatic Piper", "cmm", "1"))
            : NotFound);

        var match = await matcher.MatchAsync(new ScanReading("The HEETIF HIE", "cmm", "1"));

        Assert.Equal("The Prismatic Piper", match?.Card.Name);
    }

    // ----- Choosing another printing -----

    [Fact]
    public async Task Printings_AreAllPaperPrintsOfTheCard_NewestFirst_AcrossPages()
    {
        var oracleId = Guid.NewGuid();
        var handler = new FakeHandler((request, _) =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            return query.Contains("page=1")
                ? FakeHandler.Json($$"""{ "total_cards": 3, "has_more": true, "data": [{{CardJson("Elspeth, Knight-Errant", "mma", "13")}}, {{CardJson("Elspeth, Knight-Errant", "ddf", "4")}}] }""")
                : FakeHandler.Json($$"""{ "total_cards": 3, "has_more": false, "data": [{{CardJson("Elspeth, Knight-Errant", "ala", "9")}}] }""");
        });
        var client = new ScryfallClient(new HttpClient(handler));

        var printings = await client.GetPrintingsAsync(new CardInfo { Name = "Elspeth, Knight-Errant", OracleId = oracleId });

        Assert.Equal(["mma", "ddf", "ala"], printings.Select(p => p.SetCode));
        var first = Uri.UnescapeDataString(handler.Requests[0].Request.RequestUri!.Query);
        Assert.Contains($"oracleid:{oracleId} game:paper", first);
        Assert.Contains("unique=prints", first);
        Assert.Contains("order=released", first);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Printings_StopAtTheCap()
    {
        var page = string.Join(",", Enumerable.Range(1, 5).Select(i => CardJson("Mountain", "set" + i, i.ToString())));
        var handler = new FakeHandler((_, _) => FakeHandler.Json($$"""{ "total_cards": 999, "has_more": true, "data": [{{page}}] }"""));
        var client = new ScryfallClient(new HttpClient(handler));

        var printings = await client.GetPrintingsAsync(new CardInfo { Name = "Mountain", OracleId = Guid.NewGuid() }, maxCards: 8);

        Assert.Equal(8, printings.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Printings_WithoutAnOracleId_SearchByExactName()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{ "total_cards": 0, "has_more": false, "data": [] }"""));
        var client = new ScryfallClient(new HttpClient(handler));

        await client.GetPrintingsAsync(new CardInfo { Name = "Sol Ring" });

        Assert.Contains("!\"Sol Ring\" game:paper", Uri.UnescapeDataString(handler.Requests[0].Request.RequestUri!.Query));
    }

    [Fact]
    public async Task NothingMatches_ReturnsNull()
    {
        var (matcher, _) = NewMatcher(_ => NotFound);
        Assert.Null(await matcher.MatchAsync(new ScanReading("Xyzzy Qwerty", "zzz", "1")));
        Assert.Null(await matcher.MatchAsync(new ScanReading(null, null, null)));
    }

    [Fact]
    public async Task RejectedSetCode_IsTreatedAsNotFound()
    {
        var (matcher, _) = NewMatcher(r => r.RequestUri!.AbsolutePath switch
        {
            "/cards/named" => FakeHandler.Json(CardJson("Sol Ring", "c21", "263")),
            "/cards/search" => FakeHandler.Json("""{ "object": "error", "details": "Unknown set" }""", HttpStatusCode.BadRequest),
            _ => NotFound,
        });

        var match = await matcher.MatchAsync(new ScanReading("Sol Ring", "s0l", "9"));

        Assert.Equal("Sol Ring", match?.Card.Name);
        Assert.False(match!.ExactPrinting);
    }
}
