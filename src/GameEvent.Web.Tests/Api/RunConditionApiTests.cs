using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static GameEvent.Web.Tests.Api.EventSeason;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Conditions of runs through the API (EV9, task 5.18, SPEC «Условия от ивентов», D-507, D-546 (2), D-821): a condition
/// waiting for the next roll is in my view (<c>me.waitingConditions</c>), the roll fixes it on the run
/// (<c>me.activeRun.conditions</c>), the completion checks it (<c>me.lastCompleted.conditions</c>,
/// <c>conditionCheck</c>); the admin marks it «не применимо»
/// (<c>POST /api/admin/seasons/{id}/runs/{runId}/waive-conditions</c>, <c>WaiveRunCondition</c>): the run counts as if it
/// were met and the condition goes on to the next run. The event is «Даже не вспотел» of CONTENT.md, drawn by a drop.
/// </summary>
public sealed class RunConditionApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string WaiveUrl(Guid runId, Guid? seasonId = null) =>
        $"/api/admin/seasons/{seasonId ?? SiteFactory.SeasonId}/runs/{runId}/waive-conditions";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- The view: waiting, on the run, checked ----

    [Fact]
    public async Task A_drawn_condition_waits_for_my_next_roll_in_my_view()
    {
        var (_, vasya) = await ConditionAsync();

        var condition = Assert.Single((await MeAsync(vasya)).GetProperty("waitingConditions").EnumerateArray());

        AssertNotEvenSweating(condition);
    }

    [Fact]
    public async Task The_roll_fixes_the_condition_on_the_run_and_the_run_page_shows_it()
    {
        var (_, vasya) = await ConditionAsync();

        await ApiCalls.PostOkAsync(vasya, $"{Season}/roll");
        await ApiCalls.PostOkAsync(vasya, $"{Season}/start");

        var me = await MeAsync(vasya);
        AssertNotEvenSweating(Assert.Single(me.GetProperty("activeRun").GetProperty("conditions").EnumerateArray()));
        Assert.Empty(me.GetProperty("waitingConditions").EnumerateArray());
    }

    [Fact]
    public async Task A_completion_below_the_condition_is_checked_unmet_and_has_no_dice()
    {
        var (_, vasya) = await ConditionAsync();

        await CompleteAsync(vasya, "normal");

        var completed = (await MeAsync(vasya)).GetProperty("lastCompleted");
        AssertNotEvenSweating(Assert.Single(completed.GetProperty("conditions").EnumerateArray()));
        var check = completed.GetProperty("conditionCheck");
        Assert.False(check.GetProperty("met").GetBoolean());
        Assert.False(check.GetProperty("waived").GetBoolean());
        Assert.Empty(completed.GetProperty("dice").EnumerateArray());
    }

    [Fact]
    public async Task A_completion_on_hard_meets_the_condition()
    {
        var (_, vasya) = await ConditionAsync();

        await CompleteAsync(vasya, "hard");

        var completed = (await MeAsync(vasya)).GetProperty("lastCompleted");
        Assert.True(completed.GetProperty("conditionCheck").GetProperty("met").GetBoolean());
        Assert.Equal(3, completed.GetProperty("dice").GetArrayLength());
    }

    [Fact]
    public async Task A_run_without_conditions_shows_none_and_no_check()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Tailwind);

        await CompleteAsync(vasya);

        var me = await MeAsync(vasya);
        Assert.Empty(me.GetProperty("waitingConditions").EnumerateArray());
        Assert.Empty(me.GetProperty("lastCompleted").GetProperty("conditions").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("lastCompleted").GetProperty("conditionCheck").ValueKind);
    }

    // ---- «Не применимо» by the admin ----

    [Fact]
    public async Task The_admin_waives_the_condition_the_run_gets_its_dice_and_the_condition_waits_again()
    {
        var (admin, vasya) = await ConditionAsync();
        var runId = await CompleteAsync(vasya, "normal");

        var response = await PostOkAsync(admin, WaiveUrl(runId), new { commandId = Guid.NewGuid(), comment = "Сложности в игре нет" });

        Assert.Contains("run-condition-checked", await TypesAsync(response));
        var me = await MeAsync(vasya);
        var check = me.GetProperty("lastCompleted").GetProperty("conditionCheck");
        Assert.True(check.GetProperty("met").GetBoolean());
        Assert.True(check.GetProperty("waived").GetBoolean());
        Assert.Equal(3, me.GetProperty("lastCompleted").GetProperty("dice").GetArrayLength());
        AssertNotEvenSweating(Assert.Single(me.GetProperty("waitingConditions").EnumerateArray()));
    }

    [Fact]
    public async Task The_same_command_id_twice_waives_once()
    {
        var (admin, vasya) = await ConditionAsync();
        var runId = await CompleteAsync(vasya, "normal");
        var commandId = Guid.NewGuid();

        await PostOkAsync(admin, WaiveUrl(runId), new { commandId, comment = "Сложности в игре нет" });
        await PostOkAsync(admin, WaiveUrl(runId), new { commandId, comment = "Сложности в игре нет" });

        Assert.Single((await MeAsync(vasya)).GetProperty("waitingConditions").EnumerateArray());
    }

    [Fact]
    public async Task A_run_without_conditions_cannot_be_waived()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Tailwind);
        var runId = await CompleteAsync(vasya);

        var response = await admin.PostAsJsonAsync(WaiveUrl(runId), new { commandId = Guid.NewGuid(), comment = "Сложности нет" }, Ct);

        await AssertConflictAsync(response, "run.noCondition");
    }

    [Fact]
    public async Task An_unknown_run_cannot_be_waived()
    {
        var admin = await _site.SignedInAsync("admin");

        var response = await admin.PostAsJsonAsync(WaiveUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), comment = "Сложности нет" }, Ct);

        await AssertConflictAsync(response, "run.unknown");
    }

    [Fact]
    public async Task A_run_still_being_played_cannot_be_waived()
    {
        var (admin, vasya) = await ConditionAsync();
        await ApiCalls.PostOkAsync(vasya, $"{Season}/roll");
        await ApiCalls.PostOkAsync(vasya, $"{Season}/start");
        var runId = (await MeAsync(vasya)).GetProperty("activeRun").GetProperty("id").GetGuid();

        var response = await admin.PostAsJsonAsync(WaiveUrl(runId), new { commandId = Guid.NewGuid(), comment = "Сложности нет" }, Ct);

        await AssertConflictAsync(response, "run.notCompleted");
    }

    [Fact]
    public async Task A_waive_without_a_comment_is_refused()
    {
        var (admin, vasya) = await ConditionAsync();
        var runId = await CompleteAsync(vasya, "normal");

        await AssertRefusedAsync(await admin.PostAsJsonAsync(WaiveUrl(runId), new { commandId = Guid.NewGuid() }, Ct));
        await AssertRefusedAsync(await admin.PostAsJsonAsync(WaiveUrl(runId), new { commandId = Guid.NewGuid(), comment = " " }, Ct));

        Assert.False((await MeAsync(vasya)).GetProperty("lastCompleted").GetProperty("conditionCheck").GetProperty("waived").GetBoolean());
    }

    [Fact]
    public async Task A_waive_with_a_long_comment_or_without_a_command_id_is_invalid_input()
    {
        var (admin, vasya) = await ConditionAsync();
        var runId = await CompleteAsync(vasya, "normal");

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(WaiveUrl(runId), new { commandId = Guid.NewGuid(), comment = new string('я', 501) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(WaiveUrl(runId), new { comment = "Сложности нет" }, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("petya")]
    [InlineData("zritel")]
    public async Task Players_and_spectators_cannot_waive(string login)
    {
        var (_, vasya) = await ConditionAsync();
        var runId = await CompleteAsync(vasya, "normal");
        var client = await _site.SignedInAsync(login);

        var response = await client.PostAsJsonAsync(WaiveUrl(runId), new { commandId = Guid.NewGuid(), comment = "Моё" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await MeAsync(vasya)).GetProperty("lastCompleted").GetProperty("conditionCheck").GetProperty("met").GetBoolean());
    }

    [Fact]
    public async Task The_anonymous_are_unauthorized_and_an_unknown_season_is_not_found()
    {
        var anonymous = await _site.AnonymousAsync();
        var admin = await _site.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(WaiveUrl(Guid.NewGuid()), new { commandId = Guid.NewGuid(), comment = "x" }, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync(WaiveUrl(Guid.NewGuid(), Guid.NewGuid()), new { commandId = Guid.NewGuid(), comment = "x" }, Ct)).StatusCode);
    }

    // ---- Helpers ----

    /// <summary>Events on with «Даже не вспотел» in the bad deck; Вася drops and has the condition on his next run.</summary>
    private async Task<(HttpClient Admin, HttpClient Vasya)> ConditionAsync()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, NotEvenSweating);
        await DropAsync(vasya);
        return (admin, vasya);
    }

    private static void AssertNotEvenSweating(JsonElement condition)
    {
        Assert.Equal(("not-even-sweating", "Даже не вспотел"), (condition.GetProperty("objectId").GetString(), condition.GetProperty("name").GetString()));
        Assert.Equal("bad", condition.GetProperty("kind").GetString());
        Assert.Equal("hard", condition.GetProperty("difficultyAtLeast").GetString());
        Assert.Equal(JsonValueKind.Null, condition.GetProperty("difficultyAtMost").ValueKind);
        Assert.False(condition.GetProperty("hostile").GetBoolean());
    }
}
