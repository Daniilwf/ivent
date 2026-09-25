using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Test endpoints (SPEC «Тестовые эндпоинты», E4, A7, D-120): the admin moves the clock, seeds the randomness and loads a
/// scenario in Development and Test; in Production the routes do not exist.
/// </summary>
public sealed class TestEndpointsTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Scenario(string name) => $"/api/test/seasons/{SiteFactory.SeasonId}/scenarios/{name}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_admin_moves_the_clock_forward_to_a_moment_and_back()
    {
        var admin = await _site.SignedInAsync("admin");
        var start = _site.Clock.UtcNow;

        var advanced = await OkAsync(await admin.PostAsJsonAsync("/api/test/clock", new { advanceMinutes = 90 }, Ct));
        Assert.Equal(start.AddMinutes(90), advanced.GetProperty("now").GetDateTimeOffset());
        Assert.Equal(start.AddMinutes(90), _site.Clock.UtcNow);

        var moment = new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero);
        await OkAsync(await admin.PostAsJsonAsync("/api/test/clock", new { moveTo = moment }, Ct));
        Assert.Equal(moment, _site.Clock.UtcNow);

        await OkAsync(await admin.PostAsJsonAsync("/api/test/clock", new { reset = true }, Ct));
        Assert.Equal(start, _site.Clock.UtcNow);
        Assert.True((await OkAsync(await admin.GetAsync("/api/test/clock", Ct))).GetProperty("adjustable").GetBoolean());
    }

    [Fact]
    public async Task A_seed_makes_the_rolls_repeat()
    {
        // Eight rolls (each undone) after a seed, then the same seed again: the same eight games; one roll could match by chance
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");

        var first = await RollsAfterSeedAsync(admin, vasya, 42, 8);
        var again = await RollsAfterSeedAsync(admin, vasya, 42, 8);
        var other = await RollsAfterSeedAsync(admin, vasya, 7, 8);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"advanceMinutes":1e15}""")]
    [InlineData("""{"advanceMinutes":-6000000}""")]
    [InlineData("""{"moveTo":"9999-12-31T00:00:00Z"}""")]
    [InlineData("""{"moveTo":"1900-01-01T00:00:00Z"}""")]
    public async Task A_clock_move_out_of_bounds_is_invalid_and_changes_nothing(string body)
    {
        var admin = await _site.SignedInAsync("admin");
        var before = _site.Clock.UtcNow;

        var response = await admin.PostAsync("/api/test/clock", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, _site.Clock.UtcNow);
    }

    [Fact]
    public async Task A_clock_request_without_a_body_is_invalid()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsync("/api/test/clock", new StringContent("", System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Finish_soon_puts_the_player_one_cell_before_the_finish()
    {
        var admin = await _site.SignedInAsync("admin");

        var view = await OkAsync(await admin.PostAsJsonAsync(Scenario("finish-soon"), new { player = "vasya" }, Ct));

        Assert.Equal(["AdjustPlayer"], view.GetProperty("commands").EnumerateArray().Select(c => c.GetString()));
        var season = await SeasonAsync(await _site.SignedInAsync("vasya"));
        var cells = season.GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        var vasya = season.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == _site.Players["vasya"]);
        Assert.Equal(cells[^2], vasya.GetProperty("cellId").GetString());
    }

    [Fact]
    public async Task Deadline_in_an_hour_sets_the_deadline_an_hour_from_now()
    {
        var admin = await _site.SignedInAsync("admin");

        await OkAsync(await admin.PostAsJsonAsync(Scenario("deadline-in-hour"), new { }, Ct));

        var season = await SeasonAsync(admin);
        Assert.Equal(_site.Clock.UtcNow.AddHours(1), season.GetProperty("deadline").GetDateTimeOffset());
    }

    [Fact]
    public async Task Five_manual_effects_leave_five_good_events_to_play()
    {
        var admin = await _site.SignedInAsync("admin");

        // The seed pool has three games and a completed game leaves it: five completions need a few more
        foreach (var title in new[] { "Dead Space", "Amnesia", "Soma" })
        {
            Assert.True((await admin.PostAsJsonAsync("/api/pool", new { commandId = Guid.NewGuid(), title, tags = new[] { "Horror" }, hours = 2, force = true }, Ct)).IsSuccessStatusCode);
        }

        var view = await OkAsync(await admin.PostAsJsonAsync(Scenario("five-manual-effects"), new { player = "vasya" }, Ct));

        Assert.Equal(5, view.GetProperty("commands").EnumerateArray().Count(c => c.GetString() == "CompleteRun"));
        var me = (await SeasonAsync(await _site.SignedInAsync("vasya"))).GetProperty("me");
        Assert.Equal(5, me.GetProperty("manualEffects").GetArrayLength());
    }

    [Fact]
    public async Task Unknown_scenarios_seasons_and_players_are_refused()
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(Scenario("nope"), new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/test/seasons/{Guid.NewGuid()}/scenarios/finish-soon", new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(Scenario("finish-soon"), new { player = "nobody" }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Only_the_admin_uses_the_test_endpoints(string login)
    {
        var client = await _site.SignedInAsync(login);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/test/clock", new { advanceMinutes = 60 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Scenario("finish-soon"), new { }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_does_not_use_the_test_endpoints()
    {
        var anonymous = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/test/clock", Ct)).StatusCode);
    }

    [Fact]
    public async Task In_production_the_test_routes_do_not_exist()
    {
        await using var production = new SiteFactory(loginAttemptsPerMinute: 1000, environment: "Production");
        await production.SeedAsync();
        var client = production.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/test/clock", Ct)).StatusCode);
        foreach (var url in new[] { "/api/test/clock", "/api/test/random", Scenario("finish-soon") })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(url, new { }, Ct)).StatusCode);
        }
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Development", true)]
    [InlineData("Test", true)]
    public void Only_development_and_test_get_the_movable_clock_and_the_seedable_randomness(string environment, bool registered)
    {
        // The site as it is built, without the tests' own clock: production keeps the real time and the real randomness
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });

        GameEvent.Web.Testing.TestEndpoints.AddTestSupport(builder);

        Assert.Equal(registered, builder.Services.Any(s => s.ImplementationType == typeof(GameEvent.Infrastructure.Kernel.ShiftableClock)));
        Assert.Equal(registered, builder.Services.Any(s => s.ImplementationType == typeof(GameEvent.Infrastructure.Kernel.ReseedableRandom)));
    }

    // ---- Helpers ----

    private static async Task<List<string?>> RollsAfterSeedAsync(HttpClient admin, HttpClient player, int seed, int count)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/test/random", new { seed }, Ct)).StatusCode);
        var titles = new List<string?>();
        for (var i = 0; i < count; i++)
        {
            var rollId = Guid.NewGuid();
            Assert.True((await player.PostAsJsonAsync($"/api/seasons/{SiteFactory.SeasonId}/roll", new { commandId = rollId }, Ct)).IsSuccessStatusCode);
            titles.Add((await SeasonAsync(player)).GetProperty("me").GetProperty("offer").GetProperty("title").GetString());
            var undo = await admin.PostAsJsonAsync(
                $"/api/admin/seasons/{SiteFactory.SeasonId}/undo", new { commandId = Guid.NewGuid(), targetCommandId = rollId, comment = "повтор с тем же зерном" }, Ct);
            Assert.True(undo.IsSuccessStatusCode, await undo.Content.ReadAsStringAsync(Ct));
        }

        return titles;
    }

    private static async Task<JsonElement> SeasonAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
