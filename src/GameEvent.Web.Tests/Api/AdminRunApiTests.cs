using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The admin's side of tech rerolls (RR5, RR6, D-11, D-94) over HTTP:
/// <c>POST /api/admin/seasons/{id}/players/{playerId}/tech-reroll</c> {commandId, reason, comment} — a tech reroll on the
/// player's behalf at any time, marked <c>byAdmin</c> in the log;
/// <c>POST /api/admin/seasons/{id}/runs/{runId}/convert-to-drop</c> {commandId, comment} — the drop penalty for a tech reroll.
/// Each endpoint: success, anonymous 401, other roles 403, invalid input 400, engine rules 409 with the code, unknown
/// season 404, CSRF, one command id acts once. Responses are read as raw JSON.
/// </summary>
public sealed class AdminRunApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PlayerUrl(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    private string TechRerollUrl(string player = "vasya", Guid? seasonId = null) =>
        $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/players/{_site.Players[player]}/tech-reroll";

    private static string ConvertUrl(Guid runId, Guid? seasonId = null) =>
        $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/runs/{runId}/convert-to-drop";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Helpers ----

    private static async Task<HttpResponseMessage> PostOkAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return response;
    }

    /// <summary>Вася rolls and starts a game through the player API; returns his client.</summary>
    private async Task<HttpClient> VasyaPlayingAsync()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, PlayerUrl("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, PlayerUrl("start"), new { commandId = Guid.NewGuid() });
        return vasya;
    }

    /// <summary>Вася tech-rerolls his game himself (within the window); returns the tech-rerolled run id.</summary>
    private async Task<Guid> VasyaTechRerolledAsync()
    {
        var vasya = await VasyaPlayingAsync();
        await PostOkAsync(vasya, PlayerUrl("tech-reroll"), new { commandId = Guid.NewGuid(), reason = "doesNotLaunch" });
        await using var db = _site.NewDb();
        return (await db.Runs.SingleAsync(r => r.Status == RunStatus.TechRerolled, Ct)).Id;
    }

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

    // ---- Admin tech reroll: success ----

    [Fact]
    public async Task Admin_tech_rerolls_for_the_player_after_the_window_and_the_log_marks_it()
    {
        // Given Вася plays a game rolled 100 hours ago: his own tech reroll is refused
        var vasya = await VasyaPlayingAsync();
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(100);
        var own = await vasya.PostAsJsonAsync(PlayerUrl("tech-reroll"), new { commandId = Guid.NewGuid(), reason = "paidUnavailable" }, Ct);
        await AssertConflictAsync(own, "run.techRerollWindowClosed");

        // When the admin does it on his behalf
        var admin = await _site.SignedInAsync("admin");
        var response = await PostOkAsync(admin, TechRerollUrl(), new { commandId = Guid.NewGuid(), reason = "paidUnavailable", comment = "Игру убрали из Steam" });

        // Then the same events as the player's tech reroll, the log says it was the admin
        Assert.Equal(["run-tech-rerolled", "game-excluded", "game-rolled"], await TypesAsync(response));
        await using var db = _site.NewDb();
        var logged = await db.Events.SingleAsync(e => e.Type == "run-tech-rerolled", Ct);
        using var data = JsonDocument.Parse(logged.Data);
        Assert.True(Property(data.RootElement, "byAdmin").GetBoolean());
        Assert.Equal("Игру убрали из Steam", Property(data.RootElement, "comment").GetString());
        Assert.Equal(_site.Users["admin"], logged.AuthorId);
        var exclusion = await db.Exclusions.SingleAsync(Ct);
        Assert.Equal((_site.Players["vasya"], ExclusionReason.TechRerolled), (exclusion.PlayerId, exclusion.Reason));
        Assert.Equal(TurnPhase.Rolling, (await db.SeasonPlayers.SingleAsync(p => p.Id == _site.Players["vasya"], Ct)).Phase);
    }

    [Fact]
    public async Task Admin_tech_reroll_of_a_player_who_is_not_playing_is_a_conflict()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(TechRerollUrl(), new { commandId = Guid.NewGuid(), reason = "weakPc" }, Ct);

        await AssertConflictAsync(response, "turn.wrongPhase");
    }

    [Fact]
    public async Task Admin_tech_reroll_of_a_player_outside_the_season_is_a_conflict()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(
            $"/api/admin/seasons/{SiteFactory.SeasonId}/players/{Guid.NewGuid()}/tech-reroll",
            new { commandId = Guid.NewGuid(), reason = "weakPc" },
            Ct);

        await AssertConflictAsync(response, "player.unknown");
    }

    [Fact]
    public async Task Admin_tech_reroll_with_reason_other_and_no_comment_is_a_conflict()
    {
        await VasyaPlayingAsync();
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(TechRerollUrl(), new { commandId = Guid.NewGuid(), reason = "other", comment = " " }, Ct);

        await AssertConflictAsync(response, "run.reasonCommentRequired");
    }

    [Fact]
    public async Task Repeating_an_admin_tech_reroll_with_the_same_command_id_acts_once()
    {
        await VasyaPlayingAsync();
        var admin = await _site.SignedInAsync("admin");
        var body = new { commandId = Guid.NewGuid(), reason = "weakPc" };

        var first = await admin.PostAsJsonAsync(TechRerollUrl(), body, Ct);
        var second = await admin.PostAsJsonAsync(TechRerollUrl(), body, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var repeat = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
        Assert.True(repeat.RootElement.GetProperty("duplicate").GetBoolean());
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.Type == "run-tech-rerolled", Ct));
    }

    // ---- Convert to drop: success ----

    [Fact]
    public async Task Admin_converts_a_tech_reroll_into_a_drop_with_the_penalty()
    {
        var runId = await VasyaTechRerolledAsync();
        var admin = await _site.SignedInAsync("admin");

        var response = await PostOkAsync(admin, ConvertUrl(runId), new { commandId = Guid.NewGuid(), comment = "Игра запускалась" });

        // Вася is on the start: points go down, there is nowhere back to go; the bad event is created
        Assert.Equal(["tech-reroll-converted-to-drop", "points-changed", "manual-effect-created"], await TypesAsync(response));
        await using var db = _site.NewDb();
        Assert.Equal(RunStatus.Dropped, (await db.Runs.SingleAsync(r => r.Id == runId, Ct)).Status);
        Assert.Equal(ExclusionReason.Dropped, (await db.Exclusions.SingleAsync(Ct)).Reason);
        var vasya = await db.SeasonPlayers.SingleAsync(p => p.Id == _site.Players["vasya"], Ct);
        Assert.True(vasya.Points < 0);
        Assert.Equal(TurnPhase.Rolling, vasya.Phase); // his current turn is untouched
        var effect = await db.ManualEffects.SingleAsync(Ct);
        Assert.Equal((Engine.Effects.ManualEffectSource.Drop, (Guid?)runId), (effect.Source, effect.RunId));
        var logged = await db.Events.SingleAsync(e => e.Type == "tech-reroll-converted-to-drop", Ct);
        Assert.Equal(_site.Users["admin"], logged.AuthorId);
    }

    [Fact]
    public async Task Admin_converts_while_the_season_is_closing()
    {
        var runId = await VasyaTechRerolledAsync();
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Closing));
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(ConvertUrl(runId), new { commandId = Guid.NewGuid(), comment = "Проверка пруфов" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Converting_after_the_season_is_finished_is_a_conflict()
    {
        var runId = await VasyaTechRerolledAsync();
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Closing));
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Finished));
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(ConvertUrl(runId), new { commandId = Guid.NewGuid(), comment = "Поздно" }, Ct);

        await AssertConflictAsync(response, "season.closed");
    }

    [Fact]
    public async Task Converting_a_run_that_was_not_tech_rerolled_is_a_conflict()
    {
        await VasyaPlayingAsync();
        await using (var db = _site.NewDb())
        {
            var playing = (await db.Runs.SingleAsync(Ct)).Id;
            var admin = await _site.SignedInAsync("admin");

            var response = await admin.PostAsJsonAsync(ConvertUrl(playing), new { commandId = Guid.NewGuid(), comment = "Дроп" }, Ct);

            await AssertConflictAsync(response, "run.notTechRerolled");
        }
    }

    [Fact]
    public async Task Converting_an_unknown_run_is_a_conflict()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(ConvertUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), comment = "Дроп" }, Ct);

        await AssertConflictAsync(response, "run.unknown");
    }

    [Fact]
    public async Task Converting_with_a_blank_comment_is_a_conflict()
    {
        var runId = await VasyaTechRerolledAsync();
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(ConvertUrl(runId), new { commandId = Guid.NewGuid(), comment = "   " }, Ct);

        await AssertConflictAsync(response, "player.commentRequired");
    }

    [Fact]
    public async Task Repeating_a_conversion_with_the_same_command_id_acts_once()
    {
        var runId = await VasyaTechRerolledAsync();
        var admin = await _site.SignedInAsync("admin");
        var body = new { commandId = Guid.NewGuid(), comment = "Дроп" };

        var first = await admin.PostAsJsonAsync(ConvertUrl(runId), body, Ct);
        var second = await admin.PostAsJsonAsync(ConvertUrl(runId), body, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var repeat = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
        Assert.True(repeat.RootElement.GetProperty("duplicate").GetBoolean());
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.Type == "tech-reroll-converted-to-drop", Ct));
        Assert.Equal(1, await db.ManualEffects.CountAsync(Ct));
    }

    // ---- Refused: no session, other roles ----

    [Theory]
    [InlineData("tech-reroll")]
    [InlineData("convert-to-drop")]
    public async Task Anonymous_is_unauthorized(string action)
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync(UrlFor(action), new { commandId = Guid.NewGuid(), reason = "weakPc", comment = "x" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("vasya", "tech-reroll")]
    [InlineData("petya", "tech-reroll")]
    [InlineData("masha", "tech-reroll")]
    [InlineData("zritel", "tech-reroll")]
    [InlineData("vasya", "convert-to-drop")]
    [InlineData("petya", "convert-to-drop")]
    [InlineData("masha", "convert-to-drop")]
    [InlineData("zritel", "convert-to-drop")]
    public async Task Players_and_spectators_are_forbidden(string login, string action)
    {
        // Вася is playing and has a tech-rerolled run, so only the role can stop the request
        var runId = await VasyaTechRerolledAsync();
        var client = await _site.SignedInAsync(login);
        var before = await EventCountAsync();

        var response = await client.PostAsJsonAsync(
            action == "tech-reroll" ? TechRerollUrl() : ConvertUrl(runId),
            new { commandId = Guid.NewGuid(), reason = "weakPc", comment = "x" },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("tech-reroll")]
    [InlineData("convert-to-drop")]
    public async Task Post_without_the_antiforgery_token_is_refused(string action)
    {
        var admin = await _site.SignedInAsync("admin");
        admin.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);

        var response = await admin.PostAsJsonAsync(UrlFor(action), new { commandId = Guid.NewGuid(), reason = "weakPc", comment = "x" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("tech-reroll")]
    [InlineData("convert-to-drop")]
    public async Task Unknown_season_is_not_found(string action)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(UrlFor(action, Guid.NewGuid()), new { commandId = Guid.NewGuid(), reason = "weakPc", comment = "x" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Invalid input ----

    [Theory]
    [InlineData("tech-reroll", """{"commandId":"00000000-0000-0000-0000-000000000000","reason":"weakPc"}""")]
    [InlineData("tech-reroll", """{"reason":"weakPc"}""")]
    [InlineData("tech-reroll", """{"commandId":"not-a-guid","reason":"weakPc"}""")]
    [InlineData("tech-reroll", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000020"}""")]
    [InlineData("tech-reroll", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000021","reason":"slowInternet"}""")]
    [InlineData("tech-reroll", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000022","reason":null}""")]
    [InlineData("tech-reroll", "not json")]
    [InlineData("convert-to-drop", """{"commandId":"00000000-0000-0000-0000-000000000000","comment":"Дроп"}""")]
    [InlineData("convert-to-drop", """{"comment":"Дроп"}""")]
    [InlineData("convert-to-drop", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000023"}""")]
    [InlineData("convert-to-drop", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000024","comment":null}""")]
    [InlineData("convert-to-drop", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000025","comment":42}""")]
    [InlineData("convert-to-drop", "not json")]
    public async Task Invalid_input_is_a_bad_request(string action, string body)
    {
        var runId = await VasyaTechRerolledAsync();
        var admin = await _site.SignedInAsync("admin");
        var before = await EventCountAsync();

        var response = await admin.PostAsync(
            action == "tech-reroll" ? TechRerollUrl() : ConvertUrl(runId), new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("tech-reroll")]
    [InlineData("convert-to-drop")]
    public async Task Comment_over_500_characters_is_a_bad_request(string action)
    {
        var runId = await VasyaTechRerolledAsync();
        await PostOkAsync(await _site.SignedInAsync("vasya"), PlayerUrl("start"), new { commandId = Guid.NewGuid() });
        var admin = await _site.SignedInAsync("admin");
        var before = await EventCountAsync();

        var response = await admin.PostAsJsonAsync(
            action == "tech-reroll" ? TechRerollUrl() : ConvertUrl(runId),
            new { commandId = Guid.NewGuid(), reason = "other", comment = new string('я', 501) },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Fact]
    public async Task Run_id_that_is_not_a_guid_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(
            $"/api/admin/seasons/{SiteFactory.SeasonId}/runs/not-a-guid/convert-to-drop", new { commandId = Guid.NewGuid(), comment = "Дроп" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private string UrlFor(string action, Guid? seasonId = null) =>
        action == "tech-reroll" ? TechRerollUrl(seasonId: seasonId) : ConvertUrl(Guid.NewGuid(), seasonId);
}
