using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The admin's proof queue over HTTP (C8, W4, W5, W8, SE6, D-15, D-98):
/// <c>GET /api/admin/seasons/{id}/proofs</c> — the runs to check in queue order (finishes on top, then by completion
/// time): [{runId, playerId, playerName, gameTitle, completedAt, reachedFinish, status, links, note, witnessName}],
/// <c>status</c> is <c>"pending"</c> when a proof was sent and null when not;
/// <c>POST /api/admin/seasons/{id}/runs/{runId}/approve</c> {commandId, difficulty?, comment?} — «одобрить» or «одобрить без
/// скрина» (the comment is then required by the engine), a lower difficulty by the proof;
/// <c>POST /api/admin/seasons/{id}/runs/{runId}/reject</c> {commandId, comment} — the run's points, cells and coins taken
/// back. Comments are at most 500 characters (400 above); rule refusals are 409 with the engine code. Admin only:
/// players and spectators are forbidden. Responses are read as raw JSON.
/// </summary>
public sealed class AdminProofApiTests : IAsyncLifetime
{
    private const string Link = "https://imgur.com/a/credits";

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PlayerUrl(string action, Guid? seasonId = null) => $"/api/seasons/{seasonId ?? SiteFactory.SeasonId}/{action}";

    private static string QueueUrl(Guid? seasonId = null) => $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/proofs";

    private static string Url(string action, Guid runId, Guid? seasonId = null) =>
        $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/runs/{runId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    private static object ValidBody(string action) =>
        action == "approve"
            ? new { commandId = Guid.NewGuid(), comment = "Видел на стриме" }
            : new { commandId = Guid.NewGuid(), comment = "На скрине другая игра" };

    // ---- The queue ----

    [Fact]
    public async Task Queue_is_empty_without_completed_runs()
    {
        var admin = await _site.SignedInAsync("admin");

        using var queue = JsonDocument.Parse(await admin.GetStringAsync(QueueUrl(), Ct));

        Assert.Equal(0, queue.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Queue_lists_unchecked_runs_in_the_order_they_were_completed_with_their_proofs()
    {
        // Вася completes first without a proof; Петя later, with links, a note and Вася as the witness
        var vasyaRun = await CompletedAsync("vasya");
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        var petyaRun = await CompletedAsync("petya");
        var petya = await _site.SignedInAsync("petya");
        await PostOkAsync(petya, PlayerUrl($"runs/{petyaRun}/proof"), new { commandId = Guid.NewGuid(), links = new[] { Link }, note = "Титры", witnessId = _site.Players["vasya"] });
        var admin = await _site.SignedInAsync("admin");

        using var queue = JsonDocument.Parse(await admin.GetStringAsync(QueueUrl(), Ct));

        var items = queue.RootElement.EnumerateArray().ToList();
        Assert.Equal([vasyaRun, petyaRun], items.Select(i => i.GetProperty("runId").GetGuid()));
        var first = items[0];
        Assert.Equal(_site.Players["vasya"], first.GetProperty("playerId").GetGuid());
        Assert.Equal("vasya", first.GetProperty("playerName").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("gameTitle").GetString()));
        Assert.False(first.GetProperty("reachedFinish").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("status").ValueKind);
        Assert.Equal(0, first.GetProperty("links").GetArrayLength());
        var second = items[1];
        Assert.Equal("pending", second.GetProperty("status").GetString());
        Assert.Equal([Link], second.GetProperty("links").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("Титры", second.GetProperty("note").GetString());
        Assert.Equal("vasya", second.GetProperty("witnessName").GetString());
        Assert.True(first.GetProperty("completedAt").GetDateTimeOffset() < second.GetProperty("completedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Queue_items_carry_the_claimed_difficulty_the_hours_and_the_dice_total()
    {
        // D-98: what an approval at a lower difficulty or a reject would change; Вася's only run gave all his points
        var runId = await CompletedAsync("vasya", "hard");
        var vasya = await _site.SignedInAsync("vasya");
        var points = await PointsOfAsync(vasya, "vasya");
        var admin = await _site.SignedInAsync("admin");

        using var queue = JsonDocument.Parse(await admin.GetStringAsync(QueueUrl(), Ct));

        var item = Assert.Single(queue.RootElement.EnumerateArray().ToList());
        Assert.Equal(runId, item.GetProperty("runId").GetGuid());
        Assert.Equal("hard", item.GetProperty("difficulty").GetString());
        Assert.Equal(points, item.GetProperty("diceTotal").GetInt32());
        Assert.True(points > 0);
        await using var db = _site.NewDb();
        var hours = (await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId, Ct)).Hours;
        Assert.NotNull(hours);
        Assert.Equal(hours.Value, item.GetProperty("hours").GetDecimal());
    }

    [Fact]
    public async Task Queue_dice_total_follows_an_hours_correction()
    {
        // The total is the run's dice after corrections (C7b), not the ones rolled at completion
        var runId = await CompletedAsync("vasya");
        await using (var db = _site.NewDb())
        {
            var hours = (await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId, Ct)).Hours!.Value;
            await _site.SendAsync(new CorrectRunHours(runId, hours + 30, "Часы по HLTB"));
        }

        var vasya = await _site.SignedInAsync("vasya");
        var points = await PointsOfAsync(vasya, "vasya");
        var admin = await _site.SignedInAsync("admin");

        using var queue = JsonDocument.Parse(await admin.GetStringAsync(QueueUrl(), Ct));

        var item = Assert.Single(queue.RootElement.EnumerateArray().ToList());
        Assert.Equal(points, item.GetProperty("diceTotal").GetInt32());
    }

    [Fact]
    public async Task Run_that_reached_the_finish_is_on_top_of_the_queue()
    {
        // Вася completes first from the start; the admin puts Петя one cell before the finish, so his completion finishes
        var vasyaRun = await CompletedAsync("vasya");
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        await _site.SendAsync(new AdjustPlayer(_site.Players["petya"], "Перенос для теста", CellId: beforeFinish));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        var petyaRun = await CompletedAsync("petya");
        var admin = await _site.SignedInAsync("admin");

        using var queue = JsonDocument.Parse(await admin.GetStringAsync(QueueUrl(), Ct));

        var items = queue.RootElement.EnumerateArray().ToList();
        Assert.Equal([petyaRun, vasyaRun], items.Select(i => i.GetProperty("runId").GetGuid()));
        Assert.True(items[0].GetProperty("reachedFinish").GetBoolean());
    }

    [Fact]
    public async Task Approved_and_rejected_runs_leave_the_queue()
    {
        var vasyaRun = await CompletedAsync("vasya");
        var petyaRun = await CompletedAsync("petya");
        var admin = await _site.SignedInAsync("admin");

        await PostOkAsync(admin, Url("approve", vasyaRun), new { commandId = Guid.NewGuid(), comment = "Видел на стриме" });
        await PostOkAsync(admin, Url("reject", petyaRun), new { commandId = Guid.NewGuid(), comment = "Не та игра" });

        using var queue = JsonDocument.Parse(await admin.GetStringAsync(QueueUrl(), Ct));
        Assert.Equal(0, queue.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("masha")]
    [InlineData("zritel")]
    public async Task Queue_is_forbidden_to_players_and_spectators(string login)
    {
        var client = await _site.SignedInAsync(login);

        var response = await client.GetAsync(QueueUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Queue_is_unauthorized_to_the_anonymous()
    {
        var client = await _site.AnonymousAsync();

        var response = await client.GetAsync(QueueUrl(), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Queue_of_an_unknown_season_is_not_found()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.GetAsync(QueueUrl(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Approve ----

    [Fact]
    public async Task Admin_approves_a_submitted_proof()
    {
        var runId = await CompletedAsync("vasya");
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, PlayerUrl($"runs/{runId}/proof"), new { commandId = Guid.NewGuid(), links = new[] { Link } });
        var admin = await _site.SignedInAsync("admin");

        var response = await PostOkAsync(admin, Url("approve", runId), new { commandId = Guid.NewGuid() });

        Assert.Equal(["proof-approved"], await TypesAsync(response));
        await using var db = _site.NewDb();
        Assert.Equal(ProofStatus.Approved, (await db.Proofs.AsNoTracking().SingleAsync(Ct)).Status);
        var logged = await db.Events.SingleAsync(e => e.Type == "proof-approved", Ct);
        Assert.Equal(_site.Users["admin"], logged.AuthorId);
        using var data = JsonDocument.Parse(logged.Data);
        Assert.False(data.RootElement.GetProperty("withoutProof").GetBoolean());
        Assert.Equal("approved", (await MeJsonAsync(vasya)).GetProperty("lastCompleted").GetProperty("proof").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Admin_approves_without_a_proof_with_a_comment()
    {
        var runId = await CompletedAsync("vasya");
        var admin = await _site.SignedInAsync("admin");

        await PostOkAsync(admin, Url("approve", runId), new { commandId = Guid.NewGuid(), comment = "Видел на стриме" });

        await using var db = _site.NewDb();
        var logged = await db.Events.SingleAsync(e => e.Type == "proof-approved", Ct);
        using var data = JsonDocument.Parse(logged.Data);
        Assert.True(data.RootElement.GetProperty("withoutProof").GetBoolean());
        Assert.Equal("Видел на стриме", data.RootElement.GetProperty("comment").GetString());
        var row = await db.Proofs.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((ProofStatus.Approved, (string?)"Видел на стриме", (DateTimeOffset?)null), (row.Status, row.Comment, row.SubmittedAt));
    }

    [Fact]
    public async Task Approval_without_a_proof_and_without_a_comment_is_a_conflict()
    {
        var runId = await CompletedAsync("vasya");
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url("approve", runId), new { commandId = Guid.NewGuid() }, Ct);

        await AssertConflictAsync(response, "player.commentRequired");
        Assert.Equal(0, await EventCountAsync("proof-approved"));
    }

    [Fact]
    public async Task Admin_approves_at_a_lower_difficulty_by_the_proof()
    {
        var runId = await CompletedAsync("vasya", "hard");
        var admin = await _site.SignedInAsync("admin");

        var response = await PostOkAsync(admin, Url("approve", runId), new { commandId = Guid.NewGuid(), difficulty = "normal", comment = "На скрине нормальная" });

        var types = await TypesAsync(response);
        Assert.Equal("run-difficulty-changed", types[0]);
        Assert.Equal("proof-approved", types[^1]);
        await using var db = _site.NewDb();
        Assert.Equal(Difficulty.Normal, (await db.Runs.SingleAsync(r => r.Id == runId, Ct)).Difficulty);
    }

    [Fact]
    public async Task Higher_difficulty_than_claimed_is_a_conflict()
    {
        var runId = await CompletedAsync("vasya", "normal");
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url("approve", runId), new { commandId = Guid.NewGuid(), difficulty = "hard", comment = "Хард" }, Ct);

        await AssertConflictAsync(response, "proof.difficultyAboveClaimed");
    }

    // ---- Reject ----

    [Fact]
    public async Task Admin_rejects_a_run_and_its_points_cells_and_coins_are_taken_back()
    {
        var runId = await CompletedAsync("vasya");
        var vasya = await _site.SignedInAsync("vasya");
        Assert.True(await PointsOfAsync(vasya, "vasya") > 0);
        var admin = await _site.SignedInAsync("admin");

        var response = await PostOkAsync(admin, Url("reject", runId), new { commandId = Guid.NewGuid(), comment = "На скрине другая игра" });

        Assert.Equal(["proof-rejected", "points-changed", "player-moved", "coins-changed"], await TypesAsync(response));
        Assert.Equal(0, await PointsOfAsync(vasya, "vasya"));
        await using var db = _site.NewDb();
        Assert.Equal(RunStatus.Rejected, (await db.Runs.SingleAsync(r => r.Id == runId, Ct)).Status);
        var player = await db.SeasonPlayers.AsNoTracking().SingleAsync(p => p.Id == _site.Players["vasya"], Ct);
        Assert.Equal(("start", 0, 0), (player.CellId, player.Points, player.Coins));
        var row = await db.Proofs.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((ProofStatus.Rejected, (string?)"На скрине другая игра"), (row.Status, row.Comment));
        var logged = await db.Events.SingleAsync(e => e.Type == "proof-rejected", Ct);
        Assert.Equal(_site.Users["admin"], logged.AuthorId);
    }

    [Fact]
    public async Task Game_of_a_rejected_run_can_be_rolled_again()
    {
        // D-15: the game returns to the pool for everyone; with the other games deleted Петя can still roll
        var runId = await CompletedAsync("vasya");
        await using (var db = _site.NewDb())
        {
            var game = (await db.Runs.SingleAsync(r => r.Id == runId, Ct)).GameId;
            await db.Games.Where(g => g.Id != game).ExecuteUpdateAsync(g => g.SetProperty(x => x.IsDeleted, true), Ct);
        }

        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, Url("reject", runId), new { commandId = Guid.NewGuid(), comment = "Не та игра" });
        var petya = await _site.SignedInAsync("petya");

        var response = await petya.PostAsJsonAsync(PlayerUrl("roll"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_reject_comment_is_a_conflict(string comment)
    {
        var runId = await CompletedAsync("vasya");
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url("reject", runId), new { commandId = Guid.NewGuid(), comment }, Ct);

        await AssertConflictAsync(response, "player.commentRequired");
        Assert.Equal(0, await EventCountAsync("proof-rejected"));
    }

    // ---- Both: engine rules ----

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Checked_run_is_a_conflict_with_already_reviewed(string action)
    {
        var runId = await CompletedAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        await PostOkAsync(admin, Url("approve", runId), new { commandId = Guid.NewGuid(), comment = "Видел на стриме" });

        var response = await admin.PostAsJsonAsync(Url(action, runId), ValidBody(action), Ct);

        await AssertConflictAsync(response, "proof.alreadyReviewed");
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Run_being_played_is_a_conflict(string action)
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostOkAsync(vasya, PlayerUrl("roll"), new { commandId = Guid.NewGuid() });
        await PostOkAsync(vasya, PlayerUrl("start"), new { commandId = Guid.NewGuid() });
        var runId = Guid.Parse((await MeJsonAsync(vasya)).GetProperty("activeRun").GetProperty("id").GetString()!);
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url(action, runId), ValidBody(action), Ct);

        await AssertConflictAsync(response, "run.notCompleted");
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Unknown_run_is_a_conflict(string action)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url(action, Guid.NewGuid()), ValidBody(action), Ct);

        await AssertConflictAsync(response, "run.unknown");
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Review_is_allowed_while_the_season_is_closing(string action)
    {
        var runId = await CompletedAsync("vasya");
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Closing));
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url(action, runId), ValidBody(action), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Review_after_the_season_is_finished_is_a_conflict(string action)
    {
        // The season finishes only with every run checked (D-101): the run is approved first, so after the finish the
        // season being over is what stops the request
        var runId = await CompletedAsync("vasya");
        await _site.SendAsync(new ApproveProof(runId, null, "Видел на стриме"));
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Closing));
        await _site.SendAsync(new ChangeSeasonStatus(SeasonStatus.Finished));
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url(action, runId), ValidBody(action), Ct);

        await AssertConflictAsync(response, "season.closed");
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Run_of_another_season_is_a_conflict_and_leaves_that_season_alone(string action)
    {
        var seasonB = await _site.CreateSeasonAsync();
        await SendToAsync(seasonB, new ChangeSeasonStatus(SeasonStatus.Active));
        await SendToAsync(seasonB, new AddSeasonPlayer(Guid.NewGuid(), _site.Users["vasya"], "vasya"));
        var runB = await CompletedAsync("vasya", seasonId: seasonB);
        var admin = await _site.SignedInAsync("admin");
        List<long> logB;
        await using (var db = _site.NewDb())
        {
            logB = await db.Events.Where(e => e.SeasonId == seasonB).Select(e => e.Id).ToListAsync(Ct);
        }

        var response = await admin.PostAsJsonAsync(Url(action, runB), ValidBody(action), Ct);

        await AssertConflictAsync(response, "run.unknown");
        await using var after = _site.NewDb();
        Assert.Equal(logB, await after.Events.Where(e => e.SeasonId == seasonB).Select(e => e.Id).ToListAsync(Ct));
    }

    // ---- Idempotency ----

    [Theory]
    [InlineData("approve", "proof-approved")]
    [InlineData("reject", "proof-rejected")]
    public async Task Repeating_with_the_same_command_id_acts_once(string action, string type)
    {
        var runId = await CompletedAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var body = ValidBody(action);

        await PostOkAsync(admin, Url(action, runId), body);
        var second = await PostOkAsync(admin, Url(action, runId), body);

        using var repeat = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
        Assert.True(repeat.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.Equal(1, await EventCountAsync(type));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Same_command_id_with_another_body_is_a_conflict(string action)
    {
        var runId = await CompletedAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var commandId = Guid.NewGuid();

        await PostOkAsync(admin, Url(action, runId), new { commandId, comment = "Первый" });
        var before = await EventCountAsync();
        var second = await admin.PostAsJsonAsync(Url(action, runId), new { commandId, comment = "Второй" }, Ct);

        await AssertConflictAsync(second, "command.idReused");
        Assert.Equal(before, await EventCountAsync());
    }

    // ---- Refused: roles, session, CSRF, season ----

    [Theory]
    [InlineData("vasya", "approve")]
    [InlineData("petya", "approve")]
    [InlineData("masha", "approve")]
    [InlineData("zritel", "approve")]
    [InlineData("vasya", "reject")]
    [InlineData("petya", "reject")]
    [InlineData("masha", "reject")]
    [InlineData("zritel", "reject")]
    public async Task Players_and_spectators_are_forbidden(string login, string action)
    {
        // Вася's run is completed, so only the role can stop the request — even his own run
        var runId = await CompletedAsync("vasya");
        var client = await _site.SignedInAsync(login);
        var before = await EventCountAsync();

        var response = await client.PostAsJsonAsync(Url(action, runId), ValidBody(action), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Anonymous_is_unauthorized(string action)
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync(Url(action, Guid.NewGuid()), ValidBody(action), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Post_without_the_antiforgery_token_is_refused(string action)
    {
        var runId = await CompletedAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        admin.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);
        var before = await EventCountAsync();

        var response = await admin.PostAsJsonAsync(Url(action, runId), ValidBody(action), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Unknown_season_is_not_found(string action)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(Url(action, Guid.NewGuid(), Guid.NewGuid()), ValidBody(action), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Run_id_that_is_not_a_guid_is_not_found(string action)
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync($"/api/admin/seasons/{SiteFactory.SeasonId}/runs/not-a-guid/{action}", ValidBody(action), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Invalid input: 400 ----

    [Theory]
    [InlineData("approve", """{"commandId":"00000000-0000-0000-0000-000000000000","comment":"Ок"}""")]
    [InlineData("approve", """{"comment":"Ок"}""")]
    [InlineData("approve", """{"commandId":"not-a-guid","comment":"Ок"}""")]
    [InlineData("approve", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000401","difficulty":"impossible","comment":"Ок"}""")]
    [InlineData("approve", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000402","difficulty":7,"comment":"Ок"}""")]
    [InlineData("approve", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000403","comment":42}""")]
    [InlineData("approve", "not json")]
    [InlineData("reject", """{"commandId":"00000000-0000-0000-0000-000000000000","comment":"Нет"}""")]
    [InlineData("reject", """{"comment":"Нет"}""")]
    [InlineData("reject", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000411"}""")]
    [InlineData("reject", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000412","comment":null}""")]
    [InlineData("reject", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000413","comment":42}""")]
    [InlineData("reject", "not json")]
    public async Task Invalid_input_is_a_bad_request(string action, string body)
    {
        var runId = await CompletedAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var before = await EventCountAsync();

        var response = await admin.PostAsync(Url(action, runId), new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Comment_over_500_characters_is_a_bad_request(string action)
    {
        var runId = await CompletedAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var before = await EventCountAsync();

        var response = await admin.PostAsJsonAsync(
            Url(action, runId), new { commandId = Guid.NewGuid(), comment = new string('я', Limits.MaxCommentLength + 1) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await EventCountAsync());
    }

    // ---- Helpers ----

    /// <summary>The player rolls, starts and completes a game through the player API; returns the completed run.</summary>
    private async Task<Guid> CompletedAsync(string login, string difficulty = "normal", Guid? seasonId = null)
    {
        var client = await _site.SignedInAsync(login);
        await PostOkAsync(client, PlayerUrl("roll", seasonId), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, PlayerUrl("start", seasonId), new { commandId = Guid.NewGuid() });
        await PostOkAsync(client, PlayerUrl("complete", seasonId), new { commandId = Guid.NewGuid(), difficulty });
        await using var db = _site.NewDb();
        var userPlayers = await db.SeasonPlayers
            .Where(p => p.SeasonId == (seasonId ?? SiteFactory.SeasonId) && p.UserId == _site.Users[login])
            .Select(p => p.Id)
            .ToListAsync(Ct);
        var runs = await db.Runs
            .Where(r => r.SeasonId == (seasonId ?? SiteFactory.SeasonId) && r.Status == RunStatus.Completed && userPlayers.Contains(r.PlayerId))
            .ToListAsync(Ct);
        return runs.OrderBy(r => r.CompletedAt).Last().Id;
    }

    private async Task SendToAsync(Guid seasonId, ICommand command)
    {
        var outcome = await _site.Services.GetRequiredService<CommandBus>().SendAsync(
            new CommandEnvelope(Guid.NewGuid(), seasonId, command, AuthorId: null), Ct);
        Assert.True(outcome.IsAccepted, $"{command} rejected: {outcome.Rejection}");
    }

    private async Task<int> EventCountAsync(string? type = null)
    {
        await using var db = _site.NewDb();
        return type is null ? await db.Events.CountAsync(Ct) : await db.Events.CountAsync(e => e.Type == type, Ct);
    }

    private async Task<int> PointsOfAsync(HttpClient client, string login)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("players").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == _site.Players[login]).GetProperty("points").GetInt32();
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

    private static async Task<JsonElement> MeJsonAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("me").Clone();
    }

    private static async Task<List<string>> TypesAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()!)];
    }
}
