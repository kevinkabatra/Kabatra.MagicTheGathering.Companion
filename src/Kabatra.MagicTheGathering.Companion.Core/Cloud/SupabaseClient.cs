using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kabatra.MagicTheGathering.Companion.Core.Cloud;

/// <summary>
/// Where the Supabase project lives and the key the browser uses to reach it.
/// </summary>
/// <param name="Url">The project URL, e.g. https://abcd.supabase.co.</param>
/// <param name="PublishableKey">
/// A publishable key (<c>sb_publishable_…</c>), or a legacy <c>anon</c> key while those still work.
/// Both are public by design; row-level security is what protects the data. A secret or service_role
/// key is refused, because it bypasses row-level security and must never ship to a browser.
/// </param>
public sealed record SupabaseOptions(string? Url, string? PublishableKey)
{
    /// <summary>Why these settings can't be used, or null if they're fine (or simply not filled in).</summary>
    public string? Problem
    {
        get
        {
            var key = PublishableKey?.Trim() ?? "";
            if (key.StartsWith("sb_secret_", StringComparison.Ordinal) || IsServiceRoleJwt(key))
                return "The Supabase key in appsettings.json is a secret key. Secret keys bypass row-level security and must never be put in a web app; " +
                       "rotate it in Supabase (Settings → API Keys) and use the publishable key (sb_publishable_…) instead.";
            if (!string.IsNullOrWhiteSpace(Url) && !Uri.TryCreate(Url.Trim(), UriKind.Absolute, out _))
                return "The Supabase URL in appsettings.json isn't a valid address.";
            return null;
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(PublishableKey) && Problem is null;

    /// <summary>True for a new-style key; false for a legacy anon JWT.</summary>
    public bool IsLegacyKey => PublishableKey?.Trim().StartsWith("eyJ", StringComparison.Ordinal) == true;

    private static bool IsServiceRoleJwt(string key)
    {
        if (!key.StartsWith("eyJ", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(SupabaseClient.DecodeJwtPayload(key));
            return doc.RootElement.TryGetProperty("role", out var role) && role.GetString() == "service_role";
        }
        catch (Exception e) when (e is FormatException or JsonException or SupabaseException)
        {
            return false;
        }
    }
}

public sealed record AuthSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, Guid UserId, string? Email);

public sealed class SupabaseException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>
    /// True when the database is missing a table or column the app uses: the project was set up with an
    /// older supabase/schema.sql and needs it run again.
    /// </summary>
    public bool NeedsSchemaUpdate =>
        Status is 400 or 404
        && (Message.Contains("schema cache", StringComparison.OrdinalIgnoreCase) || Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A small client for Supabase's auth (GoTrue) and database (PostgREST) HTTP APIs: OAuth sign-in,
/// keeping the session fresh, and authenticated REST calls. Kept dependency-free so it runs in the browser.
/// </summary>
public sealed class SupabaseClient
{
    private const string SessionKey = "mtg-companion.supabase-session";
    private const string VerifierKey = "mtg-companion.supabase-pkce-verifier";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly IKeyValueStore _store;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public SupabaseClient(HttpClient http, SupabaseOptions options, IKeyValueStore store, TimeProvider? clock = null)
    {
        _http = http;
        _options = options;
        _store = store;
        _clock = clock ?? TimeProvider.System;
        if (options.IsConfigured) _http.BaseAddress ??= new Uri(options.Url!.TrimEnd('/') + "/");
    }

    public bool IsConfigured => _options.IsConfigured;
    public AuthSession? Session { get; private set; }
    public bool IsSignedIn => Session is not null;

    /// <summary>Raised when the user signs in or out.</summary>
    public event Action? SessionChanged;

    /// <summary>Restores a saved session, refreshing it if it has expired. Safe to call more than once.</summary>
    public async Task InitializeAsync()
    {
        if (!IsConfigured || Session is not null) return;
        var json = await _store.GetAsync(SessionKey);
        if (json is null) return;
        try
        {
            Session = JsonSerializer.Deserialize<AuthSession>(json);
            await EnsureFreshAsync();
        }
        catch (Exception e) when (e is JsonException or SupabaseException)
        {
            await SetSessionAsync(null);
        }
        catch (HttpRequestException)
        {
            // Offline: keep the saved session, so the game still works; it's refreshed on the next request.
        }
    }

    /// <summary>
    /// Starts signing in with an OAuth provider (e.g. "google" or "apple", as named in Supabase Auth).
    /// Returns the address to send the browser to; the provider sends it back to <paramref name="redirectTo"/>
    /// with a one-time code that <see cref="TryCompleteSignInAsync"/> exchanges for a session.
    /// Uses PKCE, so the tokens never appear in the address bar or browser history.
    /// </summary>
    public async Task<string> BeginSignInAsync(string provider, string redirectTo)
    {
        if (!IsConfigured) throw new InvalidOperationException("Supabase isn't configured.");
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("A provider is required.", nameof(provider));

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        await _store.SetAsync(VerifierKey, verifier);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return $"{_options.Url!.Trim().TrimEnd('/')}/auth/v1/authorize" +
               $"?provider={Uri.EscapeDataString(provider.Trim().ToLowerInvariant())}" +
               $"&redirect_to={Uri.EscapeDataString(redirectTo)}" +
               $"&code_challenge={challenge}&code_challenge_method=s256";
    }

    /// <summary>
    /// Finishes an OAuth sign-in when the provider sends the browser back: exchanges the <c>code</c> in the
    /// query for a session. Returns true if <paramref name="uri"/> was a sign-in redirect; a failed sign-in
    /// (e.g. the user cancelled, or the account is blocked) throws.
    /// </summary>
    public async Task<bool> TryCompleteSignInAsync(Uri uri, CancellationToken ct = default)
    {
        var values = ParseParameters(uri.Query.TrimStart('?'));
        foreach (var (k, v) in ParseParameters(uri.Fragment.TrimStart('#'))) values.TryAdd(k, v);

        if (values.TryGetValue("error_description", out var error) || values.TryGetValue("error", out error))
        {
            await _store.RemoveAsync(VerifierKey);
            throw new SupabaseException(DescribeAuthError(error), 400);
        }

        if (values.TryGetValue("code", out var code))
        {
            var verifier = await _store.GetAsync(VerifierKey);
            // A code we didn't ask for (another tab, or a ?code= that isn't ours): leave it alone.
            if (verifier is null) return false;
            await _store.RemoveAsync(VerifierKey);
            using var request = NewRequest(HttpMethod.Post, "auth/v1/token?grant_type=pkce",
                new { auth_code = code, code_verifier = verifier }, auth: false);
            var response = await ReadAsync<TokenResponse>(request, ct);
            await SetSessionAsync(ToSession(response));
            return true;
        }

        return false;
    }

    /// <summary>The query and fragment parameters Supabase Auth uses for sign-in redirects.</summary>
    public static readonly IReadOnlyList<string> SignInParameters =
        ["code", "error", "error_code", "error_description", "state"];

    private static Dictionary<string, string> ParseParameters(string text) =>
        text.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(p => Uri.UnescapeDataString(p[0]))
            .ToDictionary(g => g.Key, g => g.First() is { Length: > 1 } p ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");

    private static string DescribeAuthError(string error) =>
        error.Contains("banned", StringComparison.OrdinalIgnoreCase)
            ? BlockedMessage
            : error.Equals("access_denied", StringComparison.OrdinalIgnoreCase) ? "Sign-in was cancelled." : error;

    /// <summary>Shown when Supabase Auth refuses a banned account (see claim_profile in supabase/schema.sql).</summary>
    public const string BlockedMessage = "This account has been blocked after too many wrong invite codes.";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task SignOutAsync()
    {
        if (Session is not null)
        {
            try
            {
                using var request = NewRequest(HttpMethod.Post, "auth/v1/logout", null, auth: true);
                await _http.SendAsync(request);
            }
            catch (HttpRequestException)
            {
                // Signing out locally is what matters; the token expires on its own.
            }
        }
        await SetSessionAsync(null);
    }

    // ----- Database (PostgREST) -----

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        await EnsureFreshAsync();
        using var request = NewRequest(HttpMethod.Get, $"rest/v1/{path}", null, auth: true);
        return await ReadAsync<T>(request, ct);
    }

    /// <summary>Inserts, or updates on a primary-key clash, and returns the stored rows.</summary>
    public async Task<List<T>> UpsertAsync<T>(string table, object row, CancellationToken ct = default)
    {
        await EnsureFreshAsync();
        using var request = NewRequest(HttpMethod.Post, $"rest/v1/{table}", row, auth: true);
        request.Headers.Add("Prefer", "resolution=merge-duplicates,return=representation");
        return await ReadAsync<List<T>>(request, ct);
    }

    /// <summary>Updates the matching rows with the given columns and returns them as stored.</summary>
    public async Task<List<T>> PatchAsync<T>(string pathWithFilter, object changes, CancellationToken ct = default)
    {
        await EnsureFreshAsync();
        using var request = NewRequest(HttpMethod.Patch, $"rest/v1/{pathWithFilter}", changes, auth: true);
        request.Headers.Add("Prefer", "return=representation");
        return await ReadAsync<List<T>>(request, ct);
    }

    public async Task DeleteAsync(string pathWithFilter, CancellationToken ct = default)
    {
        await EnsureFreshAsync();
        using var request = NewRequest(HttpMethod.Delete, $"rest/v1/{pathWithFilter}", null, auth: true);
        await SendAsync(request, ct);
    }

    /// <summary>Calls a Postgres function exposed through PostgREST.</summary>
    public async Task<T> RpcAsync<T>(string function, object args, CancellationToken ct = default)
    {
        await EnsureFreshAsync();
        using var request = NewRequest(HttpMethod.Post, $"rest/v1/rpc/{function}", args, auth: true);
        return await ReadAsync<T>(request, ct);
    }

    // ----- Plumbing -----

    private async Task EnsureFreshAsync()
    {
        if (Session is null || Session.ExpiresAt - _clock.GetUtcNow() > RefreshMargin) return;
        await _refreshGate.WaitAsync();
        try
        {
            if (Session is null || Session.ExpiresAt - _clock.GetUtcNow() > RefreshMargin) return;
            using var request = NewRequest(HttpMethod.Post, "auth/v1/token?grant_type=refresh_token",
                new { refresh_token = Session.RefreshToken }, auth: false);
            try
            {
                var response = await ReadAsync<TokenResponse>(request, default);
                await SetSessionAsync(ToSession(response));
            }
            catch (SupabaseException e) when (e.Status is 400 or 401 or 403)
            {
                // Refresh token revoked or expired, or the account was banned: the user has to sign in again.
                await SetSessionAsync(null);
                throw new SupabaseException(
                    e.Message.Contains("banned", StringComparison.OrdinalIgnoreCase) ? BlockedMessage : "Your sign-in has expired. Please sign in again.",
                    401);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url, object? body, bool auth)
    {
        if (!IsConfigured) throw new InvalidOperationException("Supabase isn't configured.");
        var request = new HttpRequestMessage(method, url);
        // The project key always goes in "apikey". Publishable keys aren't JWTs, so they must not be sent as
        // a Bearer token; Authorization carries only the signed-in user's access token. Without it, Supabase
        // treats the request as the anon role.
        request.Headers.Add("apikey", _options.PublishableKey!.Trim());
        if (auth && Session is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Session.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return request;
    }

    private async Task<T> ReadAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await SendAsync(request, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new SupabaseException("Empty response from Supabase.", (int)response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return response;

        var text = await response.Content.ReadAsStringAsync(ct);
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var key in new[] { "msg", "message", "error_description", "error" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) { message = v.GetString(); break; }
        }
        catch (JsonException) { }
        throw new SupabaseException(message ?? $"Supabase returned {(int)response.StatusCode}.", (int)response.StatusCode);
    }

    private async Task SetSessionAsync(AuthSession? session)
    {
        var wasSignedIn = Session is not null;
        Session = session;
        if (session is null) await _store.RemoveAsync(SessionKey);
        else await _store.SetAsync(SessionKey, JsonSerializer.Serialize(session));
        if (wasSignedIn != (session is not null)) SessionChanged?.Invoke();
    }

    private AuthSession ToSession(TokenResponse r) =>
        new(r.AccessToken, r.RefreshToken, _clock.GetUtcNow().AddSeconds(r.ExpiresIn), r.User?.Id ?? ReadJwtUser(r.AccessToken).UserId, r.User?.Email);

    /// <summary>Reads the user id and email from an access token's payload (no signature check: the server does that).</summary>
    internal static (Guid UserId, string? Email) ReadJwtUser(string jwt)
    {
        using var doc = JsonDocument.Parse(DecodeJwtPayload(jwt));
        var sub = doc.RootElement.TryGetProperty("sub", out var s) && Guid.TryParse(s.GetString(), out var id) ? id : Guid.Empty;
        var email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        return (sub, email);
    }

    internal static string DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) throw new SupabaseException("Malformed access token.", 400);
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
    }

    private sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn, UserDto? User);
    private sealed record UserDto(Guid Id, string? Email);
}
