using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Undo over HTTP (C12a, L3, D-104): <c>POST /api/admin/seasons/{id}/undo</c> {commandId, targetCommandId, comment} and
/// the admin's list of commands <c>GET /api/admin/seasons/{id}/commands</c>. After every undo the projection equals the
/// replayed log (L4), the undone command's events are marked, and a refusal names the later commands in «related».
/// </summary>
public sealed class UndoApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    private static string AdminUrl(string action, Guid? seasonId = null) => $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Success ----

    [Fact]
    public async Task Admin_undoes_a_completion_and_the_projection_follows_the_log()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });
        var before = await MeAsync(vasya);
        var completion = Guid.NewGuid();
        await PostOkAsync(vasya, Url("complete"), new { commandId = completion, difficulty = "normal" });

        var response = await PostOkAsync(admin, AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = completion, comment = "Ошибка" });

        Assert.Equal(["command-undone"], await TypesAsync(response));
        var after = await MeAsync(vasya);
        Assert.Equal("playing", after.GetProperty("phase").GetString());
        Assert.Equal(before.GetProperty("activeRun").GetProperty("id").GetGuid(), after.GetProperty("activeRun").GetProperty("id").GetGuid());
        await AssertProjectionFollowsTheLogAsync();

        await using var db = _site.NewDb();
        Assert.All(await db.Events.Where(e => e.CommandId == completion).ToListAsync(Ct), e => Assert.NotNull(e.UndoneByEventId));
    }

    [Fact]
    public async Task Undoing_a_start_removes_the_run_from_the_projection()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        var start = Guid.NewGuid();
        await PostOkAsync(vasya, Url("start"), new { commandId = start });

        await PostOkAsync(admin, AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = start, comment = "Ошибка" });

        await using var db = _site.NewDb();
        Assert.Empty(await db.Runs.ToListAsync(Ct));
        await AssertProjectionFollowsTheLogAsync();
    }

    [Fact]
    public async Task Undoing_a_drop_takes_back_the_exclusion_and_the_effect()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });
        var drop = Guid.NewGuid();
        await PostOkAsync(vasya, Url("drop"), new { commandId = drop });

        await PostOkAsync(admin, AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = drop, comment = "Ошибка" });

        await using var db = _site.NewDb();
        Assert.Empty(await db.Exclusions.ToListAsync(Ct));
        Assert.Empty(await db.ManualEffects.ToListAsync(Ct));
        await AssertProjectionFollowsTheLogAsync();
    }

    [Fact]
    public async Task Undoing_a_proof_and_a_review_removes_their_rows()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(vasya, Url("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, Url("start"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, Url("complete"), new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var runId = (await MeAsync(vasya)).GetProperty("lastCompleted").GetProperty("id").GetGuid();
        var proof = Guid.NewGuid();
        await PostOkAsync(vasya, Url($"runs/{runId}/proof"), new { commandId = proof, links = new[] { "https://imgur.com/a/credits" } });
        var review = Guid.NewGuid();
        await PostOkAsync(vasya, Url($"runs/{runId}/review"), new { commandId = review, rating = 8 });

        await PostOkAsync(admin, AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = review, comment = "Ошибка" });
        await PostOkAsync(admin, AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = proof, comment = "Ошибка" });

        await using var db = _site.NewDb();
        Assert.Empty(await db.Proofs.ToListAsync(Ct));
        Assert.Empty(await db.Reviews.ToListAsync(Ct));
        await AssertProjectionFollowsTheLogAsync();
    }

    [Fact]
    public async Task The_command_list_shows_the_newest_first_and_what_was_undone()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var roll = Guid.NewGuid();
        await PostOkAsync(vasya, Url("roll"), new { commandId = roll });
        var undo = Guid.NewGuid();
        await PostOkAsync(admin, AdminUrl("undo"), new { commandId = undo, targetCommandId = roll, comment = "Ошибка" });

        using var doc = JsonDocument.Parse(await admin.GetStringAsync(AdminUrl("commands"), Ct));
        var list = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(undo, list[0].GetProperty("commandId").GetGuid());
        Assert.Equal("UndoCommand", list[0].GetProperty("commandType").GetString());
        Assert.Equal(roll, list[1].GetProperty("commandId").GetGuid());
        Assert.True(list[1].GetProperty("undone").GetBoolean());
        Assert.False(list[0].GetProperty("undone").GetBoolean());
        Assert.Equal(["game-rolled"], list[1].GetProperty("events").EnumerateArray().Select(e => e.GetString()));
        Assert.False(string.IsNullOrEmpty(list[1].GetProperty("authorName").GetString()));
    }

    // ---- Rule refusals: 409 ----

    [Fact]
    public async Task A_dependent_command_is_named_in_the_refusal()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var roll = Guid.NewGuid();
        await PostOkAsync(vasya, Url("roll"), new { commandId = roll });
        var start = Guid.NewGuid();
        await PostOkAsync(vasya, Url("start"), new { commandId = start });

        var response = await admin.PostAsJsonAsync(AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = roll, comment = "Ошибка" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("undo.dependents", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal([start], problem.RootElement.GetProperty("related").EnumerateArray().Select(e => e.GetGuid()));
    }

    [Fact]
    public async Task An_unknown_command_is_a_conflict()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = Guid.NewGuid(), comment = "Ошибка" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---- Refused by role and input ----

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_cannot_undo_or_list(string login)
    {
        var client = await _site.SignedInAsync(login);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = Guid.NewGuid(), comment = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(AdminUrl("commands"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_is_unauthorized()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = Guid.NewGuid(), comment = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(AdminUrl("commands"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Post_without_the_antiforgery_token_is_refused()
    {
        var admin = await _site.SignedInAsync("admin");
        admin.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);

        var response = await admin.PostAsJsonAsync(AdminUrl("undo"), new { commandId = Guid.NewGuid(), targetCommandId = Guid.NewGuid(), comment = "x" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_season_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync(AdminUrl("undo", Guid.NewGuid()), new { commandId = Guid.NewGuid(), targetCommandId = Guid.NewGuid(), comment = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(AdminUrl("commands", Guid.NewGuid()), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("{\"commandId\":\"{id}\",\"comment\":\"x\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"targetCommandId\":\"{id}\"}")]
    [InlineData("{\"commandId\":\"{id}\",\"targetCommandId\":\"{id}\",\"comment\":\"{long}\"}")]
    [InlineData("{\"targetCommandId\":\"{id}\",\"comment\":\"x\"}")]
    public async Task Bad_input_is_a_bad_request(string body)
    {
        var admin = await _site.SignedInAsync("admin");
        var json = body.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal)
            .Replace("{long}", new string('я', 501), StringComparison.Ordinal);

        var response = await admin.PostAsync(AdminUrl("undo"), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Helpers ----

    private async Task AssertProjectionFollowsTheLogAsync()
    {
        await using var db = _site.NewDb();
        var (replayed, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        Assert.Equal(replayed, await SeasonProjection.ReadAsync(db, replayed, Ct));
    }

    private static async Task<JsonElement> MeAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("me").Clone();
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
}
