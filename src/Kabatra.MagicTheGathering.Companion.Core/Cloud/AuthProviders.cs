namespace Kabatra.MagicTheGathering.Companion.Core.Cloud;

/// <summary>An OAuth provider people can sign in with. The id is the provider's name in Supabase Auth.</summary>
public sealed record AuthProvider(string Id, string Label);

/// <summary>
/// Which sign-in providers the app offers. The app never handles passwords: every sign-in goes through
/// an OAuth provider that's turned on in the Supabase project (Authentication → Sign In / Providers).
/// To offer another provider, enable it in Supabase and add its id to <c>Auth:Providers</c> in appsettings.json.
/// </summary>
public static class AuthProviders
{
    /// <summary>Offered when appsettings.json doesn't list any.</summary>
    public static readonly IReadOnlyList<string> DefaultIds = ["google", "apple"];

    private static readonly Dictionary<string, string> KnownLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["google"] = "Google",
        ["apple"] = "Apple",
        ["azure"] = "Microsoft",
        ["discord"] = "Discord",
        ["facebook"] = "Facebook",
        ["github"] = "GitHub",
        ["gitlab"] = "GitLab",
        ["twitch"] = "Twitch",
        ["twitter"] = "X",
    };

    /// <summary>The providers for the configured ids (or the defaults), in order, without blanks or repeats.</summary>
    public static IReadOnlyList<AuthProvider> From(IEnumerable<string?>? ids)
    {
        var list = (ids ?? [])
            .Select(id => id?.Trim().ToLowerInvariant())
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .Select(id => new AuthProvider(id!, Label(id!)))
            .ToList();
        return list.Count > 0 ? list : From(DefaultIds);
    }

    private static string Label(string id) =>
        KnownLabels.TryGetValue(id, out var label) ? label : char.ToUpperInvariant(id[0]) + id[1..];
}
