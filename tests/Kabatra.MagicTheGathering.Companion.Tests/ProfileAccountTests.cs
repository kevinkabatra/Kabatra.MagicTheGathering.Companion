using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;
using Kabatra.MagicTheGathering.Companion.Core.Profiles;

namespace Kabatra.MagicTheGathering.Companion.Tests;

public class ProfileAccountTests
{
    private static readonly SupabaseOptions Options = new("https://example.supabase.co", "sb_publishable_test123");

    private static (ProfileAccount Account, FakeHandler Handler) Make(string response)
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(response));
        return (new ProfileAccount(new SupabaseClient(new HttpClient(handler), Options, new MemoryKeyValueStore())), handler);
    }

    [Fact]
    public async Task State_ReadsWhetherTheUserCanClaimAndHowManyTriesAreLeft()
    {
        var (account, handler) = Make("""{ "profile_id": null, "can_claim": true, "blocked": false, "locked_until": null, "attempts_left": 2 }""");

        var state = await account.GetStateAsync();

        Assert.Equal("/rest/v1/rpc/onboarding_state", handler.Requests.Single().Request.RequestUri!.AbsolutePath);
        Assert.False(state.HasProfile);
        Assert.True(state.CanClaim);
        Assert.Equal(2, state.AttemptsLeft);
    }

    [Fact]
    public async Task CreateMine_LeavesTheNameOutWhenBlank_SoTheDatabaseUsesTheAccountsName()
    {
        var (account, handler) = Make("""{ "id": "6f0c5b8e-0000-4000-8000-000000000001", "name": "Sam Smith", "user_id": "6f0c5b8e-0000-4000-8000-0000000000aa", "player_code": "PLAY2345", "collection": [], "updated_at": "2026-01-01T00:00:00Z" }""");

        var profile = await account.CreateMineAsync("   ");

        var (request, body) = handler.Requests.Single();
        Assert.Equal("/rest/v1/rpc/create_my_profile", request.RequestUri!.AbsolutePath);
        Assert.Equal("{}", body);
        Assert.Equal("Sam Smith", profile.Name);
        Assert.True(profile.IsClaimed);
    }

    [Fact]
    public async Task CreateMine_SendsAChosenName()
    {
        var (account, handler) = Make("""{ "id": "6f0c5b8e-0000-4000-8000-000000000001", "name": "Sam", "collection": [], "updated_at": "2026-01-01T00:00:00Z" }""");
        await account.CreateMineAsync(" Sam ");
        Assert.Contains("\"profile_name\":\"Sam\"", handler.Requests.Single().Body);
    }

    [Theory]
    [InlineData("""{ "status": "claimed", "profile_id": "6f0c5b8e-0000-4000-8000-000000000001" }""", ClaimStatus.Claimed, 0)]
    [InlineData("""{ "status": "wrong_code", "attempts_left": 2 }""", ClaimStatus.WrongCode, 2)]
    [InlineData("""{ "status": "locked", "locked_until": "2026-11-13T20:15:00+00:00", "attempts_left": 0 }""", ClaimStatus.Locked, 0)]
    [InlineData("""{ "status": "blocked", "attempts_left": 0 }""", ClaimStatus.Blocked, 0)]
    public async Task Claim_ReadsEachOutcome(string response, ClaimStatus status, int attemptsLeft)
    {
        var (account, handler) = Make(response);

        var result = await account.ClaimAsync(" abcd-2345 ");

        var (request, body) = handler.Requests.Single();
        Assert.Equal("/rest/v1/rpc/claim_profile", request.RequestUri!.AbsolutePath);
        Assert.Equal("""{"code":"ABCD2345"}""", body);
        Assert.Equal(status, result.Status);
        Assert.Equal(attemptsLeft, result.AttemptsLeft);
        if (status == ClaimStatus.Locked) Assert.Equal(DateTimeOffset.Parse("2026-11-13T20:15:00Z"), result.LockedUntil);
        if (status == ClaimStatus.Claimed) Assert.NotNull(result.ProfileId);
    }

    [Fact]
    public async Task ClaimCode_IsFetchedForAProfile_AndCanBeReset()
    {
        var (account, handler) = Make("\"NEWC0DE2\"");
        var id = Guid.NewGuid();

        var code = await account.GetClaimCodeAsync(id, reset: true);

        var (request, body) = handler.Requests.Single();
        Assert.Equal("/rest/v1/rpc/profile_claim_code", request.RequestUri!.AbsolutePath);
        Assert.Contains($"\"profile\":\"{id}\"", body);
        Assert.Contains("\"reset\":true", body);
        Assert.Equal("NEWC0DE2", code);
    }

    [Theory]
    [InlineData(" abcd 2345 ", "ABCD2345")]
    [InlineData("ab-cd-23-45", "ABCD2345")]
    [InlineData(null, "")]
    public void Codes_IgnoreCaseSpacesAndDashes(string? typed, string expected) =>
        Assert.Equal(expected, ProfileAccount.NormalizeCode(typed));
}
