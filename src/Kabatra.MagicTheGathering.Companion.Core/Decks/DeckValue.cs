using System.Globalization;
using Kabatra.MagicTheGathering.Companion.Core.Cards;

namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

/// <summary>What some deck lines are worth in US dollars, and how many cards had no price to count.</summary>
public sealed record DeckValue(decimal Usd, int UnpricedCards)
{
    /// <summary>Adds up quantity × price for each line, using the printing the line points at.</summary>
    public static DeckValue Of(IEnumerable<DeckCard> lines, IReadOnlyDictionary<Guid, CardInfo> cards)
    {
        decimal total = 0;
        var unpriced = 0;
        foreach (var line in lines)
        {
            if (LinePrice(line, cards) is { } price) total += price;
            else unpriced += line.Quantity;
        }
        return new(total, unpriced);
    }

    /// <summary>Quantity × the card's price, or null when the card or its price is unknown.</summary>
    public static decimal? LinePrice(DeckCard line, IReadOnlyDictionary<Guid, CardInfo> cards) =>
        line.ScryfallId is { } id && cards.TryGetValue(id, out var card) && card.UsdPrice is { } each
            ? each * line.Quantity
            : null;

    /// <summary>"$1,234.56", the same in every browser locale.</summary>
    public static string FormatUsd(decimal value) => "$" + value.ToString("N2", CultureInfo.InvariantCulture);
}
