using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Ranking;
using GameEvent.Infrastructure.EventLog;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The season lifecycle over HTTP (C10, SE1–SE4, D-101):
/// <c>POST /api/admin/seasons/{id}/status</c> {commandId, to} — the next status (<c>"closing"</c>, <c>"finished"</c>,
/// <c>"archived"</c>…); <c>POST /api/admin/seasons/{id}/deadline</c> {commandId, deadline} — set or remove (null) the
/// deadline. Admin only (players and spectators 403, anonymous 401), antiforgery as every admin post, an unknown status or
/// a missing command id is 400, an unknown season 404, rule refusals 409 with the engine code. The season view shows
/// <c>status</c> and <c>deadline</c>; after the finish its <c>leaderboard</c> is the recorded result. Responses are read as
/// raw JSON, so the tests compile before the DTOs exist.
/// </summary>
public sealed class SeasonLifecycleApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AdminUrl(string action, Guid? seasonId = null) => $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/{action}";

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private static object ValidBody(string action) =>
        action == "status"
            ? new { commandId = Guid.NewGuid(), to = "closing" }
            : new { commandId = Guid.NewGuid(), deadline = new DateTimeOffset(2026, 12, 31, 21, 0, 0, TimeSpan.Zero) };

    // ---- Success ----

    [Fact]
    public async Task Admin_closes_the_season_and_the_view_shows_the_status()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal("active", (await SeasonJsonAsync(vasya)).GetProperty("status").GetString());

        var response = await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "closing" });

        Assert.Equal(["season-status-changed"], await TypesAsync(response));
        Assert.Equal("closing", (await SeasonJsonAsync(vasya)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Admin_sets_and_removes_the_deadline_and_the_view_shows_it()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        var deadline = new DateTimeOffset(2026, 12, 31, 21, 0, 0, TimeSpan.Zero);

        var response = await PostOkAsync(admin, AdminUrl("deadline"), new { commandId = Guid.NewGuid(), deadline });

        Assert.Equal(["season-deadline-set"], await TypesAsync(response));
        Assert.Equal(deadline, (await SeasonJsonAsync(vasya)).GetProperty("deadline").GetDateTimeOffset());

        await PostOkAsync(admin, AdminUrl("deadline"), new { commandId = Guid.NewGuid(), deadline = (DateTimeOffset?)null });
        Assert.Equal(JsonValueKind.Null, (await SeasonJsonAsync(vasya)).GetProperty("deadline").ValueKind);
    }

    [Fact]
    public async Task Same_command_id_twice_acts_once()
    {
        var admin = await _site.SignedInAsync("admin");
        var commandId = Guid.NewGuid();

        await PostOkAsync(admin, AdminUrl("status"), new { commandId, to = "closing" });
        await PostOkAsync(admin, AdminUrl("status"), new { commandId, to = "closing" });

        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.Type == "season-status-changed" && e.Data.Contains("\"to\":\"closing\""), Ct));
    }

    [Fact]
    public async Task Finished_season_view_shows_the_recorded_result()
    {
        // Вася completes a run; the admin closes, approves it and finishes: the leaderboard is the result snapshot
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var runId = await CompleteAsync(vasya);
        await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "closing" });
        await PostOkAsync(admin, AdminUrl($"runs/{runId}/approve"), new { commandId = Guid.NewGuid(), comment = "Видел на стриме" });

        var response = await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "finished" });

        Assert.Equal(["season-status-changed", "season-result-recorded"], await TypesAsync(response));
        var season = await SeasonJsonAsync(vasya);
        Assert.Equal("finished", season.GetProperty("status").GetString());
        await using var db = _site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        Assert.NotNull(state.Result);
        Assert.Equal([.. state.Result!.Value], Rows(season));
    }

    // ---- Rule refusals: 409 ----

    [Fact]
    public async Task Finish_with_a_run_waiting_for_its_proof_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await CompleteAsync(vasya);
        await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "closing" });

        var response = await admin.PostAsJsonAsync(AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "finished" }, Ct);

        await AssertConflictAsync(response, "season.proofsPending");
    }

    [Fact]
    public async Task Deadline_in_closing_is_a_conflict()
    {
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "closing" });

        var response = await admin.PostAsJsonAsync(AdminUrl("deadline"), ValidBody("deadline"), Ct);

        await AssertConflictAsync(response, "season.closed");
    }

    [Fact]
    public async Task Skipping_a_status_is_a_conflict()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "archived" }, Ct);

        await AssertConflictAsync(response, "season.invalidTransition");
    }

    [Fact]
    public async Task Roll_past_the_deadline_is_a_conflict_before_the_scheduler_closes()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(admin, AdminUrl("deadline"), new { commandId = Guid.NewGuid(), deadline = _site.Clock.UtcNow.AddMinutes(5) });
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(10);

        var response = await vasya.PostAsJsonAsync(Url("roll"), new { commandId = Guid.NewGuid() }, Ct);

        await AssertConflictAsync(response, "season.deadlinePassed");
    }

    // ---- Refused: roles, session, CSRF, season ----

    [Theory]
    [InlineData("vasya", "status")]
    [InlineData("masha", "status")]
    [InlineData("zritel", "status")]
    [InlineData("vasya", "deadline")]
    [InlineData("masha", "deadline")]
    [InlineData("zritel", "deadline")]
    public async Task Players_and_spectators_are_forbidden(string login, string action)
    {
        var client = await _site.SignedInAsync(login);
        var before = await EventCountAsync();

        var response = await client.PostAsJsonAsync(AdminUrl(action), ValidBody(action), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("deadline")]
    public async Task Anonymous_is_unauthorized(string action)
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync(AdminUrl(action), ValidBody(action), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("deadline")]
    public async Task Post_without_the_antiforgery_token_is_refused(string action)
    {
        var admin = await _site.SignedInAsync("admin");
        admin.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);
        var before = await EventCountAsync();

        var response = await admin.PostAsJsonAsync(AdminUrl(action), ValidBody(action), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("deadline")]
    public async Task Unknown_season_is_not_found(string action)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(AdminUrl(action, Guid.NewGuid()), ValidBody(action), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Invalid input: 400 ----

    [Fact]
    public async Task Unknown_status_is_a_bad_request()
    {
        var admin = await _site.SignedInAsync("admin");
        var before = await EventCountAsync();

        var response = await admin.PostAsJsonAsync(AdminUrl("status"), new { commandId = Guid.NewGuid(), to = "sleeping" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("deadline")]
    public async Task Missing_command_id_is_a_bad_request(string action)
    {
        var admin = await _site.SignedInAsync("admin");
        var before = await EventCountAsync();
        object body = action == "status" ? new { to = "closing" } : new { deadline = DateTimeOffset.UtcNow };

        var response = await admin.PostAsJsonAsync(AdminUrl(action), body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Fact]
    public async Task Deadline_that_is_not_a_date_is_a_bad_request()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(AdminUrl("deadline"), new { commandId = Guid.NewGuid(), deadline = "завтра" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Helpers ----

    private async Task<Guid> CompleteAsync(HttpClient client)
    {
        await PostOkAsync(client, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("start"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        return Guid.Parse((await SeasonJsonAsync(client)).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetString()!);
    }

    private static List<LeaderboardRow> Rows(JsonElement season) =>
        [.. season.GetProperty("leaderboard").EnumerateArray().Select(r => new LeaderboardRow(
            r.GetProperty("playerId").GetGuid(),
            r.GetProperty("place").GetInt32(),
            r.GetProperty("points").GetInt32(),
            r.GetProperty("cellsToFinish").ValueKind == JsonValueKind.Null ? null : r.GetProperty("cellsToFinish").GetInt32(),
            r.GetProperty("isFirst").GetBoolean(),
            r.GetProperty("provisional").GetBoolean()))];

    private async Task<int> EventCountAsync()
    {
        await using var db = _site.NewDb();
        return await db.Events.CountAsync(Ct);
    }

    private static async Task<JsonElement> SeasonJsonAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.Clone();
    }

    private static async Task<List<string>> TypesAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()!)];
    }

    private static async Task<HttpResponseMessage> PostOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return response;
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }
}
