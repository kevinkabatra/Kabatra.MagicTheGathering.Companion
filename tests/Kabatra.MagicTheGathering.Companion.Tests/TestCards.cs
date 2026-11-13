using System.Net;
using System.Text;
using Kabatra.MagicTheGathering.Companion.Core.Cards;

namespace Kabatra.MagicTheGathering.Companion.Tests;

/// <summary>Hand-made card data so deck tests don't need the network.</summary>
internal static class TestCards
{
    private static readonly string[] AllFormats =
        ["standard", "pioneer", "modern", "legacy", "vintage", "pauper", "commander", "standardbrawl", "brawl", "oathbreaker"];

    public static CardInfo Make(
        string name,
        string type = "Instant",
        string[]? colors = null,
        string text = "",
        string[]? keywords = null,
        Dictionary<string, string>? legal = null)
    {
        var legalities = AllFormats.ToDictionary(f => f, _ => "legal");
        foreach (var (k, v) in legal ?? []) legalities[k] = v;
        return new CardInfo
        {
            Id = Guid.NewGuid(),
            OracleId = Guid.NewGuid(),
            Name = name,
            TypeLine = type,
            OracleText = text,
            ColorIdentity = colors ?? [],
            Keywords = keywords ?? [],
            Legalities = legalities,
            SetCode = "tst",
            CollectorNumber = "1",
        };
    }

    public static CardInfo Bolt { get; } = Make("Lightning Bolt", "Instant", ["R"]);
    public static CardInfo Counterspell { get; } = Make("Counterspell", "Instant", ["U"]);
    public static CardInfo Mountain { get; } = Make("Mountain", "Basic Land — Mountain", []);
    public static CardInfo SolRing { get; } = Make("Sol Ring", "Artifact", [], legal: new() { ["standard"] = "not_legal", ["commander"] = "legal" });
    public static CardInfo Krenko { get; } = Make("Krenko, Mob Boss", "Legendary Creature — Goblin Warrior", ["R"]);
    public static CardInfo Rats { get; } = Make("Relentless Rats", "Creature — Rat", ["B"], "A deck can have any number of cards named Relentless Rats.");
    public static CardInfo Dwarves { get; } = Make("Seven Dwarves", "Creature — Dwarf", ["R"], "A deck can have up to seven cards named Seven Dwarves.");
}

/// <summary>Answers HTTP requests from a function, and records them for assertions.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
