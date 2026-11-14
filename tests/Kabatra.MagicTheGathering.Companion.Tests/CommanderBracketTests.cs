using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class CommanderBracketTests
{
    private static readonly string[] Filler = ["Sol Ring", "Arcane Signet", "Command Tower", "Llanowar Elves", "Counterspell"];

    private static BracketAssessment Rate(params string[] extra) => CommanderBrackets.Assess([.. Filler, .. extra]);

    [Fact]
    public void PlainDeck_IsCore()
    {
        var result = Rate();
        Assert.Equal(CommanderBracket.Core, result.Bracket);
        Assert.Equal("Bracket 2 · Core", result.Label);
        Assert.Empty(result.GameChangers);
    }

    [Theory]
    [InlineData(1, CommanderBracket.Upgraded)]
    [InlineData(3, CommanderBracket.Upgraded)]
    [InlineData(4, CommanderBracket.Optimized)]
    public void GameChangerCount_SetsBracket(int count, CommanderBracket expected)
    {
        var gameChangers = new[] { "Rhystic Study", "Smothering Tithe", "Cyclonic Rift", "Demonic Tutor" }.Take(count).ToArray();
        var result = Rate(gameChangers);
        Assert.Equal(expected, result.Bracket);
        Assert.Equal(count, result.GameChangers.Count);
    }

    [Fact]
    public void Matching_IgnoresCaseDuplicatesAndMatchesFrontFaces()
    {
        // The same card twice still counts once; Tergrid is matched by its front face.
        var result = Rate("rhystic study", "Rhystic Study", "Tergrid, God of Fright // Tergrid's Lantern");
        Assert.Equal(2, result.GameChangers.Count);
        Assert.Contains("Tergrid, God of Fright // Tergrid's Lantern", result.GameChangers);
    }

    [Fact]
    public void BackFaceOfSplitCard_IsMatched()
    {
        var result = Rate("Boom // Bust");
        Assert.Equal(CommanderBracket.Optimized, result.Bracket);
        Assert.Single(result.MassLandDenial);
    }

    [Theory]
    [InlineData("Armageddon")]
    [InlineData("Blood Moon")]
    [InlineData("Winter Orb")]
    public void MassLandDenial_IsOptimized(string card)
    {
        var result = Rate(card);
        Assert.Equal(CommanderBracket.Optimized, result.Bracket);
        Assert.Equal([card], result.MassLandDenial);
    }

    [Fact]
    public void EarlyTwoCardCombo_IsOptimized()
    {
        var result = Rate("Thassa's Oracle", "Demonic Consultation");
        Assert.Equal(CommanderBracket.Optimized, result.Bracket);
        var combo = Assert.Single(result.Combos);
        Assert.True(combo.IsEarly);
    }

    [Fact]
    public void LateTwoCardCombo_IsUpgraded()
    {
        var result = Rate("Exquisite Blood", "Sanguine Bond");
        Assert.Equal(CommanderBracket.Upgraded, result.Bracket);
        Assert.Single(result.LateCombos);
        Assert.Empty(result.EarlyCombos);
    }

    [Fact]
    public void OnePieceOfACombo_DoesNotCount()
    {
        Assert.Empty(Rate("Sanguine Bond").Combos);
    }

    [Fact]
    public void AFewExtraTurns_StayCore_ButManyAreOptimized()
    {
        Assert.Equal(CommanderBracket.Core, Rate("Time Warp", "Temporal Manipulation", "Capture of Jingzhou").Bracket);
        Assert.Equal(CommanderBracket.Optimized, Rate("Time Warp", "Temporal Manipulation", "Capture of Jingzhou", "Temporal Mastery").Bracket);
    }

    [Fact]
    public void Tutors_DoNotRaiseTheBracketOnTheirOwn()
    {
        // Tutor limits were removed in October 2025; only tutors on the Game Changers list count.
        Assert.Equal(CommanderBracket.Core, Rate("Diabolic Tutor", "Grim Tutor").Bracket);
        Assert.Equal(CommanderBracket.Upgraded, Rate("Demonic Tutor").Bracket);
    }

    [Fact]
    public void HighestRuleWins_AndEveryReasonIsListed()
    {
        var result = Rate("Rhystic Study", "Exquisite Blood", "Sanguine Bond", "Armageddon");
        Assert.Equal(CommanderBracket.Optimized, result.Bracket);
        Assert.Equal(3, result.Reasons.Count);
    }

    [Fact]
    public void DeckAssessment_CountsCommanderButNotSideboard_AndOnlyForCommander()
    {
        var deck = new Deck { Format = DeckFormat.Commander.Id };
        deck.Cards.Add(new DeckCard { Name = "Braids, Cabal Minion", Section = DeckSection.Commander });
        deck.Cards.Add(new DeckCard { Name = "Armageddon", Section = DeckSection.Sideboard });
        deck.Cards.Add(new DeckCard { Name = "Sol Ring" });

        var result = CommanderBrackets.Assess(deck);
        Assert.NotNull(result);
        Assert.Equal(CommanderBracket.Upgraded, result.Bracket);
        Assert.Empty(result.MassLandDenial);

        deck.Format = DeckFormat.Modern.Id;
        Assert.Null(CommanderBrackets.Assess(deck));
    }

    [Fact]
    public void GameChangersList_HasEveryCurrentCard()
    {
        Assert.Equal(53, BracketCardLists.GameChangers.Count);
    }
}
