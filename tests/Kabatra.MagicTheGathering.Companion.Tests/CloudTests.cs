using System.Net;
using System.Text;
using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;
using Kabatra.MagicTheGathering.Companion.Core.Decks;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class CloudTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly SupabaseOptions Options = new("https://example.supabase.co", "sb_publishable_test123");

    private static string Jwt(Guid sub, string email)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"HS256"}""")}.{B64($$"""{"sub":"{{sub}}","email":"{{email}}"}""")}.sig";
    }

    [Fact]
    public async Task NotConfigured_WhenUrlOrKeyMissing()
    {
        var client = new SupabaseClient(new HttpClient(), new SupabaseOptions("", null), new MemoryKeyValueStore());
        Assert.False(client.IsConfigured);
        await client.InitializeAsync(); // no-op, no throw
        Assert.False(client.IsSignedIn);
    }

    [Fact]
    public async Task SignInLink_RedirectFragmentCreatesAndSavesSession()
    {
        var store = new MemoryKeyValueStore();
        var client = new SupabaseClient(new HttpClient(new FakeHandler((_, _) => FakeHandler.Json("{}"))), Options, store);
        var changed = 0;
        client.SessionChanged += () => changed++;

        var uri = new Uri($"https://site/app/#access_token={Jwt(UserId, "a@b.c")}&expires_in=3600&refresh_token=r1&token_type=bearer&type=magiclink");
        Assert.True(await client.TryCompleteSignInAsync(uri));

        Assert.Equal(UserId, client.Session!.UserId);
        Assert.Equal("a@b.c", client.Session.Email);
        Assert.Equal(1, changed);

        // A fresh client picks the session back up from storage.
        var restored = new SupabaseClient(new HttpClient(), Options, store);
        await restored.InitializeAsync();
        Assert.Equal(UserId, restored.Session?.UserId);
    }

    [Fact]
    public async Task SignInLink_ErrorInFragmentThrows_AndOtherFragmentsAreIgnored()
    {
        var client = new SupabaseClient(new HttpClient(), Options, new MemoryKeyValueStore());
        await Assert.ThrowsAsync<SupabaseException>(() =>
            client.TryCompleteSignInAsync(new Uri("https://site/#error=access_denied&error_description=Email+link+is+invalid+or+has+expired")));
        Assert.False(await client.TryCompleteSignInAsync(new Uri("https://site/decks#top")));
    }

    [Fact]
    public async Task SendSignInEmail_PostsToOtpWithRedirectAndApiKey()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("{}"));
        var client = new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore());

        await client.SendSignInEmailAsync(" me@example.com ", "https://site/app/");

        var (request, body) = handler.Requests.Single();
        Assert.Equal("https://example.supabase.co/auth/v1/otp?redirect_to=https%3A%2F%2Fsite%2Fapp%2F", request.RequestUri!.ToString());
        Assert.Equal("sb_publishable_test123", request.Headers.GetValues("apikey").Single());
        // Publishable keys aren't JWTs, so they must never be sent as a Bearer token.
        Assert.Null(request.Headers.Authorization);
        Assert.Contains("\"email\":\"me@example.com\"", body);
        Assert.Contains("\"create_user\":true", body);
    }

    [Fact]
    public async Task SignedInRequests_SendUserTokenAsBearer_AndKeyOnlyAsApiKey()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/auth/v1/verify"
            ? FakeHandler.Json($$"""{ "access_token": "{{Jwt(UserId, "a@b.c")}}", "refresh_token": "r1", "expires_in": 3600 }""")
            : FakeHandler.Json("[]"));
        var client = new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore());

        await client.VerifyEmailCodeAsync("a@b.c", "123456");
        await client.GetAsync<List<Playgroup>>("playgroups");

        var verify = handler.Requests[0].Request;
        Assert.Null(verify.Headers.Authorization);
        var rest = handler.Requests[1].Request;
        Assert.Equal("sb_publishable_test123", rest.Headers.GetValues("apikey").Single());
        Assert.Equal("Bearer", rest.Headers.Authorization!.Scheme);
        Assert.Equal(client.Session!.AccessToken, rest.Headers.Authorization.Parameter);
    }

    [Fact]
    public void Options_AcceptPublishableAndLegacyAnonKeys()
    {
        var anon = $"{B64Url("""{"alg":"HS256"}""")}.{B64Url("""{"role":"anon"}""")}.sig"; // starts with "eyJ", like every JWT
        Assert.True(new SupabaseOptions("https://x.supabase.co", "sb_publishable_abc").IsConfigured);
        Assert.False(new SupabaseOptions("https://x.supabase.co", "sb_publishable_abc").IsLegacyKey);
        Assert.True(new SupabaseOptions("https://x.supabase.co", anon).IsConfigured);
        Assert.True(new SupabaseOptions("https://x.supabase.co", anon).IsLegacyKey);
    }

    [Theory]
    [InlineData("sb_secret_abc123")]
    [InlineData("service_role")]
    public void Options_RefuseSecretKeys(string kind)
    {
        var key = kind == "service_role"
            ? $"{B64Url("""{"alg":"HS256"}""")}.{B64Url("""{"role":"service_role"}""")}.sig"
            : kind;
        var options = new SupabaseOptions("https://x.supabase.co", key);
        Assert.False(options.IsConfigured);
        Assert.Contains("secret key", options.Problem);
    }

    private static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task ExpiredSession_IsRefreshedBeforeRestCalls()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/auth/v1/verify" => FakeHandler.Json($$"""{ "access_token": "{{Jwt(UserId, "a@b.c")}}", "refresh_token": "r1", "expires_in": 3600, "user": { "id": "{{UserId}}", "email": "a@b.c" } }"""),
            "/auth/v1/token" => FakeHandler.Json($$"""{ "access_token": "{{Jwt(UserId, "a@b.c")}}", "refresh_token": "r2", "expires_in": 3600 }"""),
            _ => FakeHandler.Json("[]"),
        });
        var client = new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore(), clock);

        await client.VerifyEmailCodeAsync("a@b.c", "123456");
        Assert.Equal("r1", client.Session!.RefreshToken);

        clock.Now += TimeSpan.FromMinutes(59.5);
        await client.GetAsync<List<Playgroup>>("playgroups");

        Assert.Equal("r2", client.Session!.RefreshToken);
        var paths = handler.Requests.Select(r => r.Request.RequestUri!.AbsolutePath).ToList();
        Assert.Equal(["/auth/v1/verify", "/auth/v1/token", "/rest/v1/playgroups"], paths);
        Assert.Contains("refresh_token=", handler.Requests[1].Request.RequestUri!.Query.Replace("grant_type=refresh_token", "refresh_token="));
    }

    [Fact]
    public async Task RevokedRefreshToken_SignsOut()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/auth/v1/verify"
            ? FakeHandler.Json($$"""{ "access_token": "{{Jwt(UserId, "a@b.c")}}", "refresh_token": "r1", "expires_in": 60 }""")
            : FakeHandler.Json("""{ "error_description": "Invalid Refresh Token" }""", HttpStatusCode.BadRequest));
        var client = new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore(), clock);
        await client.VerifyEmailCodeAsync("a@b.c", "123456");

        var error = await Assert.ThrowsAsync<SupabaseException>(() => client.GetAsync<List<Playgroup>>("playgroups"));
        Assert.Equal(401, error.Status);
        Assert.False(client.IsSignedIn);
    }

    [Fact]
    public async Task SupabaseDeckStore_UpsertsRowWithCardsAsJson()
    {
        var group = new Playgroup(Guid.NewGuid(), "Friday Night", "ABCD1234", UserId);
        string? sent = null;
        var handler = new FakeHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/rest/v1/decks")
            {
                sent = body;
                return FakeHandler.Json($"[{body![..^1]},\"updated_at\":\"2026-01-01T00:00:00+00:00\"}}]");
            }
            return FakeHandler.Json("{}");
        });
        var client = new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore());
        var store = new SupabaseDeckStore(client, group);

        var deck = new Deck { Name = "Goblins", OwnerName = "Sam" };
        deck.Add(TestCards.Krenko, DeckSection.Commander);

        var saved = await store.SaveAsync(deck);

        Assert.Contains($"\"playgroup_id\":\"{group.Id}\"", sent);
        Assert.Contains("\"section\":\"commander\"", sent);
        Assert.Contains("\"cover_image_url\":null", sent); // sent even when empty
        Assert.DoesNotContain("updated_at", sent);
        Assert.Equal("Krenko, Mob Boss", saved.Commanders.Single().Name);
        Assert.Equal("resolution=merge-duplicates,return=representation", handler.Requests.Last().Request.Headers.GetValues("Prefer").Single());
    }

    [Fact]
    public async Task LocalDeckStore_SavesListsAndDeletes()
    {
        var store = new LocalDeckStore(new MemoryKeyValueStore());
        var deck = new Deck { Name = "Burn", Format = DeckFormat.Modern.Id };
        deck.Add(TestCards.Bolt, DeckSection.Main, 4);

        await store.SaveAsync(deck);
        deck.Name = "Changed after saving"; // the store keeps its own copy
        var loaded = await store.GetAsync(deck.Id);

        Assert.Equal("Burn", loaded!.Name);
        Assert.Equal(4, loaded.Count(DeckSection.Main));
        Assert.Single(await store.ListAsync());

        await store.DeleteAsync(deck.Id);
        Assert.Empty(await store.ListAsync());
    }
}
