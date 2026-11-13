using System.Text;
using System.Text.RegularExpressions;

namespace Kabatra.MagicTheGathering.Companion.Core.Cards;

/// <summary>Raw text read from a photo of a card: the title line and the bottom-left corner.</summary>
public sealed record ScanText(string? Title, string? Bottom);

/// <summary>What the scan text says about the card. Any part may be missing.</summary>
public sealed record ScanReading(string? Name, string? SetCode, string? CollectorNumber)
{
    public bool HasPrinting => SetCode is not null && CollectorNumber is not null;
    public bool IsEmpty => Name is null && !HasPrinting;
}

/// <summary>The card a scan matched. <see cref="ExactPrinting"/> is true when the set (and usually the number) was read too.</summary>
public sealed record ScanMatch(CardInfo Card, bool ExactPrinting);

/// <summary>
/// Turns noisy OCR text from a card photo into a name, set code and collector number.
/// Cards printed since about 2019 show the collector number and set code in the bottom-left corner:
/// <code>
/// 117/274 U              or   U 0200
/// M21 • EN  Artist            MH3 • EN  Artist
/// </code>
/// </summary>
public static partial class ScanTextParser
{
    public static ScanReading Parse(ScanText text) =>
        new(CleanName(text.Title), FindSet(text.Bottom), FindCollectorNumber(text.Bottom));

    /// <summary>Keeps the letters and punctuation card names use, and drops stray marks OCR picks up around them.</summary>
    public static string? CleanName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        // The title is one line; OCR sometimes adds a second line of junk from the art.
        var line = title.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderByDescending(l => l.Count(char.IsLetter))
            .First();

        var kept = new StringBuilder();
        foreach (var c in line.Replace('’', '\'').Replace('‘', '\''))
            kept.Append(char.IsLetter(c) || c is ' ' or '\'' or ',' or '-' ? c : ' ');

        var words = kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        // Mana symbols and frame edges come out as one- or two-letter scraps at either end.
        while (words.Count > 0 && IsScrap(words[^1])) words.RemoveAt(words.Count - 1);
        while (words.Count > 0 && IsScrap(words[0])) words.RemoveAt(0);

        var name = string.Join(' ', words).Trim(' ', ',', '-', '\'');
        return name.Count(char.IsLetter) >= 3 ? name : null;
    }

    private static bool IsScrap(string word) => word.Count(char.IsLetter) < 2 || !char.IsLetter(word[0]) && !char.IsLetter(word[^1]);

    public static string? FindSet(string? bottom)
    {
        if (string.IsNullOrWhiteSpace(bottom)) return null;
        var match = SetRegex().Match(bottom.ToUpperInvariant());
        if (!match.Success) return null;
        var set = match.Groups["set"].Value;
        return set.Any(char.IsLetter) ? set.ToLowerInvariant() : null; // all-digit "sets" are collector numbers
    }

    public static string? FindCollectorNumber(string? bottom)
    {
        if (string.IsNullOrWhiteSpace(bottom)) return null;
        var upper = bottom.ToUpperInvariant();

        // Best: a number next to the rarity letter ("117/274 R" before 2023, "U 0200" since).
        var match = NumberWithRarityRegex().Match(upper);
        if (!match.Success) match = RarityThenNumberRegex().Match(upper);
        // Otherwise the first standalone number before the set line.
        if (!match.Success)
        {
            var setAt = SetRegex().Match(upper) is { Success: true } s ? s.Index : upper.Length;
            match = LooseNumberRegex().Match(upper[..setAt]);
        }
        if (!match.Success) return null;

        var number = match.Groups["num"].Value.TrimStart('0');
        return number.Length == 0 ? "0" : number;
    }

    /// <summary>How alike two card names are, from 0 (nothing alike) to 1 (the same, ignoring case and punctuation).</summary>
    public static double NameSimilarity(string a, string b)
    {
        var x = Letters(a);
        var y = Letters(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x.Contains(y) || y.Contains(x)) return Math.Min(x.Length, y.Length) / (double)Math.Max(x.Length, y.Length) >= 0.5 ? 1 : 0.5;
        return 1 - Levenshtein(x, y) / (double)Math.Max(x.Length, y.Length);
    }

    private static string Letters(string s) => new(s.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());

    private static int Levenshtein(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    // "2X2 • EN", "M21 * EN", "MOM · JP". OCR turns the dot into all sorts of marks, or drops it.
    [GeneratedRegex(@"(?<![A-Z0-9])(?<set>[A-Z0-9]{3,5})\s*[^A-Z0-9\s]{0,3}\s*(?:EN|ES|FR|DE|IT|PT|JA|JP|KO|RU|ZHS|ZHT|PH)(?![A-Z])")]
    private static partial Regex SetRegex();

    [GeneratedRegex(@"(?<![0-9])(?<num>[0-9]{1,4})(?:\s*/\s*[0-9]{1,4})?\s+[CURMLSTPB](?![A-Z0-9])")]
    private static partial Regex NumberWithRarityRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])[CURMLSTPB]\s+(?<num>[0-9]{1,4})(?![0-9])")]
    private static partial Regex RarityThenNumberRegex();

    [GeneratedRegex(@"(?<![0-9A-Z])(?<num>[0-9]{1,4})(?![0-9A-Z])")]
    private static partial Regex LooseNumberRegex();
}

/// <summary>
/// Finds the card a scan shows. The exact printing (set + collector number) wins when its name agrees
/// with the title that was read; otherwise the title is matched by name, forgiving OCR typos.
/// </summary>
public sealed class CardScanMatcher(CardCatalog catalog)
{
    /// <summary>How close the read title must be to the printing's name to trust the printing.</summary>
    public const double PrintingNameAgreement = 0.6;

    public async Task<ScanMatch?> MatchAsync(ScanReading reading, CancellationToken ct = default)
    {
        if (reading.IsEmpty) return null;

        CardInfo? printing = null;
        if (reading.HasPrinting)
        {
            printing = await TryAsync(() => catalog.GetByPrintingAsync(reading.SetCode!, reading.CollectorNumber!, ct));
            if (printing is not null && (reading.Name is null || NamesAgree(reading.Name, printing)))
                return new ScanMatch(printing, ExactPrinting: true);
        }

        if (reading.Name is null) return null;

        var byName = await FindByNameAsync(reading.Name, ct);
        // A garbled title that matches nothing: the printing is still our best guess, and the user confirms it.
        if (byName is null) return printing is null ? null : new ScanMatch(printing, ExactPrinting: true);

        // The number was misread but the set may still be right: look for this card in that set.
        if (reading.SetCode is { } set && !string.Equals(byName.SetCode, set, StringComparison.OrdinalIgnoreCase))
        {
            var inSet = await TryAsync(async () =>
                (await catalog.SearchAsync($"!\"{byName.Name}\" set:{set}", ct: ct)).Cards.FirstOrDefault());
            if (inSet is not null) return new ScanMatch(inSet, ExactPrinting: true);
        }

        return new ScanMatch(byName, ExactPrinting: reading.SetCode is { } s && string.Equals(byName.SetCode, s, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<CardInfo?> FindByNameAsync(string name, CancellationToken ct)
    {
        if (await TryAsync(() => catalog.FindByNameAsync(name, ct)) is { } card) return card;

        // Scryfall's fuzzy match gives up on badly garbled names; autocomplete is more forgiving of a bad ending.
        var suggestions = await TryAsync<IReadOnlyList<string>>(async () => await catalog.AutocompleteAsync(name, ct)) ?? [];
        var best = suggestions.OrderByDescending(s => ScanTextParser.NameSimilarity(s, name)).FirstOrDefault();
        return best is null ? null : await TryAsync(() => catalog.FindByNameAsync(best, ct));
    }

    private static bool NamesAgree(string read, CardInfo card) =>
        ScanTextParser.NameSimilarity(read, card.FrontName) >= PrintingNameAgreement
        || ScanTextParser.NameSimilarity(read, card.Name) >= PrintingNameAgreement;

    private static async Task<T?> TryAsync<T>(Func<Task<T?>> lookup)
    {
        try { return await lookup(); }
        catch (ScryfallException) { return default; } // e.g. a misread set code Scryfall rejects
    }
}
