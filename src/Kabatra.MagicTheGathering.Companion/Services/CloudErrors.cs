using Kabatra.MagicTheGathering.Companion.Core.Cloud;

namespace Kabatra.MagicTheGathering.Companion.Services;

public static class CloudErrors
{
    /// <summary>A failure as a sentence for the user, e.g. "Couldn't load players: …".</summary>
    public static string Describe(string whatFailed, Exception e) => e switch
    {
        SupabaseException { NeedsSchemaUpdate: true } =>
            "Player profiles and play history need a database update. In your Supabase project's SQL Editor, run supabase/schema.sql again (it keeps your existing decks).",
        HttpRequestException => $"{whatFailed}: couldn't reach the server. Check your connection and try again.",
        _ => $"{whatFailed}: {e.Message}",
    };
}
