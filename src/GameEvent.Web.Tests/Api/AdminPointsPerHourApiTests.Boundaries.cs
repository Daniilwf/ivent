using System.Net;
using System.Text.Json;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Queue;
using Microsoft.Extensions.DependencyInjection;

namespace GameEvent.Web.Tests.Api;

public sealed partial class AdminPointsPerHourApiTests
{
    [Fact]
    public async Task A_legal_tiny_saved_length_keeps_frozen_credited_points_and_a_finite_unclamped_hour_rate()
    {
        const decimal TinyHours = 0.0000000000000000000000000001m;
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        await _site.SendAsync(new AdjustPlayer(_site.Players["vasya"], "Перед финишем", CellId: beforeFinish));
        var run = await CompleteAsync(player, hours: 6m, faces: [4, 4]);
        await ApproveAsync(admin, run.Id);
        Assert.True((await ReadAsync(player, Season(SeasonId))).GetProperty("me").GetProperty("finish").GetProperty("frozen").GetBoolean());

        // A legal admin correction may shrink the saved dice but cannot change this frozen player's awarded eight points.
        await CorrectHoursAsync(admin, run.Id, TinyHours);
        using var response = await admin.GetAsync($"{AdminSeason(SeasonId)}/statistics", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"A positive saved length must keep the report readable: {response.StatusCode}, {await response.Content.ReadAsStringAsync(Ct)}");
        var report = await ApiCalls.OkAsync(response);
        var row = report.GetProperty("players").EnumerateArray()
            .Single(candidate => candidate.GetProperty("playerId").GetGuid() == _site.Players["vasya"]);

        Assert.Equal(8L, row.GetProperty("completedRunPoints").GetInt64());
        Assert.Equal(TinyHours, row.GetProperty("completedRunHours").GetDecimal());
        var rate = row.GetProperty("pointsPerHour").GetDouble();
        Assert.True(double.IsFinite(rate), "The rate must remain a finite number, not null, infinity or an error.");
        // Relative floating-point tolerance; this excludes clamping at decimal.MaxValue or inventing a minimum length.
        const double ExpectedRate = 8e28;
        Assert.InRange(rate, ExpectedRate * (1 - 1e-12), ExpectedRate * (1 + 1e-12));
    }

    [Fact]
    public async Task Queued_domain_lengths_beyond_the_http_editor_limit_keep_a_finite_unclamped_sum_and_rate()
    {
        const decimal LargeHours = 50000000000000000000000000000m;
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var runs = await CompleteTwoBeforeFreezeAsync(admin, player);
        var queue = host.Services.GetRequiredService<CommandBus>();

        // The engine and season import accept these saved lengths; the separate HTTP editing form caps them at 1000.
        foreach (var runId in new[] { runs.First, runs.Last })
        {
            _random.Set(Enumerable.Repeat(1, 9).ToArray());
            var outcome = await queue.SendAsync(new CommandEnvelope(Guid.NewGuid(), SeasonId,
                new CorrectRunHours(runId, LargeHours, "Сохранённая доменная длина"), _site.Users["admin"]), Ct);
            Assert.True(outcome.IsAccepted, outcome.Rejection?.Detail);
            Assert.Equal(0, _random.Remaining);
        }
        Assert.Equal(4, await BalanceAsync(player, "vasya"));
        var row = await BoundaryRowAsync(admin);

        Assert.Equal(4L, row.GetProperty("completedRunPoints").GetInt64());
        AssertFiniteCloseTo(1e29, row.GetProperty("completedRunHours").GetDouble());
        AssertFiniteCloseTo(4e-29, row.GetProperty("pointsPerHour").GetDouble());
    }

    [Fact]
    public async Task The_http_editors_maximum_saved_lengths_keep_their_full_sum_and_rate()
    {
        const decimal MaximumHttpHours = 1000m;
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var runs = await CompleteTwoBeforeFreezeAsync(admin, player);

        foreach (var runId in new[] { runs.First, runs.Last })
        {
            _random.Set(Enumerable.Repeat(1, 9).ToArray());
            await CorrectHoursAsync(admin, runId, MaximumHttpHours);
            Assert.Equal(0, _random.Remaining);
        }
        var row = await BoundaryRowAsync(admin);

        Assert.Equal(4L, row.GetProperty("completedRunPoints").GetInt64());
        Assert.Equal(2000m, row.GetProperty("completedRunHours").GetDecimal());
        AssertFiniteCloseTo(0.002d, row.GetProperty("pointsPerHour").GetDouble());
    }

    [Fact]
    public async Task Ordinary_fractional_saved_lengths_are_added_exactly_before_conversion_to_a_finite_hour_rate()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var runs = await CompleteTwoBeforeFreezeAsync(admin, player);

        await CorrectHoursAsync(admin, runs.First, 0.1m);
        await CorrectHoursAsync(admin, runs.Last, 0.2m);
        var row = await BoundaryRowAsync(admin);

        Assert.Equal(4L, row.GetProperty("completedRunPoints").GetInt64());
        // Summing binary doubles first would expose 0.30000000000000004 instead of the saved decimal sum.
        Assert.Equal(0.3m, row.GetProperty("completedRunHours").GetDecimal());
        AssertFiniteCloseTo(4d / 0.3d, row.GetProperty("pointsPerHour").GetDouble());
    }

    private async Task<(Guid First, Guid Last)> CompleteTwoBeforeFreezeAsync(HttpClient admin, HttpClient player)
    {
        var first = await CompleteAsync(player, hours: 2m, faces: [2]);
        await ApproveAsync(admin, first.Id);
        Assert.Equal(2, await BalanceAsync(player, "vasya"));
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        await ApiCalls.PostOkAsync(admin, $"{AdminSeason(SeasonId)}/players/{_site.Players["vasya"]}/adjust",
            new { commandId = Guid.NewGuid(), cellId = beforeFinish, comment = "Перед финишем" });
        var last = await CompleteAsync(player, hours: 2m, faces: [2]);
        await ApproveAsync(admin, last.Id);
        Assert.True((await ReadAsync(player, Season(SeasonId))).GetProperty("me").GetProperty("finish").GetProperty("frozen").GetBoolean());
        Assert.Equal(4, await BalanceAsync(player, "vasya"));
        return (first.Id, last.Id);
    }

    private async Task<JsonElement> BoundaryRowAsync(HttpClient admin)
    {
        using var response = await admin.GetAsync($"{AdminSeason(SeasonId)}/statistics", Ct);
        var report = await ApiCalls.OkAsync(response);
        return report.GetProperty("players").EnumerateArray()
            .Single(row => row.GetProperty("playerId").GetGuid() == _site.Players["vasya"]);
    }

    private static void AssertFiniteCloseTo(double expected, double actual)
    {
        Assert.True(double.IsFinite(actual), "The true sum and rate must remain finite without clamping.");
        Assert.InRange(actual, expected * (1 - 1e-12), expected * (1 + 1e-12));
    }
}
