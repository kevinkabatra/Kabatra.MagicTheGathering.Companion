using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class DeckListTextTests
{
    [Fact]
    public void Parse_ReadsArenaExportWithSectionsAndPrintings()
    {
        var text = """
            About
            Name Goblin Gang

            Commander
            1 Krenko, Mob Boss (DDT) 52

            Deck
            1 Sol Ring (C21) 263
            30 Mountain

            Sideboard
            1 Pyroblast
            """;

        var result = DeckListText.Parse(text);

        Assert.Empty(result.Problems);
        Assert.Equal("Goblin Gang", result.Name);
        Assert.Equal(4, result.Cards.Count);
        var krenko = result.Cards[0];
        Assert.Equal(("Krenko, Mob Boss", DeckSection.Commander, "ddt", "52"), (krenko.Name, krenko.Section, krenko.SetCode, krenko.CollectorNumber));
        Assert.Equal(30, result.Cards.Single(c => c.Name == "Mountain").Quantity);
        Assert.Equal(DeckSection.Sideboard, result.Cards.Single(c => c.Name == "Pyroblast").Section);
    }

    [Fact]
    public void Parse_MtgoStyle_BlankLineStartsSideboard()
    {
        var result = DeckListText.Parse("4 Lightning Bolt\n20 Mountain\n\n2 Smash to Smithereens\n");
        Assert.Equal(DeckSection.Main, result.Cards[1].Section);
        Assert.Equal(DeckSection.Sideboard, result.Cards[2].Section);
    }

    [Fact]
    public void Parse_HandlesXQuantitiesFoilMarkersSbPrefixAndComments()
    {
        var text = """
            // Burn
            4x Lightning Bolt (2X2) 117 *F*
            Mountain
            SB: 2 Smash to Smithereens
            """;
        var result = DeckListText.Parse(text);

        Assert.Empty(result.Problems);
        Assert.Equal(("Lightning Bolt", 4, "117"), (result.Cards[0].Name, result.Cards[0].Quantity, result.Cards[0].CollectorNumber));
        Assert.Equal(1, result.Cards[1].Quantity); // no number means one copy
        Assert.Equal(DeckSection.Sideboard, result.Cards[2].Section);
    }

    [Fact]
    public void Parse_MergesRepeatedLinesAndKeepsSplitCardNames()
    {
        var result = DeckListText.Parse("2 Fire // Ice\n1 Fire // Ice\n");
        var card = Assert.Single(result.Cards);
        Assert.Equal(("Fire // Ice", 3), (card.Name, card.Quantity));
    }

    [Fact]
    public void Parse_HeadersWithCountsAndColons()
    {
        var result = DeckListText.Parse("Commander (1)\n1 Krenko, Mob Boss\nMainboard:\n1 Sol Ring\nSIDEBOARD:\n1 Pyroblast");
        Assert.Equal([DeckSection.Commander, DeckSection.Main, DeckSection.Sideboard], result.Cards.Select(c => c.Section));
    }

    [Fact]
    public void Parse_ReportsLinesItCannotRead()
    {
        var result = DeckListText.Parse("0 Lightning Bolt\n4 Mountain");
        Assert.Single(result.Problems);
        Assert.Single(result.Cards);
    }

    [Fact]
    public void Write_ThenParse_RoundTrips()
    {
        var deck = new Deck { Name = "Goblins" };
        deck.Add(TestCards.Krenko, DeckSection.Commander);
        deck.Add(TestCards.Mountain, DeckSection.Main, 30);
        deck.Add(TestCards.Bolt, DeckSection.Sideboard);

        var text = DeckListText.Write(deck);
        Assert.Contains("1 Krenko, Mob Boss (TST) 1", text);

        var parsed = DeckListText.Parse(text);
        Assert.Empty(parsed.Problems);
        Assert.Equal("Goblins", parsed.Name);
        Assert.Equal(
            deck.Cards.Select(c => (c.Name, c.Quantity, c.Section)),
            parsed.Cards.Select(c => (c.Name, c.Quantity, c.Section)));
    }
}
