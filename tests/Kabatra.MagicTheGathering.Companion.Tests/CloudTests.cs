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

    /// <summary>Goes through an OAuth sign-in: start it, then come back with a code.</summary>
    private static async Task SignInAsync(SupabaseClient client)
    {
        await client.BeginSignInAsync("google", "https://site/app/decks");
        Assert.True(await client.TryCompleteSignInAsync(new Uri("https://site/app/decks?code=abc123")));
    }

    private static bool IsPkceExchange(HttpRequestMessage r) =>
        r.RequestUri!.AbsolutePath == "/auth/v1/token" && r.RequestUri.Query.Contains("grant_type=pkce");

    [Fact]
    public async Task BeginSignIn_PointsAtTheProvidersAuthorizePage_WithAPkceChallenge()
    {
        var store = new MemoryKeyValueStore();
        var client = new SupabaseClient(new HttpClient(), Options, store);

        var url = new Uri(await client.BeginSignInAsync(" Apple ", "https://site/app/decks?x=1"));

        Assert.Equal("https://example.supabase.co/auth/v1/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Contains("provider=apple", url.Query);
        Assert.Contains("redirect_to=https%3A%2F%2Fsite%2Fapp%2Fdecks%3Fx%3D1", url.Query);
        Assert.Contains("code_challenge_method=s256", url.Query);

        // The challenge is the SHA-256 of the verifier kept on the device, base64url-encoded.
        var verifier = await store.GetAsync("mtg-companion.supabase-pkce-verifier");
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier!)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Contains($"code_challenge={expected}&", url.Query);
    }

    [Fact]
    public async Task CompleteSignIn_ExchangesTheCodeWithTheVerifier_AndSavesTheSession()
    {
        var store = new MemoryKeyValueStore();
        var handler = new FakeHandler((request, _) => IsPkceExchange(request)
            ? FakeHandler.Json($$"""{ "access_token": "{{Jwt(UserId, "a@b.c")}}", "refresh_token": "r1", "expires_in": 3600, "user": { "id": "{{UserId}}", "email": "a@b.c" } }""")
            : FakeHandler.Json("{}"));
        var client = new SupabaseClient(new HttpClient(handler), Options, store);
        var changed = 0;
        client.SessionChanged += () => changed++;

        await client.BeginSignInAsync("google", "https://site/app/");
        var verifier = await store.GetAsync("mtg-companion.supabase-pkce-verifier");
        Assert.True(await client.TryCompleteSignInAsync(new Uri("https://site/app/?code=abc123")));

        var (request, body) = handler.Requests.Single();
        Assert.Contains("\"auth_code\":\"abc123\"", body);
        Assert.Contains($"\"code_verifier\":\"{verifier}\"", body);
        Assert.Equal("sb_publishable_test123", request.Headers.GetValues("apikey").Single());
        // Publishable keys aren't JWTs, so they must never be sent as a Bearer token.
        Assert.Null(request.Headers.Authorization);

        Assert.Equal(UserId, client.Session!.UserId);
        Assert.Equal("a@b.c", client.Session.Email);
        Assert.Equal(1, changed);
        Assert.Null(await store.GetAsync("mtg-companion.supabase-pkce-verifier")); // single use

        // A fresh client picks the session back up from storage.
        var restored = new SupabaseClient(new HttpClient(), Options, store);
        await restored.InitializeAsync();
        Assert.Equal(UserId, restored.Session?.UserId);
    }

    [Fact]
    public async Task CompleteSignIn_IgnoresCodesItDidntAskFor_AndOrdinaryAddresses()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("{}"));
        var client = new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore());

        Assert.False(await client.TryCompleteSignInAsync(new Uri("https://site/app/?code=not-ours")));
        Assert.False(await client.TryCompleteSignInAsync(new Uri("https://site/decks#top")));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("?error=access_denied&error_description=User+cancelled", "User cancelled")]
    [InlineData("?error=access_denied", "Sign-in was cancelled.")]
    [InlineData("#error=server_error&error_description=User+is+banned", SupabaseClient.BlockedMessage)]
    public async Task CompleteSignIn_ProviderErrorsThrowReadableMessages(string suffix, string message)
    {
        var client = new SupabaseClient(new HttpClient(), Options, new MemoryKeyValueStore());
        var error = await Assert.ThrowsAsync<SupabaseException>(() => client.TryCompleteSignInAsync(new Uri("https://site/app/" + suffix)));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Providers_DefaultToGoogleAndApple_AndAcceptOthersByName()
    {
        Assert.Equal(["google", "apple"], AuthProviders.From(null).Select(p => p.Id));
        Assert.Equal(["Google", "Apple"], AuthProviders.From([]).Select(p => p.Label));

        var custom = AuthProviders.From([" GitHub ", "github", "", "keycloak"]);
        Assert.Equal([new AuthProvider("github", "GitHub"), new AuthProvider("keycloak", "Keycloak")], custom);
    }

    [Fact]
    public async Task SignedInRequests_SendUserTokenAsBearer_AndKeyOnlyAsApiKey()
    {
        var handler = new FakeHandler((request, _) => IsPkceExchange(request)
            ? FakeHandler.Json($$"""{ "access_token": "{{Jwt(UserId, "a@b.c")}}", "refresh_token": "r1", "expires_in": 3600 }""")
            : FakeHandler.Json("[]"));
        var client = new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore());

        await SignInAsync(client);
        await client.GetAsync<List<Playgroup>>("playgroups");

        var rest = handler.Requests[1].Request;
        Assert.Equal("sb_publishable_test123", rest.Headers.GetValues("apikey").Single());
        Assert.Equal("Bearer", rest.Headers.Authorization!.Scheme);
        Assert.Equal(client.Session!.AccessToken, rest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task OfflineAtStartup_KeepsTheSavedSession()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new MemoryKeyValueStore();
        var online = new SupabaseClient(new HttpClient(new FakeHandler((_, _) => FakeHandler.Json(
            $$"""{ "access_token": "{{Jwt(UserId, "a@b.c")}}", "refresh_token": "r1", "expires_in": 60 }"""))), Options, store, clock);
        await SignInAsync(online);

        clock.Now += TimeSpan.FromHours(2); // expired, so starting up tries to refresh
        var offline = new SupabaseClient(new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException("offline"))), Options, store, clock);
        await offline.InitializeAsync();

        Assert.True(offline.IsSignedIn);
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
            "/auth/v1/token" when IsPkceExchange(request) => FakeHandler.Json($$"""{ "access_token": "{{Jwt(UserId, "a@b.c")}}", "refresh_token": "r1", "expires_in": 3600, "user": { "id": "{{UserId}}", "email": "a@b.c" } }"""),
            "/auth/v1/token" => FakeHandler.Json($$"""{ "access_token": "{{Jwt(UserId, "a@b.c")}}", "refresh_token": "r2", "expires_in": 3600 }"""),
            _ => FakeHandler.Json("[]"),
        });
        var client = new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore(), clock);

        await SignInAsync(client);
        Assert.Equal("r1", client.Session!.RefreshToken);

        clock.Now += TimeSpan.FromMinutes(59.5);
        await client.GetAsync<List<Playgroup>>("playgroups");

        Assert.Equal("r2", client.Session!.RefreshToken);
        var paths = handler.Requests.Select(r => r.Request.RequestUri!.AbsolutePath).ToList();
        Assert.Equal(["/auth/v1/token", "/auth/v1/token", "/rest/v1/playgroups"], paths);
        Assert.Contains("refresh_token=", handler.Requests[1].Request.RequestUri!.Query.Replace("grant_type=refresh_token", "refresh_token="));
    }

    [Fact]
    public async Task RevokedRefreshToken_SignsOut()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var handler = new FakeHandler((request, _) => IsPkceExchange(request)
            ? FakeHandler.Json($$"""{ "access_token": "{{Jwt(UserId, "a@b.c")}}", "refresh_token": "r1", "expires_in": 60 }""")
            : FakeHandler.Json("""{ "error_description": "Invalid Refresh Token" }""", HttpStatusCode.BadRequest));
        var client = new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore(), clock);
        await SignInAsync(client);

        var error = await Assert.ThrowsAsync<SupabaseException>(() => client.GetAsync<List<Playgroup>>("playgroups"));
        Assert.Equal(401, error.Status);
        Assert.False(client.IsSignedIn);
    }

    [Fact]
    public async Task SupabaseDeckStore_UpsertsTheDeckForItsOwnerProfile()
    {
        var group = new Playgroup(Guid.NewGuid(), "Friday Night", Guid.NewGuid());
        var me = Guid.NewGuid();
        string? sent = null;
        var handler = new FakeHandler((request, body) =>
        {
            sent = body;
            return FakeHandler.Json($"[{body![..^1]},\"updated_at\":\"2026-01-01T00:00:00+00:00\",\"owner\":{{\"name\":\"Sam\"}}}}]");
        });
        var store = new SupabaseDeckStore(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore()), group, me);

        var deck = new Deck { Name = "Goblins", OwnerName = "typed by hand" };
        deck.Add(TestCards.Krenko, DeckSection.Commander);
        var saved = await store.SaveAsync(deck);

        var request = handler.Requests.Single().Request;
        Assert.Equal("/rest/v1/decks", request.RequestUri!.AbsolutePath);
        Assert.Contains("owner:player_profiles(name)", Uri.UnescapeDataString(request.RequestUri.Query));
        Assert.Contains($"\"owner_profile_id\":\"{me}\"", sent); // no owner yet: it's yours
        Assert.Contains("\"section\":\"commander\"", sent);
        Assert.Contains("\"cover_image_url\":null", sent); // sent even when empty
        // Decks belong to profiles now, not playgroups, and the owner's name comes from their profile.
        Assert.DoesNotContain("playgroup_id", sent);
        Assert.DoesNotContain("owner_name", sent);
        Assert.DoesNotContain("updated_at", sent);
        Assert.Equal("Sam", saved.OwnerName);
        Assert.Equal("Krenko, Mob Boss", saved.Commanders.Single().Name);
        Assert.Equal("resolution=merge-duplicates,return=representation", request.Headers.GetValues("Prefer").Single());
    }

    [Fact]
    public async Task SupabaseDeckStore_ListsDecksWhoseOwnersAreInTheGroup()
    {
        var group = new Playgroup(Guid.NewGuid(), "Friday Night", Guid.NewGuid());
        var handler = new FakeHandler((_, _) => FakeHandler.Json($$"""
            [{ "id": "{{Guid.NewGuid()}}", "owner_profile_id": "{{Guid.NewGuid()}}", "name": "Burn", "format": "modern", "cards": [],
               "updated_at": "2026-01-01T00:00:00Z", "owner": { "name": "Alex", "playgroup_profiles": [{ "playgroup_id": "{{group.Id}}" }] } }]
            """));
        var store = new SupabaseDeckStore(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore()), group, Guid.NewGuid());

        var deck = (await store.ListAsync()).Single();

        var query = Uri.UnescapeDataString(handler.Requests.Single().Request.RequestUri!.Query);
        Assert.Contains("owner:player_profiles!inner(name,playgroup_profiles!inner(playgroup_id))", query);
        Assert.Contains($"owner.playgroup_profiles.playgroup_id=eq.{group.Id}", query);
        Assert.Equal("Burn", deck.Name);
        Assert.Equal("Alex", deck.OwnerName);
    }

    [Fact]
    public async Task SupabaseDeckStore_SavingSomeoneElsesDeckFails()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("[]"));
        var store = new SupabaseDeckStore(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore()),
            new Playgroup(Guid.NewGuid(), "Friday Night", Guid.NewGuid()), Guid.NewGuid());

        var error = await Assert.ThrowsAsync<SupabaseException>(() => store.SaveAsync(new Deck { Name = "Burn" }));
        Assert.Equal(403, error.Status);
    }

    // ----- Playgroups -----

    private static (PlaygroupService Service, FakeHandler Handler) Playgroups(string response)
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(response));
        return (new PlaygroupService(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore())), handler);
    }

    [Fact]
    public async Task Playgroups_ListMine_NeverAsksForTheJoinCode()
    {
        var admin = Guid.NewGuid();
        var (service, handler) = Playgroups($$"""[{ "id": "{{Guid.NewGuid()}}", "name": "Friday Night", "admin_profile_id": "{{admin}}" }]""");

        var group = (await service.ListMineAsync()).Single();

        Assert.DoesNotContain("join_code", handler.Requests.Single().Request.RequestUri!.Query);
        Assert.Equal(admin, group.AdminProfileId);
    }

    [Theory]
    [InlineData("create", "/rest/v1/rpc/create_playgroup", "\"group_name\":\"Friday Night\"")]
    [InlineData("join", "/rest/v1/rpc/join_playgroup", "\"code\":\"ABCD2345\"")]
    [InlineData("transfer", "/rest/v1/rpc/transfer_playgroup", "\"new_admin\":")]
    public async Task Playgroups_CallTheDatabaseFunctions(string action, string path, string bodyPart)
    {
        var (service, handler) = Playgroups($$"""{ "id": "{{Guid.NewGuid()}}", "name": "Friday Night", "admin_profile_id": "{{Guid.NewGuid()}}" }""");

        var group = action switch
        {
            "create" => await service.CreateAsync(" Friday Night "),
            "join" => await service.JoinAsync(" abcd-2345 "),
            _ => await service.TransferAsync(Guid.NewGuid(), Guid.NewGuid()),
        };

        var (request, body) = handler.Requests.Single();
        Assert.Equal(path, request.RequestUri!.AbsolutePath);
        Assert.Contains(bodyPart, body);
        Assert.Equal("Friday Night", group.Name);
    }

    [Fact]
    public async Task Playgroups_JoinCodeIsFetchedThroughTheAdminOnlyFunction()
    {
        var (service, handler) = Playgroups("\"JOIN2345\"");
        var id = Guid.NewGuid();

        Assert.Equal("JOIN2345", await service.GetJoinCodeAsync(id, reset: true));

        var (request, body) = handler.Requests.Single();
        Assert.Equal("/rest/v1/rpc/playgroup_join_code", request.RequestUri!.AbsolutePath);
        Assert.Contains($"\"group_id\":\"{id}\"", body);
        Assert.Contains("\"reset\":true", body);
    }

    [Fact]
    public async Task Playgroups_AddByPlayerCode_AndRemoveAProfile()
    {
        var profile = Guid.NewGuid();
        var (service, handler) = Playgroups($$"""{ "id": "{{profile}}", "name": "Sam" }""");
        var group = Guid.NewGuid();

        Assert.Equal(profile, await service.AddByPlayerCodeAsync(group, "play 2345"));
        await service.RemoveProfileAsync(group, profile);

        Assert.Equal("/rest/v1/rpc/add_profile_to_playgroup", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"code\":\"PLAY2345\"", handler.Requests[0].Body);
        var remove = handler.Requests[1].Request;
        Assert.Equal(HttpMethod.Delete, remove.Method);
        Assert.Equal($"?playgroup_id=eq.{group}&profile_id=eq.{profile}", remove.RequestUri!.Query);
    }

    [Fact]
    public async Task Playgroups_RenameThatComesBackEmptyMeansNotAdmin()
    {
        var (service, _) = Playgroups("[]");
        var error = await Assert.ThrowsAsync<SupabaseException>(() => service.RenameAsync(Guid.NewGuid(), "New name"));
        Assert.Equal(403, error.Status);
    }
}
