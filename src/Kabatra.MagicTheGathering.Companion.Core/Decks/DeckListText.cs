using System.Text;
using System.Text.RegularExpressions;

namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

public sealed record DeckListParseResult(string? Name, IReadOnlyList<DeckCard> Cards, IReadOnlyList<string> Problems);

/// <summary>
/// Reads and writes plain-text deck lists in the formats people paste around:
/// MTG Arena ("Deck" / "Sideboard" headers, "4 Lightning Bolt (M10) 146"), MTGO
/// ("4 Lightning Bolt", a blank line before the sideboard, or "SB: 2 Duress"), and
/// Moxfield / Archidekt exports ("1x Sol Ring (C21) 263 *F*").
/// </summary>
public static partial class DeckListText
{
    public static DeckListParseResult Parse(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        var hasHeaders = lines.Any(l => TryReadHeader(l, out _));

        var cards = new List<DeckCard>();
        var problems = new List<string>();
        string? name = null;
        var section = DeckSection.Main;
        var inAbout = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                // MTGO: with no headers, the cards after a blank line are the sideboard.
                if (!hasHeaders && section == DeckSection.Main && cards.Count > 0) section = DeckSection.Sideboard;
                continue;
            }
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#')) continue;

            if (line.Equals("About", StringComparison.OrdinalIgnoreCase)) { inAbout = true; continue; }
            if (inAbout)
            {
                if (line.StartsWith("Name ", StringComparison.OrdinalIgnoreCase)) { name = line[5..].Trim(); continue; }
                inAbout = false;
            }

            if (TryReadHeader(line, out var header))
            {
                section = header;
                continue;
            }

            var lineSection = section;
            if (line.StartsWith("SB:", StringComparison.OrdinalIgnoreCase))
            {
                lineSection = DeckSection.Sideboard;
                line = line[3..].Trim();
            }

            var match = CardLineRegex().Match(line);
            if (!match.Success || match.Groups["name"].Value.Trim().Length == 0)
            {
                problems.Add($"Line {i + 1}: couldn't read \"{lines[i]}\".");
                continue;
            }

            var quantity = match.Groups["qty"].Success ? int.Parse(match.Groups["qty"].Value) : 1;
            if (quantity < 1)
            {
                problems.Add($"Line {i + 1}: quantity must be at least 1.");
                continue;
            }

            var card = new DeckCard
            {
                Name = match.Groups["name"].Value.Trim(),
                Quantity = quantity,
                Section = lineSection,
                SetCode = match.Groups["set"].Success ? match.Groups["set"].Value.ToLowerInvariant() : null,
                CollectorNumber = match.Groups["num"].Success ? match.Groups["num"].Value : null,
            };

            var same = cards.FirstOrDefault(c => c.Section == card.Section
                && string.Equals(c.Name, card.Name, StringComparison.OrdinalIgnoreCase)
                && c.SetCode == card.SetCode && c.CollectorNumber == card.CollectorNumber);
            if (same is not null) same.Quantity += card.Quantity;
            else cards.Add(card);
        }

        return new(name, cards, problems);
    }

    /// <summary>
    /// Writes a deck in MTG Arena format, which Arena, Moxfield, Archidekt and most other tools import.
    /// </summary>
    public static string Write(Deck deck, bool includePrintings = true)
    {
        var sb = new StringBuilder();
        void WriteSection(string header, DeckSection section)
        {
            var lines = deck.In(section).ToList();
            if (lines.Count == 0) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(header).Append('\n');
            foreach (var card in lines)
            {
                sb.Append(card.Quantity).Append(' ').Append(card.Name);
                if (includePrintings && !string.IsNullOrEmpty(card.SetCode) && !string.IsNullOrEmpty(card.CollectorNumber))
                    sb.Append(" (").Append(card.SetCode.ToUpperInvariant()).Append(") ").Append(card.CollectorNumber);
                sb.Append('\n');
            }
        }

        if (!string.IsNullOrWhiteSpace(deck.Name)) sb.Append("About\nName ").Append(deck.Name.Trim()).Append('\n');
        WriteSection("Commander", DeckSection.Commander);
        WriteSection("Deck", DeckSection.Main);
        WriteSection("Sideboard", DeckSection.Sideboard);
        return sb.ToString();
    }

    private static bool TryReadHeader(string line, out DeckSection section)
    {
        var word = line.TrimEnd(':').Trim();
        // Moxfield-style headers can carry a count, e.g. "Sideboard (15)".
        word = HeaderCountRegex().Replace(word, "").Trim().ToLowerInvariant();
        switch (word)
        {
            case "deck": case "main": case "maindeck": case "mainboard": case "main deck":
                section = DeckSection.Main; return true;
            case "sideboard": case "side": case "sb": case "companion":
                section = DeckSection.Sideboard; return true;
            case "commander": case "commanders": case "oathbreaker": case "signature spell":
                section = DeckSection.Commander; return true;
            default:
                section = default; return false;
        }
    }

    // "4x Lightning Bolt (M10) 146 *F*" -> qty 4, name, set M10, number 146. Foil markers and tags are dropped.
    [GeneratedRegex(@"^(?:(?<qty>\d+)\s*[xX]?\s+)?(?<name>.+?)(?:\s+\((?<set>[A-Za-z0-9]{2,6})\)(?:\s+(?<num>[A-Za-z0-9\-★†]+))?)?(?:\s+\*[A-Z]+\*)*(?:\s+\[[^\]]*\])*(?:\s+\^[^^]*\^)*$")]
    private static partial Regex CardLineRegex();

    [GeneratedRegex(@"\(\d+\)$")]
    private static partial Regex HeaderCountRegex();
}
