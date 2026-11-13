using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// A small client for Supabase's auth (GoTrue) and database (PostgREST) HTTP APIs: email sign-in,
/// keeping the session fresh, and authenticated REST calls. Kept dependency-free so it runs in the browser.
/// </summary>
public sealed class SupabaseClient
{
    private const string SessionKey = "mtg-companion.supabase-session";
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

    /// <summary>
    /// Set once the decks table turns out to have no owner_profile_id column (schema from before player profiles),
    /// so decks keep working until the project's schema is updated.
    /// </summary>
    internal bool DecksLackOwnerProfile { get; set; }

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
        catch (Exception e) when (e is JsonException or SupabaseException or HttpRequestException)
        {
            await SetSessionAsync(null);
        }
    }

    /// <summary>
    /// Emails a sign-in link (and a 6-digit code, if the email template includes it). Supabase creates
    /// the account on first sign-in.
    /// </summary>
    public async Task SendSignInEmailAsync(string email, string redirectTo, CancellationToken ct = default)
    {
        var url = $"auth/v1/otp?redirect_to={Uri.EscapeDataString(redirectTo)}";
        using var request = NewRequest(HttpMethod.Post, url, new { email = email.Trim(), create_user = true }, auth: false);
        await SendAsync(request, ct);
    }

    /// <summary>Signs in with the 6-digit code from the sign-in email.</summary>
    public async Task VerifyEmailCodeAsync(string email, string code, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Post, "auth/v1/verify",
            new { type = "email", email = email.Trim(), token = code.Trim() }, auth: false);
        var response = await ReadAsync<TokenResponse>(request, ct);
        await SetSessionAsync(ToSession(response));
    }

    /// <summary>
    /// Finishes a sign-in-link login: Supabase redirects back with the tokens in the URL fragment.
    /// Returns true if <paramref name="uri"/> carried a session (or an error, which is thrown).
    /// </summary>
    public async Task<bool> TryCompleteSignInAsync(Uri uri)
    {
        var fragment = uri.Fragment.TrimStart('#');
        if (fragment.Length == 0) return false;
        var values = fragment.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");

        if (values.TryGetValue("error_description", out var error))
            throw new SupabaseException(error, 400);
        if (!values.TryGetValue("access_token", out var access) || !values.TryGetValue("refresh_token", out var refresh))
            return false;

        var expiresIn = values.TryGetValue("expires_in", out var e) && int.TryParse(e, out var seconds) ? seconds : 3600;
        var (userId, email) = ReadJwtUser(access);
        await SetSessionAsync(new AuthSession(access, refresh, _clock.GetUtcNow().AddSeconds(expiresIn), userId, email));
        return true;
    }

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
                // Refresh token revoked or expired: the user has to sign in again.
                await SetSessionAsync(null);
                throw new SupabaseException("Your sign-in has expired. Please sign in again.", 401);
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
