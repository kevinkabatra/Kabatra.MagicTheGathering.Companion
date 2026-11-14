using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kabatra.MagicTheGathering.Companion.Core.Decks;

public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Every one of a player's decks, and their collection if they have one, in one JSON file. It lets anyone move to
/// another device or keep a copy of their own, with or without an account.
/// </summary>
public sealed record BackupFile(int Version, DateTimeOffset ExportedAt, List<Deck> Decks, List<DeckCard>? Collection)
{
    /// <summary>The format version this app writes. Files from newer versions are refused rather than half-read.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The kind of file, so a different JSON file isn't mistaken for a backup.</summary>
    public string? Kind { get; init; }

    private const string FileKind = "mtg-companion-backup";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write(IEnumerable<Deck> decks, IEnumerable<DeckCard>? collection, DateTimeOffset now) =>
        JsonSerializer.Serialize(new BackupFile(CurrentVersion, now, decks.Select(d => d.Clone()).ToList(), collection?.Select(c => c.Clone()).ToList())
        {
            Kind = FileKind,
        }, Json);

    /// <summary>A file name like <c>mtg-decks-2026-11-13.json</c>.</summary>
    public static string FileName(DateTimeOffset now) => $"mtg-decks-{now:yyyy-MM-dd}.json";

    public static BackupFile Read(string json)
    {
        BackupFile? file;
        try
        {
            file = JsonSerializer.Deserialize<BackupFile>(json, Json);
        }
        catch (JsonException e)
        {
            throw new BackupException("That file isn't a deck backup (it couldn't be read).", e);
        }
        if (file is null || file.Kind != FileKind)
            throw new BackupException("That file isn't a deck backup.");
        if (file.Version > CurrentVersion)
            throw new BackupException("That backup was made by a newer version of the app. Reload the page to update, then try again.");
        return file with { Decks = file.Decks ?? [] };
    }

    /// <summary>
    /// The decks to add when restoring into <paramref name="existing"/>: each gets a new id (so restoring never
    /// overwrites anything) and no owner profile (it goes to whoever restores it), and a deck that's already there,
    /// with the same name and cards, is skipped so restoring twice doesn't double everything.
    /// </summary>
    public IReadOnlyList<Deck> DecksToRestore(IEnumerable<Deck> existing)
    {
        var have = existing.Select(Signature).ToHashSet();
        return Decks
            .Where(d => have.Add(Signature(d)))
            .Select(d =>
            {
                var copy = d.Clone();
                copy.Id = Guid.NewGuid();
                copy.OwnerProfileId = null;
                copy.CardsInCollection = false; // a new deck for whoever restores it; its cards aren't in their collection
                return copy;
            })
            .ToList();
    }

    private static string Signature(Deck d) =>
        d.Name.Trim().ToUpperInvariant() + "|" + string.Join(";", d.Cards
            .OrderBy(c => c.Section).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"{c.Section}:{c.Quantity}:{c.Name.ToUpperInvariant()}"));
}
