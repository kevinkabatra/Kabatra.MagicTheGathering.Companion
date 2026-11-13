using Kabatra.MagicTheGathering.Companion.Core.Cards;
using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class DeckValidatorTests
{
    private static (Deck Deck, Dictionary<Guid, CardInfo> Cards) Build(DeckFormat format, params (CardInfo Card, int Qty, DeckSection Section)[] lines)
    {
        var deck = new Deck { Format = format.Id };
        var cards = new Dictionary<Guid, CardInfo>();
        foreach (var (card, qty, section) in lines)
        {
            deck.Add(card, section, qty);
            cards[card.Id] = card;
        }
        return (deck, cards);
    }

    private static IReadOnlyList<DeckIssue> Errors(Deck deck, Dictionary<Guid, CardInfo> cards) =>
        DeckValidator.Validate(deck, cards).Where(i => i.Severity == IssueSeverity.Error).ToList();

    [Fact]
    public void LegalStandardDeck_HasNoIssues()
    {
        var (deck, cards) = Build(DeckFormat.Standard,
            (TestCards.Bolt, 4, DeckSection.Main),
            (TestCards.Mountain, 56, DeckSection.Main),
            (TestCards.Counterspell, 2, DeckSection.Sideboard));
        Assert.Empty(DeckValidator.Validate(deck, cards));
    }

    [Fact]
    public void SixtyCardFormats_NeedSixtyCardsAndAtMostFifteenInSideboard()
    {
        var (deck, cards) = Build(DeckFormat.Modern,
            (TestCards.Mountain, 59, DeckSection.Main),
            (TestCards.Mountain, 16, DeckSection.Sideboard));
        var errors = Errors(deck, cards);
        Assert.Contains(errors, e => e.Message.Contains("at least 60"));
        Assert.Contains(errors, e => e.Message.Contains("at most 15"));
    }

    [Fact]
    public void CopyLimit_CountsMainAndSideboardTogether_AndExemptsBasics()
    {
        var (deck, cards) = Build(DeckFormat.Modern,
            (TestCards.Bolt, 3, DeckSection.Main),
            (TestCards.Bolt, 2, DeckSection.Sideboard),
            (TestCards.Mountain, 57, DeckSection.Main));
        var error = Assert.Single(Errors(deck, cards));
        Assert.Equal("Lightning Bolt", error.CardName);
        Assert.Contains("has 5", error.Message);
    }

    [Fact]
    public void CopyLimit_RespectsAnyNumberAndUpToSevenText()
    {
        var (deck, cards) = Build(DeckFormat.Modern,
            (TestCards.Rats, 30, DeckSection.Main),
            (TestCards.Dwarves, 7, DeckSection.Main),
            (TestCards.Mountain, 23, DeckSection.Main));
        Assert.Empty(Errors(deck, cards));

        deck.Cards.Single(c => c.Name == "Seven Dwarves").Quantity = 8;
        Assert.Contains(Errors(deck, cards), e => e.CardName == "Seven Dwarves");
    }

    [Fact]
    public void BannedNotLegalAndRestrictedCards_AreErrors()
    {
        var banned = TestCards.Make("Oko, Thief of Crowns", "Legendary Planeswalker — Oko", ["G", "U"], legal: new() { ["legacy"] = "banned" });
        var restricted = TestCards.Make("Ancestral Recall", "Instant", ["U"], legal: new() { ["vintage"] = "restricted" });

        var (legacy, legacyCards) = Build(DeckFormat.Legacy, (banned, 1, DeckSection.Main), (TestCards.Mountain, 59, DeckSection.Main));
        Assert.Contains(Errors(legacy, legacyCards), e => e.Message.Contains("banned"));

        var (vintage, vintageCards) = Build(DeckFormat.Vintage, (restricted, 2, DeckSection.Main), (TestCards.Mountain, 58, DeckSection.Main));
        Assert.Contains(Errors(vintage, vintageCards), e => e.Message.Contains("restricted"));

        var (standard, standardCards) = Build(DeckFormat.Standard, (TestCards.SolRing, 1, DeckSection.Main), (TestCards.Mountain, 59, DeckSection.Main));
        Assert.Contains(Errors(standard, standardCards), e => e.Message.Contains("isn't legal"));
    }

    [Fact]
    public void Commander_LegalHundredCardSingletonDeck_HasNoErrors()
    {
        var (deck, cards) = Build(DeckFormat.Commander,
            (TestCards.Krenko, 1, DeckSection.Commander),
            (TestCards.SolRing, 1, DeckSection.Main),
            (TestCards.Bolt, 1, DeckSection.Main),
            (TestCards.Mountain, 97, DeckSection.Main));
        Assert.Empty(Errors(deck, cards));
    }

    [Fact]
    public void Commander_FlagsSizeSingletonColorIdentityAndMissingCommander()
    {
        var (deck, cards) = Build(DeckFormat.Commander,
            (TestCards.Krenko, 1, DeckSection.Commander),
            (TestCards.Bolt, 2, DeckSection.Main),
            (TestCards.Counterspell, 1, DeckSection.Main),
            (TestCards.Mountain, 90, DeckSection.Main));
        var errors = Errors(deck, cards);
        Assert.Contains(errors, e => e.Message.Contains("exactly 100"));
        Assert.Contains(errors, e => e.CardName == "Lightning Bolt" && e.Message.Contains("only 1"));
        Assert.Contains(errors, e => e.CardName == "Counterspell" && e.Message.Contains("color identity"));

        deck.Cards.RemoveAll(c => c.Section == DeckSection.Commander);
        Assert.Contains(Errors(deck, cards), e => e.Message == "Choose a commander.");
    }

    [Fact]
    public void Commander_MustBeLegendaryCreature()
    {
        var (deck, cards) = Build(DeckFormat.Commander, (TestCards.Bolt, 1, DeckSection.Commander), (TestCards.Mountain, 99, DeckSection.Main));
        Assert.Contains(Errors(deck, cards), e => e.Message.Contains("can't be a commander"));
    }

    [Fact]
    public void Commander_TwoCommandersNeedPartner()
    {
        var thrasios = TestCards.Make("Thrasios, Triton Hero", "Legendary Creature — Merfolk Wizard", ["G", "U"], "Partner", ["Partner"]);
        var tymna = TestCards.Make("Tymna the Weaver", "Legendary Creature — Human Cleric", ["W", "B"], "Partner", ["Partner"]);
        var (deck, cards) = Build(DeckFormat.Commander,
            (thrasios, 1, DeckSection.Commander),
            (tymna, 1, DeckSection.Commander),
            (TestCards.Counterspell, 1, DeckSection.Main),
            (TestCards.Mountain, 97, DeckSection.Main));
        Assert.Empty(Errors(deck, cards)); // and Counterspell fits the combined WUBG identity

        var (bad, badCards) = Build(DeckFormat.Commander,
            (thrasios, 1, DeckSection.Commander),
            (TestCards.Krenko, 1, DeckSection.Commander),
            (TestCards.Mountain, 98, DeckSection.Main));
        Assert.Contains(Errors(bad, badCards), e => e.Message.Contains("can't both be commanders"));
    }

    [Fact]
    public void Commander_BackgroundPairsWithChooseABackground()
    {
        var wilson = TestCards.Make("Wilson, Refined Grizzly", "Legendary Creature — Bear Warrior", ["G"], "Choose a Background", ["Choose a Background"]);
        var background = TestCards.Make("Raised by Giants", "Legendary Enchantment — Background", ["G"]);
        var (deck, cards) = Build(DeckFormat.Commander,
            (wilson, 1, DeckSection.Commander),
            (background, 1, DeckSection.Commander),
            (TestCards.Mountain, 98, DeckSection.Main));
        Assert.Empty(Errors(deck, cards));
    }

    [Fact]
    public void Brawl_AllowsPlaneswalkerCommander()
    {
        var walker = TestCards.Make("Chandra, Torch of Defiance", "Legendary Planeswalker — Chandra", ["R"]);
        var (deck, cards) = Build(DeckFormat.Brawl, (walker, 1, DeckSection.Commander), (TestCards.Mountain, 59, DeckSection.Main));
        Assert.Empty(Errors(deck, cards));
    }

    [Fact]
    public void Oathbreaker_NeedsPlaneswalkerAndSignatureSpell()
    {
        var walker = TestCards.Make("Chandra, Torch of Defiance", "Legendary Planeswalker — Chandra", ["R"]);
        var (deck, cards) = Build(DeckFormat.Oathbreaker,
            (walker, 1, DeckSection.Commander),
            (TestCards.Bolt, 1, DeckSection.Commander),
            (TestCards.Mountain, 58, DeckSection.Main));
        Assert.Empty(DeckValidator.Validate(deck, cards));

        deck.Cards.RemoveAll(c => c.Name == "Lightning Bolt");
        deck.Add(TestCards.Mountain, DeckSection.Main);
        var issue = Assert.Single(DeckValidator.Validate(deck, cards));
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void UnknownCards_AreWarnings_AndCasualSkipsRules()
    {
        var deck = new Deck { Format = DeckFormat.Casual.Id, Cards = [new DeckCard { Name = "Not A Real Card", Quantity = 9 }] };
        var issue = Assert.Single(DeckValidator.Validate(deck, new Dictionary<Guid, CardInfo>()));
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Deck_AddMergesAndMoveCombinesLines()
    {
        var deck = new Deck();
        deck.Add(TestCards.Bolt, DeckSection.Main, 2);
        deck.Add(TestCards.Bolt, DeckSection.Main, 1);
        deck.Add(TestCards.Bolt, DeckSection.Sideboard, 1);
        Assert.Equal(3, deck.Count(DeckSection.Main));

        deck.Move(deck.In(DeckSection.Sideboard).Single(), DeckSection.Main);
        var line = Assert.Single(deck.Cards);
        Assert.Equal(4, line.Quantity);

        deck.SetQuantity(line, 0);
        Assert.Empty(deck.Cards);
    }
}
