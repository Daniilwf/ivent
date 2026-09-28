using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The admin corrects a completed run over HTTP (W7, W8, Q-5, D-14, D-97):
/// <c>POST /api/admin/seasons/{id}/runs/{runId}/hours</c> {commandId, hours, comment} — hours above 0 and at most 1000
/// (the same bound as an estimate), dice appended or taken off the end;
/// <c>POST /api/admin/seasons/{id}/runs/{runId}/difficulty</c> {commandId, difficulty, comment} — every die recalculated,
/// the difficulty's pending event resolved «не применимо». The comment is required and at most 500 characters: a missing
/// or too long one is a bad request, a blank one is the engine's <c>player.commentRequired</c>.
/// Each endpoint: success, anonymous 401, players and spectators 403, invalid input 400, engine rules 409 with the code,
/// unknown season 404, CSRF, one command id acts once. Responses are read as raw JSON.
/// </summary>
public sealed class AdminRunCorrectionApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PlayerUrl(string action, Guid? seasonId = null) => $"/api/seasons/{seasonId ?? SiteFactory.SeasonId}/{action}";

    private static string Url(string action, Guid runId, Guid? seasonId = null) =>
        $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/runs/{runId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Helpers ----

    private static async Task<HttpResponseMessage> PostOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return response;
    }

    /// <summary>Вася rolls, starts and completes a game through the player API; returns the completed run.</summary>
    private async Task<RunRecordView> VasyaCompletedAsync(string difficulty = "normal", Guid? seasonId = null)
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, PlayerUrl("roll", seasonId), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, PlayerUrl("start", seasonId), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, PlayerUrl("complete", seasonId), new { commandId = Guid.NewGuid(), difficulty });
        await using var db = _site.NewDb();
        var run = await db.Runs.SingleAsync(r => r.SeasonId == (seasonId ?? SiteFactory.SeasonId) && r.Status == RunStatus.Completed, Ct);
        return new RunRecordView(run.Id, run.Hours!.Value, run.DiceJson);
    }

    private sealed record RunRecordView(Guid Id, decimal Hours, string DiceJson);

    private static async Task<List<string>> TypesAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()!)];
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.EnumerateObject().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private async Task<int> EventCountAsync()
    {
        await using var db = _site.NewDb();
        return await db.Events.CountAsync(Ct);
    }

    /// <summary>A valid body for the action: 6 more hours, or hard.</summary>
    private static object ValidBody(string action, decimal hours) =>
        action == "hours"
            ? new { commandId = Guid.NewGuid(), hours = hours + 6, comment = "Часы по HLTB" }
            : new { commandId = Guid.NewGuid(), difficulty = "hard", comment = "По пруфу — сложная" };

    // ---- Hours: success ----

    [Fact]
    public async Task Admin_adds_hours_and_the_missing_dice_are_appended()
    {
        // Seeded pool: 12, 15 or 5 hours; 6 more hours are always 2 more dice (3 hours per die in the default rules)
        var run = await VasyaCompletedAsync();
        var admin = await _site.SignedInAsync("admin");

        var response = await PostOkAsync(admin, Url("hours", run.Id), new { commandId = Guid.NewGuid(), hours = run.Hours + 6, comment = "Часы по HLTB" });

        Assert.Equal(["run-hours-corrected", "points-changed", "player-moved", "coins-changed"], await TypesAsync(response));
        await using var db = _site.NewDb();
        var row = await db.Runs.SingleAsync(r => r.Id == run.Id, Ct);
        Assert.Equal(run.Hours + 6, row.Hours);
        using (var before = JsonDocument.Parse(run.DiceJson))
        using (var after = JsonDocument.Parse(row.DiceJson))
        {
            Assert.Equal(before.RootElement.GetArrayLength() + 2, after.RootElement.GetArrayLength());
        }

        var logged = await db.Events.SingleAsync(e => e.Type == "run-hours-corrected", Ct);
        Assert.Equal(_site.Users["admin"], logged.AuthorId);
        using var data = JsonDocument.Parse(logged.Data);
        Assert.Equal(run.Hours, Property(data.RootElement, "oldHours").GetDecimal());
        Assert.Equal(run.Hours + 6, Property(data.RootElement, "newHours").GetDecimal());
        Assert.Equal(2, Property(data.RootElement, "added").GetArrayLength());
        Assert.Equal(0, Property(data.RootElement, "removed").GetArrayLength());
        Assert.Equal("Часы по HLTB", Property(data.RootElement, "comment").GetString());
        Assert.Equal(_site.Clock.UtcNow, Property(data.RootElement, "correctedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Admin_takes_hours_away_and_the_extra_dice_are_removed_from_the_end()
    {
        var run = await VasyaCompletedAsync();
        var admin = await _site.SignedInAsync("admin");

        var response = await PostOkAsync(admin, Url("hours", run.Id), new { commandId = Guid.NewGuid(), hours = 1, comment = "Это демо" });

        Assert.Equal("run-hours-corrected", (await TypesAsync(response))[0]);
        await using var db = _site.NewDb();
        var row = await db.Runs.SingleAsync(r => r.Id == run.Id, Ct);
        Assert.Equal(1, row.Hours);
        using var before = JsonDocument.Parse(run.DiceJson);
        using var after = JsonDocument.Parse(row.DiceJson);
        Assert.Equal(1, after.RootElement.GetArrayLength()); // min 1 die
        Assert.Equal(before.RootElement[0].GetRawText(), after.RootElement[0].GetRawText());

        // The event keeps the values of the dice taken off the end, in their order
        var logged = await db.Events.SingleAsync(e => e.Type == "run-hours-corrected", Ct);
        using var data = JsonDocument.Parse(logged.Data);
        static (int, int) Face(JsonElement die) => (Property(die, "sides").GetInt32(), Property(die, "value").GetInt32());
        Assert.Equal(
            before.RootElement.EnumerateArray().Skip(1).Select(Face),
            Property(data.RootElement, "removed").EnumerateArray().Select(Face));
    }

    [Fact]
    public async Task Admin_corrects_hours_while_the_season_is_closing()
    {
        var run = await VasyaCompletedAsync();
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Closing));
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url("hours", run.Id), ValidBody("hours", run.Hours), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Hours_of_1000_are_accepted()
    {
        var run = await VasyaCompletedAsync();
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url("hours", run.Id), new { commandId = Guid.NewGuid(), hours = 1000, comment = "Гринд" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Difficulty: success ----

    [Fact]
    public async Task Admin_lowers_the_difficulty_and_the_pending_good_event_is_not_applicable()
    {
        // Given Вася completed on «выше сложной»: a good event waits
        var run = await VasyaCompletedAsync("extreme");
        await using (var db = _site.NewDb())
        {
            Assert.Equal(1, await db.ManualEffects.CountAsync(Ct));
        }

        var admin = await _site.SignedInAsync("admin");

        var response = await PostOkAsync(admin, Url("difficulty", run.Id), new { commandId = Guid.NewGuid(), difficulty = "normal", comment = "По пруфу — нормальная" });

        var types = await TypesAsync(response);
        Assert.Equal("run-difficulty-changed", types[0]);
        Assert.Equal("manual-effect-resolved", types[^1]);
        Assert.DoesNotContain("coins-changed", types);
        await using var after = _site.NewDb();
        Assert.Equal(0, await after.ManualEffects.CountAsync(Ct));
        var row = await after.Runs.SingleAsync(r => r.Id == run.Id, Ct);
        Assert.Equal(Difficulty.Normal, row.Difficulty);
        var logged = await after.Events.SingleAsync(e => e.Type == "run-difficulty-changed", Ct);
        Assert.Equal(_site.Users["admin"], logged.AuthorId);
        using var data = JsonDocument.Parse(logged.Data);
        using var dice = JsonDocument.Parse(row.DiceJson);
        Assert.Equal(Property(data.RootElement, "dice").GetArrayLength(), dice.RootElement.GetArrayLength());
        var resolved = await after.Events.SingleAsync(e => e.Type == "manual-effect-resolved", Ct);
        using var resolution = JsonDocument.Parse(resolved.Data);
        Assert.Equal("По пруфу — нормальная", Property(resolution.RootElement, "comment").GetString());
    }

    [Fact]
    public async Task Admin_raises_the_difficulty_to_extreme_and_a_good_event_is_created()
    {
        var run = await VasyaCompletedAsync("hard");
        var admin = await _site.SignedInAsync("admin");

        var response = await PostOkAsync(admin, Url("difficulty", run.Id), new { commandId = Guid.NewGuid(), difficulty = "extreme", comment = "Выше сложной по пруфу" });

        // hard and extreme are both d6 in the default rules: no points change, only the event
        Assert.Equal(["run-difficulty-changed", "manual-effect-created"], await TypesAsync(response));
        await using var db = _site.NewDb();
        var effect = await db.ManualEffects.SingleAsync(Ct);
        Assert.Equal((Engine.Effects.ManualEffectSource.Difficulty, (Guid?)run.Id), (effect.Source, effect.RunId));
    }

    // ---- Engine rules: 409 ----

    [Fact]
    public async Task Same_hours_are_a_conflict()
    {
        var run = await VasyaCompletedAsync();
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url("hours", run.Id), new { commandId = Guid.NewGuid(), hours = run.Hours, comment = "Та же" }, Ct);

        await AssertConflictAsync(response, "run.nothingToChange");
    }

    [Fact]
    public async Task Same_difficulty_is_a_conflict()
    {
        var run = await VasyaCompletedAsync("hard");
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url("difficulty", run.Id), new { commandId = Guid.NewGuid(), difficulty = "hard", comment = "Та же" }, Ct);

        await AssertConflictAsync(response, "run.nothingToChange");
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Run_being_played_is_a_conflict(string action)
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, PlayerUrl("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, PlayerUrl("start"), new { commandId = Guid.NewGuid() });
        Guid playing;
        await using (var db = _site.NewDb())
        {
            playing = (await db.Runs.SingleAsync(Ct)).Id;
        }

        var admin = await _site.SignedInAsync("admin");
        var before = await EventCountAsync();

        var response = await admin.PostAsJsonAsync(Url(action, playing), ValidBody(action, 12), Ct);

        await AssertConflictAsync(response, "run.notCompleted");
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Unknown_run_is_a_conflict(string action)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url(action, Guid.NewGuid()), ValidBody(action, 12), Ct);

        await AssertConflictAsync(response, "run.unknown");
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Blank_comment_is_a_conflict(string action)
    {
        var run = await VasyaCompletedAsync();
        var admin = await _site.SignedInAsync("admin");
        var before = await EventCountAsync();

        var response = await admin.PostAsJsonAsync(
            Url(action, run.Id),
            action == "hours"
                ? new { commandId = Guid.NewGuid(), hours = run.Hours + 6, comment = "   " }
                : (object)new { commandId = Guid.NewGuid(), difficulty = "hard", comment = "   " },
            Ct);

        await AssertConflictAsync(response, "player.commentRequired");
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Correcting_after_the_season_is_finished_is_a_conflict(string action)
    {
        // The season finishes only with every run checked (D-101); a correction of an approved run is allowed while the
        // season is open, so after the finish only the season being over stops it
        var run = await VasyaCompletedAsync();
        await _site.SendAsync(new ApproveProof(run.Id, null, "Видел на стриме"));
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Closing));
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Finished));
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url(action, run.Id), ValidBody(action, run.Hours), Ct);

        await AssertConflictAsync(response, "season.closed");
    }

    // ---- Idempotency ----

    [Theory]
    [InlineData("hours", "run-hours-corrected")]
    [InlineData("difficulty", "run-difficulty-changed")]
    public async Task Repeating_with_the_same_command_id_acts_once(string action, string type)
    {
        var run = await VasyaCompletedAsync();
        var admin = await _site.SignedInAsync("admin");
        var body = ValidBody(action, run.Hours);

        var first = await admin.PostAsJsonAsync(Url(action, run.Id), body, Ct);
        var second = await admin.PostAsJsonAsync(Url(action, run.Id), body, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var repeat = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
        Assert.True(repeat.RootElement.GetProperty("duplicate").GetBoolean());
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.Type == type, Ct));
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Same_command_id_with_another_body_is_a_conflict(string action)
    {
        // D-95: a repeat counts only with the same body; another body under a used id is refused and changes nothing
        var run = await VasyaCompletedAsync();
        var admin = await _site.SignedInAsync("admin");
        var commandId = Guid.NewGuid();
        object First() => action == "hours"
            ? new { commandId, hours = run.Hours + 6, comment = "Часы по HLTB" }
            : (object)new { commandId, difficulty = "hard", comment = "По пруфу — сложная" };
        object Other() => action == "hours"
            ? new { commandId, hours = run.Hours + 9, comment = "Часы по HLTB" }
            : (object)new { commandId, difficulty = "extreme", comment = "По пруфу — сложная" };

        await PostOkAsync(admin, Url(action, run.Id), First());
        var before = await EventCountAsync();
        var second = await admin.PostAsJsonAsync(Url(action, run.Id), Other(), Ct);

        await AssertConflictAsync(second, "command.idReused");
        Assert.Equal(before, await EventCountAsync());
    }

    // ---- Another season ----

    private async Task SendToAsync(Guid seasonId, ICommand command)
    {
        var outcome = await _site.Services.GetRequiredService<CommandBus>().SendAsync(
            new CommandEnvelope(Guid.NewGuid(), seasonId, command, AuthorId: null), Ct);
        Assert.True(outcome.IsAccepted, $"{command} rejected: {outcome.Rejection}");
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Run_of_another_season_is_a_conflict_and_leaves_that_season_alone(string action)
    {
        // Given Вася completed a run in season B
        var seasonB = await _site.CreateSeasonAsync();
        await SendToAsync(seasonB, new ChangeSeasonStatus(SeasonStatus.Active));
        await SendToAsync(seasonB, new AddSeasonPlayer(Guid.NewGuid(), _site.Users["vasya"], "vasya"));
        var runB = await VasyaCompletedAsync(seasonId: seasonB);
        var admin = await _site.SignedInAsync("admin");
        List<long> logB;
        await using (var db = _site.NewDb())
        {
            logB = await db.Events.Where(e => e.SeasonId == seasonB).Select(e => e.Id).ToListAsync(Ct);
        }

        // When the admin addresses it through season A
        var response = await admin.PostAsJsonAsync(Url(action, runB.Id), ValidBody(action, runB.Hours), Ct);

        await AssertConflictAsync(response, "run.unknown");
        await using var after = _site.NewDb();
        Assert.Equal(logB, await after.Events.Where(e => e.SeasonId == seasonB).Select(e => e.Id).ToListAsync(Ct));
    }

    // ---- Refused: no session, other roles, CSRF, unknown season ----

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Anonymous_is_unauthorized(string action)
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync(Url(action, Guid.NewGuid()), ValidBody(action, 12), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("vasya", "hours")]
    [InlineData("petya", "hours")]
    [InlineData("masha", "hours")]
    [InlineData("zritel", "hours")]
    [InlineData("vasya", "difficulty")]
    [InlineData("petya", "difficulty")]
    [InlineData("masha", "difficulty")]
    [InlineData("zritel", "difficulty")]
    public async Task Players_and_spectators_are_forbidden(string login, string action)
    {
        // Вася's run is completed, so only the role can stop the request — even his own run
        var run = await VasyaCompletedAsync();
        var client = await _site.SignedInAsync(login);
        var before = await EventCountAsync();

        var response = await client.PostAsJsonAsync(Url(action, run.Id), ValidBody(action, run.Hours), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Post_without_the_antiforgery_token_is_refused(string action)
    {
        var run = await VasyaCompletedAsync();
        var admin = await _site.SignedInAsync("admin");
        admin.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);
        var before = await EventCountAsync();

        var response = await admin.PostAsJsonAsync(Url(action, run.Id), ValidBody(action, run.Hours), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Unknown_season_is_not_found(string action)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url(action, Guid.NewGuid(), Guid.NewGuid()), ValidBody(action, 12), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Run_id_that_is_not_a_guid_is_not_found(string action)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(
            $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/not-a-guid/{action}", ValidBody(action, 12), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Invalid input: 400 ----

    [Theory]
    [InlineData("hours", """{"commandId":"00000000-0000-0000-0000-000000000000","hours":20,"comment":"Часы"}""")]
    [InlineData("hours", """{"hours":20,"comment":"Часы"}""")]
    [InlineData("hours", """{"commandId":"not-a-guid","hours":20,"comment":"Часы"}""")]
    [InlineData("hours", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000030","comment":"Часы"}""")]
    [InlineData("hours", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000031","hours":null,"comment":"Часы"}""")]
    [InlineData("hours", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000032","hours":0,"comment":"Часы"}""")]
    [InlineData("hours", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000033","hours":-3,"comment":"Часы"}""")]
    [InlineData("hours", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000034","hours":1000.5,"comment":"Часы"}""")]
    [InlineData("hours", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000035","hours":"много","comment":"Часы"}""")]
    [InlineData("hours", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000036","hours":20}""")]
    [InlineData("hours", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000037","hours":20,"comment":null}""")]
    [InlineData("hours", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000038","hours":20,"comment":42}""")]
    [InlineData("hours", "not json")]
    [InlineData("difficulty", """{"commandId":"00000000-0000-0000-0000-000000000000","difficulty":"hard","comment":"Пруф"}""")]
    [InlineData("difficulty", """{"difficulty":"hard","comment":"Пруф"}""")]
    [InlineData("difficulty", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000040","comment":"Пруф"}""")]
    [InlineData("difficulty", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000041","difficulty":null,"comment":"Пруф"}""")]
    [InlineData("difficulty", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000042","difficulty":"impossible","comment":"Пруф"}""")]
    [InlineData("difficulty", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000043","difficulty":7,"comment":"Пруф"}""")]
    [InlineData("difficulty", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000044","difficulty":"hard"}""")]
    [InlineData("difficulty", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000045","difficulty":"hard","comment":null}""")]
    [InlineData("difficulty", "not json")]
    public async Task Invalid_input_is_a_bad_request(string action, string body)
    {
        var run = await VasyaCompletedAsync();
        var admin = await _site.SignedInAsync("admin");
        var before = await EventCountAsync();

        var response = await admin.PostAsync(Url(action, run.Id), new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("difficulty")]
    public async Task Comment_over_500_characters_is_a_bad_request(string action)
    {
        var run = await VasyaCompletedAsync();
        var admin = await _site.SignedInAsync("admin");
        var before = await EventCountAsync();
        var comment = new string('я', Limits.MaxCommentLength + 1);

        var response = await admin.PostAsJsonAsync(
            Url(action, run.Id),
            action == "hours"
                ? new { commandId = Guid.NewGuid(), hours = run.Hours + 6, comment }
                : (object)new { commandId = Guid.NewGuid(), difficulty = "hard", comment },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }
}
