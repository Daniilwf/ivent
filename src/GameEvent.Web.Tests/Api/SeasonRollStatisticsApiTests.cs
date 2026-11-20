using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>Personal roll statistics from the final dice of credited completions (SPEC «Итоги сезона», D-927).</summary>
public sealed class SeasonRollStatisticsApiTests : IAsyncLifetime
{
    private const string HoursSource = "https://howlongtobeat.com/game/2231";
    private readonly DiceScript _random = new();
    private readonly SiteFactory _site;

    public SeasonRollStatisticsApiTests() => _site = new() { Random = _random };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Season => $"/api/seasons/{SiteFactory.SeasonId}";
    private static string AdminSeason => $"/api/admin/seasons/{SiteFactory.SeasonId}";

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await using var db = _site.NewDb();
        await db.Games.ExecuteUpdateAsync(games => games.SetProperty(game => game.Hours, (decimal?)null), Ct);
        await RulesAsync(_site, Flags(features => features with { SeasonStatistics = true }));
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Best_and_worst_use_relative_luck_instead_of_sum_and_omit_a_rejected_roll()
    {
        using var player = await _site.SignedInAsync("vasya");
        using var admin = await _site.SignedInAsync("admin");

        var smallMaximum = await CompleteAsync(player, "easy", 3, 2);
        var largerLowRoll = await CompleteAsync(player, "hard", 6, 2, 2);
        await PostOkAsync(admin, $"{AdminSeason}/runs/{smallMaximum.GetProperty("id").GetGuid()}/approve",
            new { commandId = Guid.NewGuid(), comment = "Пруф подтвердил первое прохождение" });
        var rejectedMinimum = await CompleteAsync(player, "normal", 3, 1);
        await PostOkAsync(admin, $"{AdminSeason}/runs/{rejectedMinimum.GetProperty("id").GetGuid()}/reject",
            new { commandId = Guid.NewGuid(), comment = "Пруф не подтвердил прохождение" });

        var statistics = await ReadStatisticsAsync(player);
        Assert.Equal(2, statistics.Completed);
        Assert.Equal(Measure(smallMaximum, 2, 1, 2, 1m), statistics.Best);
        Assert.Equal(Measure(largerLowRoll, 4, 2, 12, 0.2m), statistics.Worst);
    }

    [Fact]
    public async Task Admin_hour_correction_changes_the_final_dice_and_the_luck_for_the_same_run()
    {
        using var player = await _site.SignedInAsync("vasya");
        using var admin = await _site.SignedInAsync("admin");
        var completed = await CompleteAsync(player, "normal", 3, 4);
        var runId = completed.GetProperty("id").GetGuid();

        _random.Set(1);
        await PostOkAsync(admin, $"{AdminSeason}/runs/{runId}/hours",
            new { commandId = Guid.NewGuid(), hours = 6, comment = "Уточнили часы игры" });
        Assert.Equal(0, _random.Remaining);

        var statistics = await ReadStatisticsAsync(player);
        var finalRoll = new RollMeasure(runId, 5, 2, 8, 0.5m);
        Assert.Equal(finalRoll, statistics.Best);
        Assert.Equal(finalRoll, statistics.Worst);
    }

    [Fact]
    public async Task Coop_credits_only_the_roller_with_one_roll_including_challenge_and_added_dice()
    {
        await CoopPoolAsync(_site);
        await RulesAsync(_site, Flags(features => features with { Coop = true, Items = true, Challenges = true }));
        using var roller = await _site.SignedInAsync("vasya");
        using var partner = await _site.SignedInAsync("petya");
        using var admin = await _site.SignedInAsync("admin");

        var extraDie = EconomyApiTests.RepositoryPack()["objects"]!.AsArray()
            .Single(item => item!["id"]!.GetValue<string>() == "extra-die")!.DeepClone();
        var pack = new JsonObject { ["objects"] = new JsonArray(extraDie) };
        await PostOkAsync(admin, $"{AdminSeason}/content/publish", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = 0,
            pack,
            comment = "Предмет для проверки броска",
        });
        await StartAsync(roller);
        await PostOkAsync(roller, $"{Season}/coop/invite",
            new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"] });
        var invitations = await ReadAsync(partner, $"{Season}/interactions");
        var invitation = Assert.Single(invitations.EnumerateArray()).GetProperty("id").GetGuid();
        await PostOkAsync(partner, $"{Season}/interactions/{invitation}/answer",
            new { commandId = Guid.NewGuid(), accept = true });
        await PostOkAsync(admin, $"{AdminSeason}/players/{_site.Players["vasya"]}/inventory",
            new { commandId = Guid.NewGuid(), giveObjectId = "extra-die", comment = "Лишний кубик" });
        var economy = await ReadAsync(roller, $"{Season}/economy");
        var itemId = Assert.Single(economy.GetProperty("items").EnumerateArray()).GetProperty("instanceId").GetGuid();
        await PostOkAsync(roller, $"{Season}/items/{itemId}/use");

        var completed = await FinishAsync(roller, "normal", 6, challengeDone: true, [1, 2, 3, 4]);
        Assert.Equal(3, completed.GetProperty("dice").GetArrayLength());
        Assert.Single(completed.GetProperty("challengeDice").EnumerateArray());
        var own = await ReadStatisticsAsync(roller);
        var joined = await ReadStatisticsAsync(partner);
        var roll = Measure(completed, 10, 4, 16, 0.5m);
        Assert.Equal((1, roll, roll), own);
        Assert.Equal(1, joined.Completed);
        Assert.Null(joined.Best);
        Assert.Null(joined.Worst);
    }

    [Fact]
    public async Task A_completion_without_dice_does_not_create_a_roll_statistic()
    {
        using var player = await _site.SignedInAsync("vasya");
        using var admin = await _site.SignedInAsync("admin");
        await EventSeason.SetUpAsync(admin, EventSeason.NotEvenSweating);
        await EventSeason.DropAsync(player);

        var completed = await CompleteAsync(player, "normal", 3);
        Assert.Empty(completed.GetProperty("dice").EnumerateArray());

        var statistics = await ReadStatisticsAsync(player);
        Assert.Equal(1, statistics.Completed);
        Assert.Null(statistics.Best);
        Assert.Null(statistics.Worst);
    }

    [Fact]
    public async Task A_frozen_first_players_free_mode_dice_still_count()
    {
        using var player = await _site.SignedInAsync("vasya");
        using var admin = await _site.SignedInAsync("admin");
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        await _site.SendAsync(new AdjustPlayer(_site.Players["vasya"], "Перенос перед финишем", CellId: beforeFinish));
        var finish = await CompleteAsync(player, "normal", 3, 1);
        await PostOkAsync(admin, $"{AdminSeason}/runs/{finish.GetProperty("id").GetGuid()}/approve",
            new { commandId = Guid.NewGuid(), comment = "Финиш подтверждён" });
        var me = (await ReadAsync(player, Season)).GetProperty("me");
        Assert.True(me.GetProperty("finish").GetProperty("frozen").GetBoolean());

        var freeRun = await CompleteAsync(player, "normal", 3, 4);
        var statistics = await ReadStatisticsAsync(player);
        Assert.Equal(Measure(freeRun, 4, 1, 4, 1m), statistics.Best);
        Assert.Equal(Measure(finish, 1, 1, 4, 0m), statistics.Worst);
    }

    private static async Task StartAsync(HttpClient player)
    {
        await PostOkAsync(player, $"{Season}/roll");
        await PostOkAsync(player, $"{Season}/start");
    }

    private async Task<JsonElement> CompleteAsync(HttpClient player, string difficulty, decimal estimatedHours, params int[] dice)
    {
        await StartAsync(player);
        return await FinishAsync(player, difficulty, estimatedHours, challengeDone: false, dice);
    }

    private async Task<JsonElement> FinishAsync(HttpClient player, string difficulty, decimal estimatedHours, bool challengeDone, int[] dice)
    {
        _random.Set(dice);
        await PostOkAsync(player, $"{Season}/complete",
            new { commandId = Guid.NewGuid(), difficulty, estimatedHours, hoursSource = HoursSource, challengeDone });
        Assert.Equal(0, _random.Remaining);
        var completed = (await ReadAsync(player, Season)).GetProperty("me").GetProperty("lastCompleted");
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        return completed;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient player, string url)
    {
        using var response = await player.GetAsync(url, Ct);
        return await OkAsync(response);
    }

    private static async Task<(int Completed, RollMeasure? Best, RollMeasure? Worst)> ReadStatisticsAsync(HttpClient player)
    {
        var recap = await ReadAsync(player, $"{Season}/my-statistics");
        return (recap.GetProperty("completedRuns").GetInt32(), ReadRoll(recap, "bestRoll"), ReadRoll(recap, "worstRoll"));
    }

    private static RollMeasure? ReadRoll(JsonElement recap, string property)
    {
        var roll = recap.GetProperty(property);
        return roll.ValueKind == JsonValueKind.Null
            ? null
            : new RollMeasure(roll.GetProperty("runId").GetGuid(), roll.GetProperty("sum").GetInt32(),
                roll.GetProperty("minimum").GetInt32(), roll.GetProperty("maximum").GetInt32(), roll.GetProperty("luck").GetDecimal());
    }

    private static RollMeasure Measure(JsonElement run, int sum, int minimum, int maximum, decimal luck) =>
        new(run.GetProperty("id").GetGuid(), sum, minimum, maximum, luck);

    private sealed record RollMeasure(Guid RunId, int Sum, int Minimum, int Maximum, decimal Luck);

    private sealed class DiceScript : IRandomSource
    {
        private readonly Queue.SeededRandom _fallback = new(11);
        private Queue<int> _faces = new();

        public int Remaining => _faces.Count;

        public void Set(params int[] faces) => _faces = new Queue<int>(faces);

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (!_faces.TryDequeue(out var face))
            {
                return _fallback.NextInt(minInclusive, maxExclusive);
            }

            Assert.InRange(face, minInclusive, maxExclusive - 1);
            return face;
        }
    }
}
