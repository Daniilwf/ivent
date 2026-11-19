using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using static GameEvent.Web.Tests.Api.EventSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>D-546 (11): test tools prepare real, inspectable states through the existing season commands.</summary>
public sealed class Stage5TestScenariosTests : IAsyncLifetime
{
    internal const string IrlProofPending = "irl-proof-pending";
    internal const string CoopInvitation = "coop-invitation";
    internal const string PollAndGlobalGoal = "poll-and-global-goal";

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task An_existing_irl_effect_gets_a_pending_proof_visible_to_its_owner_and_admin()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Karaoke);
        await DropAsync(player);
        var effect = await SingleEffectAsync(player);
        var effectId = effect.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, effect.GetProperty("irlProof").ValueKind);
        Assert.Empty((await ReadAsync(admin, $"{Admin}/irl-proofs")).EnumerateArray());

        await ApiCalls.OkAsync(await admin.PostAsJsonAsync(Scenario(IrlProofPending), new { playerId = _site.Players["vasya"] }, Ct));

        var prepared = await SingleEffectAsync(player);
        Assert.Equal(effectId, prepared.GetProperty("id").GetGuid());
        Assert.True(prepared.GetProperty("irl").GetBoolean());
        var proof = prepared.GetProperty("irlProof");
        Assert.Equal("pending", proof.GetProperty("status").GetString());
        var link = proof.GetProperty("link").GetString();
        Assert.True(Uri.TryCreate(link, UriKind.Absolute, out var evidence) && evidence.Scheme == Uri.UriSchemeHttps);

        var queued = Assert.Single((await ReadAsync(admin, $"{Admin}/irl-proofs")).EnumerateArray());
        Assert.Equal(effectId, queued.GetProperty("effectId").GetGuid());
        Assert.Equal(_site.Players["vasya"], queued.GetProperty("playerId").GetGuid());
        Assert.Equal(link, queued.GetProperty("link").GetString());
        Assert.Equal(proof.GetProperty("submittedAt").GetDateTimeOffset(), queued.GetProperty("submittedAt").GetDateTimeOffset());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task An_effect_without_media_proof_is_left_untouched()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Toast);
        await DropAsync(player);
        var before = await SequenceAsync(admin);

        var result = await admin.PostAsJsonAsync(Scenario(IrlProofPending), new { playerId = _site.Players["vasya"] }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Equal(before, await SequenceAsync(admin));
        Assert.Equal(JsonValueKind.Null, (await SingleEffectAsync(player)).GetProperty("irlProof").ValueKind);
        Assert.Empty((await ReadAsync(admin, $"{Admin}/irl-proofs")).EnumerateArray());
    }

    [Fact]
    public async Task An_active_coop_run_gets_one_invitation_that_the_partner_can_answer()
    {
        await CoopOnAsync(_site);
        var run = await PlayingAsync(_site, _site.Players["vasya"]);
        using var admin = await _site.SignedInAsync("admin");
        using var roller = await _site.SignedInAsync("vasya");
        using var partner = await _site.SignedInAsync("petya");

        await ApiCalls.OkAsync(await admin.PostAsJsonAsync(Scenario(CoopInvitation), new { playerId = _site.Players["vasya"] }, Ct));

        var outgoing = Assert.Single((await ReadAsync(roller, $"{Season}/interactions")).EnumerateArray());
        var incoming = Assert.Single((await ReadAsync(partner, $"{Season}/interactions")).EnumerateArray());
        var invitation = incoming.GetProperty("id").GetGuid();
        Assert.Equal(invitation, outgoing.GetProperty("id").GetGuid());
        Assert.Equal("coop", incoming.GetProperty("kind").GetString());
        Assert.Equal("pending", incoming.GetProperty("status").GetString());
        Assert.Equal(run, incoming.GetProperty("runId").GetGuid());
        Assert.Equal(_site.Players["vasya"], incoming.GetProperty("fromPlayerId").GetGuid());
        Assert.Equal(_site.Players["petya"], incoming.GetProperty("toPlayerId").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(partner, $"{Season}/coop")).ValueKind);

        await ApiCalls.PostOkAsync(partner, $"{Season}/interactions/{invitation}/answer", new { commandId = Guid.NewGuid(), accept = true });
        Assert.Equal(run, (await ReadAsync(partner, $"{Season}/coop")).GetProperty("runId").GetGuid());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task An_ineligible_partner_does_not_leave_a_partial_scenario()
    {
        await CoopOnAsync(_site);
        await PlayingAsync(_site, _site.Players["vasya"]);
        var otherRun = await PlayingAsync(_site, _site.Players["petya"]);
        using var admin = await _site.SignedInAsync("admin");
        using var roller = await _site.SignedInAsync("vasya");
        using var partner = await _site.SignedInAsync("petya");
        var before = await SequenceAsync(admin);

        var result = await admin.PostAsJsonAsync(Scenario(CoopInvitation), new { playerId = _site.Players["vasya"] }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Equal(before, await SequenceAsync(admin));
        Assert.Empty((await ReadAsync(roller, $"{Season}/interactions")).EnumerateArray());
        Assert.Empty((await ReadAsync(partner, $"{Season}/interactions")).EnumerateArray());
        Assert.Equal(otherRun, (await MeAsync(partner)).GetProperty("activeRun").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_full_global_event_limit_leaves_the_poll_uncreated()
    {
        using var admin = await _site.SignedInAsync("admin");
        await SetUpPollAndGoalAsync(_site, admin);
        await ApiCalls.PostOkAsync(admin, $"{Admin}/global-events", new { commandId = Guid.NewGuid(), objectId = "tailwind", durationHours = 24 });
        // The queue settles Tailwind for everyone before the next command, which sets this test's one-event limit.
        await RulesAsync(_site, r => r with { GlobalEvents = r.GlobalEvents! with { MaxActive = 1 } });
        var before = await SequenceAsync(admin);
        var existing = Assert.Single((await ReadAsync(admin, $"{Season}/global-events")).EnumerateArray());

        await AssertConflictAsync(await admin.PostAsJsonAsync(Scenario(PollAndGlobalGoal), new { }, Ct), "globalEvent.tooMany");
        Assert.Equal(before, await SequenceAsync(admin));
        Assert.Empty((await ReadAsync(admin, $"{Season}/polls")).EnumerateArray());
        var remaining = Assert.Single((await ReadAsync(admin, $"{Season}/global-events")).EnumerateArray());
        Assert.Equal(existing.GetProperty("id").GetGuid(), remaining.GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData(IrlProofPending)]
    [InlineData(CoopInvitation)]
    public async Task A_player_scenario_rejects_malformed_and_unknown_player_ids(string scenario)
    {
        using var admin = await _site.SignedInAsync("admin");
        var before = await SequenceAsync(admin);

        var malformed = await admin.PostAsJsonAsync(Scenario(scenario), new { playerId = "vasya" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        await AssertConflictAsync(await admin.PostAsJsonAsync(Scenario(scenario), new { playerId = Guid.NewGuid() }, Ct), "test.playerUnknown");

        Assert.Equal(before, await SequenceAsync(admin));
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_cannot_run_the_new_scenarios(string login)
    {
        using var client = await _site.SignedInAsync(login);
        foreach (var scenario in new[] { IrlProofPending, CoopInvitation, PollAndGlobalGoal })
        {
            var result = await client.PostAsJsonAsync(Scenario(scenario), new { }, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        }
    }

    internal static string Scenario(string name) => $"/api/test/seasons/{SiteFactory.SeasonId}/scenarios/{name}";

    internal static async Task<JsonElement> ReadAsync(HttpClient client, string path) =>
        await ApiCalls.OkAsync(await client.GetAsync(path, Ct));

    internal static async Task SetUpPollAndGoalAsync(SiteFactory site, HttpClient admin)
    {
        await SetUpAsync(admin, Tailwind);
        await RulesAsync(site, Flags(features => features with { GlobalEvents = true, Polls = true }));
    }

    private static async Task<long> SequenceAsync(HttpClient admin) =>
        (await ReadAsync(admin, Season)).GetProperty("lastSequence").GetInt64();
}

/// <summary>D-220: the same administrator can run the new scenarios on each test copy; the live API has no routes.</summary>
public sealed class Stage5TestScenarioAvailabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    [InlineData("Staging")]
    public async Task An_admin_can_prepare_an_open_poll_and_active_global_goal_on_a_test_copy(string environment)
    {
        await using var site = new SiteFactory(loginAttemptsPerMinute: 1000, environment: environment);
        await site.SeedAsync();
        using var admin = await SignedInOverHttpsAsync(site, "admin");
        var tools = await Stage5TestScenariosTests.ReadAsync(admin, "/api/test");
        var scenarios = tools.GetProperty("scenarios").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains(Stage5TestScenariosTests.IrlProofPending, scenarios);
        Assert.Contains(Stage5TestScenariosTests.CoopInvitation, scenarios);
        Assert.Contains(Stage5TestScenariosTests.PollAndGlobalGoal, scenarios);
        await Stage5TestScenariosTests.SetUpPollAndGoalAsync(site, admin);

        await ApiCalls.OkAsync(await admin.PostAsJsonAsync(Stage5TestScenariosTests.Scenario(Stage5TestScenariosTests.PollAndGlobalGoal), new { }, Ct));

        using var spectator = await SignedInOverHttpsAsync(site, "zritel");
        var poll = Assert.Single((await Stage5TestScenariosTests.ReadAsync(spectator, $"{Season}/polls")).EnumerateArray());
        Assert.Equal("open", poll.GetProperty("status").GetString());
        Assert.True(poll.GetProperty("options").GetArrayLength() >= 2);
        Assert.True(poll.GetProperty("closesAt").GetDateTimeOffset() > site.Clock.UtcNow);
        var banner = Assert.Single((await Stage5TestScenariosTests.ReadAsync(spectator, Season)).GetProperty("globalEvents").EnumerateArray());
        Assert.Equal("tailwind", banner.GetProperty("objectId").GetString());
        Assert.Equal("active", banner.GetProperty("status").GetString());
        Assert.True(banner.GetProperty("endsAt").GetDateTimeOffset() > site.Clock.UtcNow);
        var goal = banner.GetProperty("goal");
        Assert.Equal("runs", goal.GetProperty("metric").GetString());
        Assert.True(goal.GetProperty("target").GetDecimal() > 0);
        Assert.Equal(0m, goal.GetProperty("progress").GetDecimal());
        Assert.False(goal.GetProperty("reached").GetBoolean());
        await AssertIntactAsync(site);
    }

    [Fact]
    public async Task Even_the_admin_cannot_find_stage_5_scenario_routes_in_production()
    {
        await using var site = new SiteFactory(loginAttemptsPerMinute: 1000, environment: "Production");
        await site.SeedAsync();
        using var admin = await SignedInOverHttpsAsync(site, "admin");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/test", Ct)).StatusCode);
        foreach (var scenario in new[] { Stage5TestScenariosTests.IrlProofPending, Stage5TestScenariosTests.CoopInvitation, Stage5TestScenariosTests.PollAndGlobalGoal })
        {
            var result = await admin.PostAsJsonAsync(Stage5TestScenariosTests.Scenario(scenario), new { }, Ct);
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        }
    }

    private static async Task<HttpClient> SignedInOverHttpsAsync(SiteFactory site, string login)
    {
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct)).EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
        return client;
    }
}
