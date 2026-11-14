using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Api.PollSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Global events over HTTP (task 5.19; SPEC «Общие ивенты», D-517, D-518, D-750, D-755, D-832): the admin launches one with
/// a term and a goal and ends it early; everyone sees the list with the goal's progress (the sum of the contributions in
/// force) and the season view carries the banner of the running ones. Every endpoint: success, another role (403, 401),
/// invalid input (400), the engine's refusals as 409. Written by <c>test-author</c> before the endpoints.
/// </summary>
public sealed class GlobalEventApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await GlobalEventsOnAsync(_site);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private static object Launch(int durationHours = 24, object? goal = null) =>
        goal is null
            ? new { commandId = Guid.NewGuid(), objectId = CoinRain.Id, durationHours }
            : new { commandId = Guid.NewGuid(), objectId = CoinRain.Id, durationHours, goal };

    private async Task<Guid> LaunchedAsync(HttpClient admin, object? goal = null)
    {
        await ApiCalls.PostOkAsync(admin, AdminUrl("global-events"), Launch(goal: goal));
        return Assert.Single((await StateAsync(_site)).GlobalEvents.Values).GlobalEventId;
    }

    private static async Task<JsonElement> ListAsync(HttpClient client) => await ApiCalls.OkAsync(await client.GetAsync(Url("global-events"), Ct));

    private static async Task<JsonElement> BannerAsync(HttpClient client) =>
        (await ApiCalls.OkAsync(await client.GetAsync(Url(""), Ct))).GetProperty("globalEvents");

    /// <summary>The player rolls, starts and completes a game over HTTP: the run and the HowLongToBeat hours of its game.</summary>
    private async Task<(Guid RunId, decimal Hours)> CompletedAsync(string login)
    {
        var client = await _site.SignedInAsync(login);
        await ApiCalls.PostOkAsync(client, Url("roll"));
        await ApiCalls.PostOkAsync(client, Url("start"));
        await ApiCalls.PostOkAsync(client, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        await using var db = _site.NewDb();
        var run = await db.Runs.AsNoTracking().Where(r => r.PlayerId == _site.Players[login]).OrderByDescending(r => r.StartedAt).FirstAsync(Ct);
        return (run.Id, (await db.Games.AsNoTracking().SingleAsync(g => g.Id == run.GameId, Ct)).Hours!.Value);
    }

    // ---- Launch ----

    [Fact]
    public async Task The_admin_launches_an_event_with_a_term_and_everyone_sees_it_with_its_banner()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(AdminUrl("global-events"), Launch(goal: new { metric = "runs", target = 3 }), Ct);

        Assert.Contains("global-event-launched", await TypesAsync(response));
        var ev = Assert.Single((await StateAsync(_site)).GlobalEvents.Values);
        foreach (var login in new[] { "vasya", "zritel", "masha" })
        {
            var client = await _site.SignedInAsync(login);
            var item = Assert.Single((await ListAsync(client)).EnumerateArray());
            Assert.Equal(ev.GlobalEventId, item.GetProperty("id").GetGuid());
            Assert.Equal("Монетный дождь", item.GetProperty("name").GetString());
            Assert.Equal("Всем по 2 монетки.", item.GetProperty("description").GetString());
            Assert.Equal("active", item.GetProperty("status").GetString());
            Assert.Equal(ev.EndsAt, item.GetProperty("endsAt").GetDateTimeOffset());
            var goal = item.GetProperty("goal");
            Assert.Equal(("runs", 3, 0m, false), (goal.GetProperty("metric").GetString(), goal.GetProperty("target").GetInt32(), goal.GetProperty("progress").GetDecimal(), goal.GetProperty("reached").GetBoolean()));

            var banner = Assert.Single((await BannerAsync(client)).EnumerateArray());
            Assert.Equal(ev.GlobalEventId, banner.GetProperty("id").GetGuid());
            Assert.Equal("Монетный дождь", banner.GetProperty("name").GetString());
            Assert.Equal(0m, banner.GetProperty("goal").GetProperty("progress").GetDecimal());
        }
    }

    [Fact]
    public async Task An_event_without_a_goal_has_a_banner_and_no_progress_bar()
    {
        var admin = await _site.SignedInAsync("admin");
        await LaunchedAsync(admin);

        var banner = Assert.Single((await BannerAsync(await _site.SignedInAsync("vasya"))).EnumerateArray());

        Assert.Equal(JsonValueKind.Null, banner.GetProperty("goal").ValueKind);
    }

    [Fact]
    public async Task Without_global_events_the_banner_is_empty_and_the_list_and_launch_are_refused()
    {
        await RulesAsync(_site, Flags(f => f with { GlobalEvents = false }));
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(0, (await BannerAsync(vasya)).GetArrayLength());
        await AssertConflictAsync(await vasya.GetAsync(Url("global-events"), Ct), "feature.disabled");
        await AssertConflictAsync(await admin.PostAsJsonAsync(AdminUrl("global-events"), Launch(), Ct), "feature.disabled");
    }

    [Fact]
    public async Task An_unknown_object_or_an_object_that_is_not_an_event_is_a_conflict()
    {
        await _site.SendAsync(new Engine.Content.PublishContent(
            new Engine.Content.ContentPack
            {
                Objects =
                [
                    CoinRain,
                    new Engine.Content.ObjectDefinition
                    {
                        Id = "orange",
                        Kind = Engine.Content.ObjectKind.Item,
                        Name = "Апельсин",
                        Description = "Просто апельсин.",
                        Rarity = Engine.Content.Rarity.Common,
                        Window = Engine.Content.UseWindow.Anytime,
                        Manual = true,
                    },
                ],
            },
            "Контент"));
        var admin = await _site.SignedInAsync("admin");

        await AssertConflictAsync(
            await admin.PostAsJsonAsync(AdminUrl("global-events"), new { commandId = Guid.NewGuid(), objectId = "no-such", durationHours = 24 }, Ct), "content.unknownObject");
        await AssertConflictAsync(
            await admin.PostAsJsonAsync(AdminUrl("global-events"), new { commandId = Guid.NewGuid(), objectId = "orange", durationHours = 24 }, Ct), "globalEvent.notEvent");
    }

    [Fact]
    public async Task More_than_max_active_events_at_once_is_a_conflict()
    {
        var admin = await _site.SignedInAsync("admin");
        await ApiCalls.PostOkAsync(admin, AdminUrl("global-events"), Launch());
        await ApiCalls.PostOkAsync(admin, AdminUrl("global-events"), Launch());

        await AssertConflictAsync(await admin.PostAsJsonAsync(AdminUrl("global-events"), Launch(), Ct), "globalEvent.tooMany");
        Assert.Equal(2, (await BannerAsync(admin)).GetArrayLength());
    }

    [Theory]
    [InlineData("""{"objectId":"coin-rain","durationHours":24}""")]
    [InlineData("""{"commandId":"{id}","durationHours":24}""")]
    [InlineData("""{"commandId":"{id}","objectId":"","durationHours":24}""")]
    [InlineData("""{"commandId":"{id}","objectId":"coin-rain"}""")]
    [InlineData("""{"commandId":"{id}","objectId":"coin-rain","durationHours":0}""")]
    [InlineData("""{"commandId":"{id}","objectId":"coin-rain","durationHours":"сутки"}""")]
    [InlineData("""{"commandId":"{id}","objectId":"coin-rain","durationHours":24,"goal":{"metric":"kilometres","target":5}}""")]
    [InlineData("""{"commandId":"{id}","objectId":"coin-rain","durationHours":24,"goal":{"metric":"runs","target":0}}""")]
    [InlineData("""{"commandId":"{id}","objectId":"coin-rain","durationHours":24,"goal":{"metric":"runs"}}""")]
    public async Task A_launch_with_invalid_input_is_a_bad_request(string json)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await PostJsonAsync(admin, AdminUrl("global-events"), json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await StateAsync(_site)).GlobalEvents);
    }

    [Fact]
    public async Task A_goal_reward_the_engine_refuses_is_a_conflict_with_its_path()
    {
        var admin = await _site.SignedInAsync("admin");
        var goal = new
        {
            metric = "runs",
            target = 2,
            reward = new { target = new { selector = "leader" }, actions = new[] { new { type = "changeResource", resource = "coins", amount = 3 } } },
        };

        var response = await admin.PostAsJsonAsync(AdminUrl("global-events"), Launch(goal: goal), Ct);

        await AssertConflictAsync(response, "content.invalid");
        Assert.Empty((await StateAsync(_site)).GlobalEvents);
    }

    // ---- Progress ----

    [Fact]
    public async Task Progress_of_a_goal_of_runs_is_the_sum_of_the_completions_in_force()
    {
        var admin = await _site.SignedInAsync("admin");
        await LaunchedAsync(admin, new { metric = "runs", target = 2 });
        await CompletedAsync("vasya");

        var goal = Assert.Single((await ListAsync(await _site.SignedInAsync("petya"))).EnumerateArray()).GetProperty("goal");
        Assert.Equal((1m, false), (goal.GetProperty("progress").GetDecimal(), goal.GetProperty("reached").GetBoolean()));

        await CompletedAsync("petya");

        var reached = Assert.Single((await BannerAsync(await _site.SignedInAsync("zritel"))).EnumerateArray()).GetProperty("goal");
        Assert.Equal((2m, true), (reached.GetProperty("progress").GetDecimal(), reached.GetProperty("reached").GetBoolean()));
    }

    [Fact]
    public async Task Progress_of_an_hours_goal_is_the_hours_of_the_game_and_a_rejected_proof_takes_them_back()
    {
        var admin = await _site.SignedInAsync("admin");
        await LaunchedAsync(admin, new { metric = "hours", target = 100 });
        var (run, hours) = await CompletedAsync("vasya");

        var goal = Assert.Single((await ListAsync(admin)).EnumerateArray()).GetProperty("goal");
        Assert.Equal(hours, goal.GetProperty("progress").GetDecimal());

        await ApiCalls.PostOkAsync(admin, AdminUrl($"runs/{run}/reject"), new { commandId = Guid.NewGuid(), comment = "Не то" });

        Assert.Equal(0m, Assert.Single((await ListAsync(admin)).EnumerateArray()).GetProperty("goal").GetProperty("progress").GetDecimal());
    }

    // ---- End early ----

    [Fact]
    public async Task The_admin_ends_an_event_early_and_its_banner_goes()
    {
        var admin = await _site.SignedInAsync("admin");
        var id = await LaunchedAsync(admin, new { metric = "runs", target = 3 });

        var response = await admin.PostAsJsonAsync(AdminUrl($"global-events/{id}/end"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(["global-event-ended"], await TypesAsync(response));
        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal(0, (await BannerAsync(vasya)).GetArrayLength());
        Assert.Equal("ended", Assert.Single((await ListAsync(vasya)).EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ending_an_ended_or_unknown_event_is_a_conflict_and_without_a_command_id_a_bad_request()
    {
        var admin = await _site.SignedInAsync("admin");
        var id = await LaunchedAsync(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostJsonAsync(admin, AdminUrl($"global-events/{id}/end"), "{}")).StatusCode);
        await ApiCalls.PostOkAsync(admin, AdminUrl($"global-events/{id}/end"));

        await AssertConflictAsync(await admin.PostAsJsonAsync(AdminUrl($"global-events/{id}/end"), new { commandId = Guid.NewGuid() }, Ct), "globalEvent.ended");
        await AssertConflictAsync(await admin.PostAsJsonAsync(AdminUrl($"global-events/{Guid.NewGuid()}/end"), new { commandId = Guid.NewGuid() }, Ct), "globalEvent.unknown");
    }

    // ---- Roles ----

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    [InlineData("masha")]
    public async Task Players_and_spectators_cannot_launch_or_end_an_event(string login)
    {
        var admin = await _site.SignedInAsync("admin");
        var id = await LaunchedAsync(admin);
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(AdminUrl("global-events"), Launch(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(AdminUrl($"global-events/{id}/end"), new { commandId = Guid.NewGuid() }, Ct)).StatusCode);
        Assert.Equal(Engine.GlobalEvents.GlobalEventStatus.Active, Assert.Single((await StateAsync(_site)).GlobalEvents.Values).Status);
    }

    [Fact]
    public async Task The_anonymous_get_401_and_an_unknown_season_is_not_found()
    {
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url("global-events"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(AdminUrl("global-events"), Launch(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await _site.SignedInAsync("vasya")).GetAsync($"/api/seasons/{Guid.NewGuid()}/global-events", Ct)).StatusCode);
    }

    // ---- SignalR ----

    [Fact]
    public async Task A_watcher_learns_about_the_launch_and_the_progress()
    {
        var (connection, updates) = await WatchAsync(_site, "petya");
        await using var hub = connection;
        var admin = await _site.SignedInAsync("admin");

        await LaunchedAsync(admin, new { metric = "runs", target = 3 });
        await CompletedAsync("vasya");

        await WaitForAsync(updates, "global-event-launched");
        await WaitForAsync(updates, "global-progress-changed");
    }
}
