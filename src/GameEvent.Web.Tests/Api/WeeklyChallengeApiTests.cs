using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The weekly challenge over HTTP (task 5.19; SPEC «Челлендж недели», D-526, D-570, D-574, D-578, D-833): the admin sets a
/// challenge (CONTENT.md format) with its start, marks a player of a manual one with the run; everyone sees the list with
/// the window, the reward and who completed it. Every endpoint: success, another role or another player's run, invalid
/// input. Written by <c>test-author</c> before the endpoints.
/// </summary>
public sealed class WeeklyChallengeApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await RulesAsync(_site, Flags(f => f with { WeeklyChallenge = true }));
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private static object Challenge(string check = "auto", int coins = 10) => new
    {
        id = check == "auto" ? "horror-week" : "horror-stream",
        name = check == "auto" ? "Неделя хоррора" : "Хоррор на стриме",
        description = "Пройди хоррор.",
        condition = new { game = new { tags = new[] { "Horror" } } },
        reward = new { coins },
        check,
    };

    private async Task<Guid> SetAsync(HttpClient admin, string check = "auto")
    {
        var before = (await StateAsync(_site)).WeeklyChallenges.Keys.ToHashSet();
        await ApiCalls.PostOkAsync(admin, AdminUrl("weekly-challenges"), new { commandId = Guid.NewGuid(), challenge = Challenge(check), startsAt = _site.Clock.UtcNow });
        return (await StateAsync(_site)).WeeklyChallenges.Keys.Single(id => !before.Contains(id));
    }

    private static async Task<JsonElement> ListAsync(HttpClient client) => await ApiCalls.OkAsync(await client.GetAsync(Url("weekly-challenges"), Ct));

    private async Task<Guid> CompletedAsync(string login)
    {
        var client = await _site.SignedInAsync(login);
        await ApiCalls.PostOkAsync(client, Url("roll"));
        await ApiCalls.PostOkAsync(client, Url("start"));
        await ApiCalls.PostOkAsync(client, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        await using var db = _site.NewDb();
        return (await db.Runs.AsNoTracking().Where(r => r.PlayerId == _site.Players[login]).OrderByDescending(r => r.StartedAt).FirstAsync(Ct)).Id;
    }

    private Task<HttpResponseMessage> MarkAsync(HttpClient admin, Guid challengeId, string login, Guid runId) =>
        admin.PostAsJsonAsync(AdminUrl($"weekly-challenges/{challengeId}/mark"), new { commandId = Guid.NewGuid(), playerId = _site.Players[login], runId }, Ct);

    private static IReadOnlyList<Guid> CompletedBy(JsonElement challenge) =>
        [.. challenge.GetProperty("completedBy").EnumerateArray().Select(p => p.GetProperty("playerId").GetGuid())];

    // ---- Set ----

    [Fact]
    public async Task The_admin_sets_a_challenge_and_everyone_sees_its_window_and_reward()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(
            AdminUrl("weekly-challenges"), new { commandId = Guid.NewGuid(), challenge = Challenge(), startsAt = _site.Clock.UtcNow }, Ct);

        Assert.Equal(["weekly-challenge-set"], await TypesAsync(response));
        var state = Assert.Single((await StateAsync(_site)).WeeklyChallenges.Values);
        foreach (var login in new[] { "vasya", "zritel", "masha" })
        {
            var item = Assert.Single((await ListAsync(await _site.SignedInAsync(login))).EnumerateArray());
            Assert.Equal(state.ChallengeId, item.GetProperty("id").GetGuid());
            Assert.Equal("Неделя хоррора", item.GetProperty("name").GetString());
            Assert.Equal("Пройди хоррор.", item.GetProperty("description").GetString());
            Assert.Equal("auto", item.GetProperty("check").GetString());
            Assert.Equal(10, item.GetProperty("rewardCoins").GetInt32());
            Assert.Equal(state.StartsAt, item.GetProperty("startsAt").GetDateTimeOffset());
            Assert.Equal(state.StartsAt.AddDays(7), item.GetProperty("endsAt").GetDateTimeOffset());
            Assert.Empty(CompletedBy(item));
        }
    }

    [Theory]
    [InlineData("""{"challenge":{"id":"w","name":"W","description":"D","condition":{},"reward":{"coins":5},"check":"auto"},"startsAt":"2026-10-01T12:00:00Z"}""")]
    [InlineData("""{"commandId":"{id}","startsAt":"2026-10-01T12:00:00Z"}""")]
    [InlineData("""{"commandId":"{id}","challenge":{"id":"w","name":"W","description":"D","condition":{},"reward":{"coins":5},"check":"sometimes"},"startsAt":"2026-10-01T12:00:00Z"}""")]
    [InlineData("""{"commandId":"{id}","challenge":{"id":"w","description":"D","condition":{},"reward":{"coins":5},"check":"auto"},"startsAt":"2026-10-01T12:00:00Z"}""")]
    [InlineData("""{"commandId":"{id}","challenge":{"id":"w","name":"W","description":"D","condition":{},"reward":{"coins":5},"check":"auto"},"startsAt":"на неделе"}""")]
    public async Task Setting_a_challenge_with_invalid_input_is_a_bad_request(string json)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await PostJsonAsync(admin, AdminUrl("weekly-challenges"), json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await StateAsync(_site)).WeeklyChallenges);
    }

    [Fact]
    public async Task With_the_weekly_challenge_off_the_list_and_the_set_are_refused()
    {
        await RulesAsync(_site, Flags(f => f with { WeeklyChallenge = false }));
        var admin = await _site.SignedInAsync("admin");

        await AssertConflictAsync(await (await _site.SignedInAsync("vasya")).GetAsync(Url("weekly-challenges"), Ct), "feature.disabled");
        await AssertConflictAsync(
            await admin.PostAsJsonAsync(AdminUrl("weekly-challenges"), new { commandId = Guid.NewGuid(), challenge = Challenge(), startsAt = _site.Clock.UtcNow }, Ct),
            "feature.disabled");
    }

    // ---- Who completed it ----

    [Fact]
    public async Task An_automatic_challenge_lists_the_player_who_completed_a_fitting_game_in_its_window()
    {
        var admin = await _site.SignedInAsync("admin");
        await SetAsync(admin);

        await CompletedAsync("vasya");

        var item = Assert.Single((await ListAsync(await _site.SignedInAsync("petya"))).EnumerateArray());
        Assert.Equal([_site.Players["vasya"]], CompletedBy(item));
        Assert.Equal("vasya", Assert.Single(item.GetProperty("completedBy").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_rejected_proof_takes_the_player_off_the_list()
    {
        var admin = await _site.SignedInAsync("admin");
        await SetAsync(admin);
        var run = await CompletedAsync("vasya");

        await ApiCalls.PostOkAsync(admin, AdminUrl($"runs/{run}/reject"), new { commandId = Guid.NewGuid(), comment = "Не то" });

        Assert.Empty(CompletedBy(Assert.Single((await ListAsync(admin)).EnumerateArray())));
    }

    // ---- Mark ----

    [Fact]
    public async Task The_admin_marks_a_player_of_a_manual_challenge_with_the_run()
    {
        var admin = await _site.SignedInAsync("admin");
        var challenge = await SetAsync(admin, "manual");
        var run = await CompletedAsync("petya");

        var response = await MarkAsync(admin, challenge, "petya", run);

        Assert.Contains("weekly-challenge-completed", await TypesAsync(response));
        Assert.Equal([_site.Players["petya"]], CompletedBy(Assert.Single((await ListAsync(await _site.SignedInAsync("vasya"))).EnumerateArray())));
        await AssertConflictAsync(await MarkAsync(admin, challenge, "petya", run), "weeklyChallenge.alreadyCompleted");
    }

    [Fact]
    public async Task A_mark_with_another_players_run_or_of_an_automatic_challenge_is_a_conflict()
    {
        var admin = await _site.SignedInAsync("admin");
        var manual = await SetAsync(admin, "manual");
        var auto = await SetAsync(admin, "auto");
        var vasyasRun = await CompletedAsync("vasya");

        await AssertConflictAsync(await MarkAsync(admin, manual, "petya", vasyasRun), "run.notYours");
        await AssertConflictAsync(await MarkAsync(admin, auto, "vasya", vasyasRun), "weeklyChallenge.notManual");
        await AssertConflictAsync(await MarkAsync(admin, Guid.NewGuid(), "vasya", vasyasRun), "weeklyChallenge.unknown");
    }

    [Theory]
    [InlineData("""{"playerId":"{player}","runId":"{run}"}""")]
    [InlineData("""{"commandId":"{id}","runId":"{run}"}""")]
    [InlineData("""{"commandId":"{id}","playerId":"{player}"}""")]
    [InlineData("""{"commandId":"{id}","playerId":"vasya","runId":"{run}"}""")]
    public async Task A_mark_with_invalid_input_is_a_bad_request(string json)
    {
        var admin = await _site.SignedInAsync("admin");
        var challenge = await SetAsync(admin, "manual");
        var run = await CompletedAsync("vasya");

        var response = await PostJsonAsync(
            admin,
            AdminUrl($"weekly-challenges/{challenge}/mark"),
            json.Replace("{player}", _site.Players["vasya"].ToString(), StringComparison.Ordinal).Replace("{run}", run.ToString(), StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await StateAsync(_site)).WeeklyChallenges[challenge].Completions);
    }

    // ---- Roles ----

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    [InlineData("masha")]
    public async Task Players_and_spectators_cannot_set_or_mark_a_challenge(string login)
    {
        var admin = await _site.SignedInAsync("admin");
        var challenge = await SetAsync(admin, "manual");
        var run = await CompletedAsync("vasya");
        var client = await _site.SignedInAsync(login);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(AdminUrl("weekly-challenges"), new { commandId = Guid.NewGuid(), challenge = Challenge(), startsAt = _site.Clock.UtcNow }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await MarkAsync(client, challenge, "vasya", run)).StatusCode);
        Assert.Single((await StateAsync(_site)).WeeklyChallenges);
        Assert.Empty((await StateAsync(_site)).WeeklyChallenges[challenge].Completions);
    }

    [Fact]
    public async Task The_anonymous_get_401_and_an_unknown_season_is_not_found()
    {
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url("weekly-challenges"), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(AdminUrl("weekly-challenges"), new { commandId = Guid.NewGuid(), challenge = Challenge(), startsAt = _site.Clock.UtcNow }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await _site.SignedInAsync("vasya")).GetAsync($"/api/seasons/{Guid.NewGuid()}/weekly-challenges", Ct)).StatusCode);
    }

    // ---- SignalR ----

    [Fact]
    public async Task A_watcher_learns_about_a_set_challenge_and_a_completion()
    {
        var (connection, updates) = await WatchAsync(_site, "petya");
        await using var hub = connection;
        var admin = await _site.SignedInAsync("admin");

        await SetAsync(admin);
        await CompletedAsync("vasya");

        await WaitForAsync(updates, "weekly-challenge-set");
        await WaitForAsync(updates, "weekly-challenge-completed");
    }
}
