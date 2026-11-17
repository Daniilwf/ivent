using System.Net;
using System.Text.Json;
using static GameEvent.Web.Tests.Api.EventSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// SPEC «Ивенты» accumulation and «Условия от ивентов»: an original event description is not a list of consequences
/// still waiting. The own view identifies the actual next roll / next throw modifiers, excluding a condition already
/// attached to an offered run. Written against the HTTP contract, without reading the rule implementation.
/// </summary>
public sealed class WaitingEventTimingApiTests : IAsyncLifetime
{
    private const string Filter = """
        { "id": "short-wait", "kind": "event", "name": "Только короткая игра",
          "description": "Следующая игра короче тысячи часов.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [
            { "type": "modifyNextRoll", "filter": { "maxHours": 1000 } }
          ] } }
        """;

    private const string FilterAndDice = """
        { "id": "two-steps", "kind": "event", "name": "Два разных шага",
          "description": "Следующая игра короче тысячи часов; следующий бросок получает дополнительный кубик.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [
            { "type": "modifyNextRoll", "filter": { "maxHours": 1000 } },
            { "type": "modifyDice", "when": "next", "stage": "count", "value": 1 }
          ] } }
        """;

    private const string ConditionAndDice = """
        { "id": "trial-and-die", "kind": "event", "name": "Испытание и кубик",
          "description": "Следующую игру нужно пройти на сложной сложности или выше. В следующем броске на один кубик больше.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "target": { "selector": "self" }, "actions": [
            { "type": "modifyNextRoll", "runCondition": { "difficultyAtLeast": "hard" } },
            { "type": "modifyDice", "when": "next", "stage": "count", "value": 1 }
          ] } }
        """;

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Theory]
    [InlineData(Filter, "short-wait", true, false)]
    [InlineData(Tailwind, "tailwind", false, true)]
    [InlineData(FilterAndDice, "two-steps", true, true)]
    public async Task The_waiting_row_names_exactly_the_remaining_steps_of_the_event(
        string content, string objectId, bool waitingForRoll, bool waitingForThrow)
    {
        var (_, vasya) = await DrawAsync(content);

        var row = Assert.Single((await MeAsync(vasya)).GetProperty("waitingEvents").EnumerateArray());

        Assert.Equal(objectId, row.GetProperty("objectId").GetString());
        Assert.Equal("bad", row.GetProperty("kind").GetString());
        AssertTiming(row, waitingForRoll, waitingForThrow);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_pure_run_condition_is_not_reported_as_a_waiting_roll_modifier()
    {
        var (_, vasya) = await DrawAsync(NotEvenSweating);

        var me = await MeAsync(vasya);

        Assert.Empty(me.GetProperty("waitingEvents").EnumerateArray());
        Assert.Equal("not-even-sweating", Assert.Single(me.GetProperty("waitingConditions").EnumerateArray()).GetProperty("objectId").GetString());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task An_applied_roll_filter_disappears_without_waiting_for_a_completion()
    {
        var (_, vasya) = await DrawAsync(Filter);

        var response = await PostOkAsync(vasya, $"{Season}/roll", new { commandId = Guid.NewGuid() });

        Assert.Contains("roll-modifiers-applied", await TypesAsync(response));
        var me = await MeAsync(vasya);
        Assert.Equal(JsonValueKind.Object, me.GetProperty("offer").ValueKind);
        Assert.Empty(me.GetProperty("waitingEvents").EnumerateArray());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_throw_modifier_keeps_waiting_through_the_roll_and_start_and_leaves_after_the_throw()
    {
        var (_, vasya) = await DrawAsync(Tailwind);

        await ApiCalls.PostOkAsync(vasya, $"{Season}/roll");
        AssertTiming(Assert.Single((await MeAsync(vasya)).GetProperty("waitingEvents").EnumerateArray()), false, true);
        await ApiCalls.PostOkAsync(vasya, $"{Season}/start");
        AssertTiming(Assert.Single((await MeAsync(vasya)).GetProperty("waitingEvents").EnumerateArray()), false, true);
        await ApiCalls.PostOkAsync(vasya, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });

        var me = await MeAsync(vasya);
        Assert.Equal(4, me.GetProperty("lastCompleted").GetProperty("dice").GetArrayLength());
        Assert.Empty(me.GetProperty("waitingEvents").EnumerateArray());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task One_event_with_both_steps_keeps_one_row_with_only_the_throw_after_the_roll()
    {
        var (_, vasya) = await DrawAsync(FilterAndDice);

        await ApiCalls.PostOkAsync(vasya, $"{Season}/roll");

        var row = Assert.Single((await MeAsync(vasya)).GetProperty("waitingEvents").EnumerateArray());
        Assert.Equal("two-steps", row.GetProperty("objectId").GetString());
        AssertTiming(row, false, true);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_condition_plus_extra_die_does_not_claim_the_consumed_condition_still_waits_for_a_roll()
    {
        var (_, vasya) = await DrawAsync(ConditionAndDice);

        await ApiCalls.PostOkAsync(vasya, $"{Season}/roll");

        var me = await MeAsync(vasya);
        Assert.Empty(me.GetProperty("waitingConditions").EnumerateArray());
        var condition = Assert.Single(me.GetProperty("offer").GetProperty("conditions").EnumerateArray());
        Assert.Equal("trial-and-die", condition.GetProperty("objectId").GetString());
        Assert.Equal("hard", condition.GetProperty("difficultyAtLeast").GetString());
        var row = Assert.Single(me.GetProperty("waitingEvents").EnumerateArray());
        Assert.Equal("trial-and-die", row.GetProperty("objectId").GetString());
        AssertTiming(row, false, true);
        // The content is original prose, not recomputed instructions for the next run.
        Assert.StartsWith("Следующую игру нужно пройти", row.GetProperty("description").GetString(), StringComparison.Ordinal);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task An_unmet_condition_with_no_throw_leaves_the_extra_die_waiting_until_a_real_throw()
    {
        // SPEC «без кубов», existing RunConditionTests: no throw cannot spend a modifier for the next throw.
        var (_, vasya) = await DrawAsync(ConditionAndDice);
        await CompleteAsync(vasya, "normal");

        var me = await MeAsync(vasya);
        var completed = me.GetProperty("lastCompleted");
        Assert.Empty(completed.GetProperty("dice").EnumerateArray());
        Assert.False(completed.GetProperty("conditionCheck").GetProperty("met").GetBoolean());
        Assert.Empty(me.GetProperty("waitingConditions").EnumerateArray());
        AssertTiming(Assert.Single(me.GetProperty("waitingEvents").EnumerateArray()), false, true);

        var (secondRunId, _) = await CompleteKnownRunAsync(vasya);

        me = await MeAsync(vasya);
        Assert.Equal(secondRunId, me.GetProperty("lastCompleted").GetProperty("id").GetGuid());
        Assert.Equal(4, me.GetProperty("lastCompleted").GetProperty("dice").GetArrayLength());
        Assert.Empty(me.GetProperty("waitingEvents").EnumerateArray());
        await AssertIntactAsync(_site);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("reviewOlder")]
    [InlineData("approveOlder")]
    [InlineData("approveLatest")]
    [InlineData("rejectOlder")]
    [InlineData("rejectLatest")]
    [InlineData("undoLatest")]
    [InlineData("undoThenComplete")]
    public async Task Same_clock_completions_keep_the_actual_latest_run_after_reviews_rejections_and_undo(string operation)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Tailwind);
        var (first, _) = await CompleteKnownRunAsync(vasya);
        // Three completed candidates are possible without bypassing maxUncheckedRuns: an approved run still exists.
        await ApiCalls.PostOkAsync(admin, $"{Admin}/runs/{first}/approve", new { commandId = Guid.NewGuid(), comment = "Видел прохождение" });
        var (second, _) = await CompleteKnownRunAsync(vasya);
        var (third, thirdCommand) = await CompleteKnownRunAsync(vasya);
        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
        var completed = await StateAsync(_site);
        Assert.Equal(completed.Runs[first].CompletedAt, completed.Runs[second].CompletedAt);
        Assert.Equal(completed.Runs[second].CompletedAt, completed.Runs[third].CompletedAt);

        // A later review or rejection does not make an earlier completion the most recent one.
        switch (operation)
        {
            case "reviewOlder":
                await ApiCalls.PostOkAsync(vasya, $"{Season}/runs/{first}/review", new { commandId = Guid.NewGuid(), rating = 8, text = "Хорошая игра" });
                break;
            case "approveOlder":
                await ApiCalls.PostOkAsync(admin, $"{Admin}/runs/{second}/approve", new { commandId = Guid.NewGuid(), comment = "Видел прохождение" });
                break;
            case "approveLatest":
                await ApiCalls.PostOkAsync(admin, $"{Admin}/runs/{third}/approve", new { commandId = Guid.NewGuid(), comment = "Видел прохождение" });
                break;
            case "rejectOlder":
                await ApiCalls.PostOkAsync(admin, $"{Admin}/runs/{second}/reject", new { commandId = Guid.NewGuid(), comment = "На пруфе другая игра" });
                break;
            case "rejectLatest":
                await ApiCalls.PostOkAsync(admin, $"{Admin}/runs/{third}/reject", new { commandId = Guid.NewGuid(), comment = "На пруфе другая игра" });
                break;
            case "undoLatest":
            case "undoThenComplete":
                await ApiCalls.PostOkAsync(admin, $"{Admin}/undo", new { commandId = Guid.NewGuid(), targetCommandId = thirdCommand, comment = "Ошибка завершения" });
                if (operation == "undoThenComplete")
                {
                    await ApiCalls.PostOkAsync(vasya, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
                }
                break;
        }

        var me = await MeAsync(vasya);
        var latest = me.GetProperty("lastCompleted");
        Assert.Equal(operation == "undoLatest" ? second : third, latest.GetProperty("id").GetGuid());
        Assert.Equal(operation == "rejectLatest" ? "rejected" : "completed", latest.GetProperty("status").GetString());
        Assert.DoesNotContain(me.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetGuid() == latest.GetProperty("id").GetGuid());
        if (operation == "undoLatest")
        {
            Assert.Equal(third, me.GetProperty("activeRun").GetProperty("id").GetGuid());
        }
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task The_existing_roll_then_throw_row_order_survives_the_new_timing_fields()
    {
        var (admin, vasya) = await DrawAsync(Tailwind);
        await ApiCalls.PostOkAsync(admin, $"/api/test/seasons/{SiteFactory.SeasonId}/content", Pack(Filter));
        await DropAsync(vasya);

        var rows = (await MeAsync(vasya)).GetProperty("waitingEvents").EnumerateArray().ToArray();

        // The view has always enumerated waiting roll modifiers before waiting throw modifiers, without sorting kinds.
        Assert.Equal(new[] { "short-wait", "tailwind" }, rows.Select(r => r.GetProperty("objectId").GetString()));
        AssertTiming(rows[0], true, false);
        AssertTiming(rows[1], false, true);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Switching_events_off_hides_existing_waiting_steps_and_switching_on_restores_them()
    {
        var (admin, vasya) = await DrawAsync(ConditionAndDice);

        await SwitchEventsAsync(admin, on: false);
        var off = await MeAsync(vasya);
        Assert.Empty(off.GetProperty("waitingEvents").EnumerateArray());
        Assert.Empty(off.GetProperty("waitingConditions").EnumerateArray());

        await SwitchEventsAsync(admin, on: true);
        var on = await MeAsync(vasya);
        AssertTiming(Assert.Single(on.GetProperty("waitingEvents").EnumerateArray()), false, true);
        Assert.Single(on.GetProperty("waitingConditions").EnumerateArray());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task The_timing_fields_belong_to_the_own_turn_and_do_not_appear_for_another_role_or_player()
    {
        var (_, vasya) = await DrawAsync(FilterAndDice);
        AssertTiming(Assert.Single((await MeAsync(vasya)).GetProperty("waitingEvents").EnumerateArray()), true, true);
        var petya = await _site.SignedInAsync("petya");
        Assert.Empty((await MeAsync(petya)).GetProperty("waitingEvents").EnumerateArray());
        foreach (var login in new[] { "admin", "zritel" })
        {
            var client = await _site.SignedInAsync(login);
            var season = await ApiCalls.OkAsync(await client.GetAsync(Season, Ct));
            Assert.Equal(JsonValueKind.Null, season.GetProperty("me").ValueKind);
        }
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task An_unsigned_reader_cannot_read_a_turns_waiting_steps()
    {
        await DrawAsync(FilterAndDice);
        using var anonymous = _site.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Season, Ct)).StatusCode);
    }

    private async Task<(HttpClient Admin, HttpClient Vasya)> DrawAsync(string content)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, content);
        await DropAsync(vasya);
        return (admin, vasya);
    }

    private static async Task<(Guid RunId, Guid CommandId)> CompleteKnownRunAsync(HttpClient player)
    {
        await ApiCalls.PostOkAsync(player, $"{Season}/roll");
        await ApiCalls.PostOkAsync(player, $"{Season}/start");
        var runId = (await MeAsync(player)).GetProperty("activeRun").GetProperty("id").GetGuid();
        var commandId = Guid.NewGuid();
        await ApiCalls.PostOkAsync(player, $"{Season}/complete", new { commandId, difficulty = "normal" });
        return (runId, commandId);
    }

    private static void AssertTiming(JsonElement row, bool waitingForRoll, bool waitingForThrow)
    {
        Assert.Equal(waitingForRoll, row.GetProperty("waitingForRoll").GetBoolean());
        Assert.Equal(waitingForThrow, row.GetProperty("waitingForThrow").GetBoolean());
    }
}
