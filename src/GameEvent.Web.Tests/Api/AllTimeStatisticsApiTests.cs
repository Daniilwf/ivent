using System.Net;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

/// <summary>The owner's all-time recap spans their season memberships, independently of the per-season recap flag.</summary>
public sealed class AllTimeStatisticsApiTests : IAsyncLifetime
{
    private const string Statistics = "/api/me/statistics";
    private const string HoursSource = "https://howlongtobeat.com/game/2231";
    private readonly CompletionDice _random = new();
    private readonly SiteFactory _site;

    public AllTimeStatisticsApiTests() => _site = new() { Random = _random };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        await using var db = _site.NewDb();
        await db.Games.ExecuteUpdateAsync(games => games.SetProperty(game => game.IsCoop, true)
            .SetProperty(game => game.Hours, (decimal?)null), Ct);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_current_and_flag_off_archived_seasons_count_and_a_rejected_coop_run_is_removed_for_both()
    {
        using var roller = await _site.SignedInAsync("vasya");
        using var partner = await _site.SignedInAsync("petya");
        using var admin = await _site.SignedInAsync("admin");
        var archived = SiteFactory.SeasonId;
        await StartAsync(roller, archived);
        var archivedRun = await FinishAsync(roller, archived, 3, 1);
        await ArchiveAsync(admin, archived, archivedRun);
        using var perSeason = await roller.GetAsync($"{Season(archived)}/my-statistics", Ct);
        Assert.Equal(HttpStatusCode.NotFound, perSeason.StatusCode);

        var active = await _site.CreateSeasonAsync();
        var rules = RulesetJson.Default();
        await SendAsync(active, new ChangeRuleset(rules with
        {
            Features = rules.Features with { Coop = true, SeasonStatistics = true },
        }));
        await SetStatusAsync(admin, active, "active");
        await SendAsync(active, new AddSeasonPlayer(Guid.NewGuid(), _site.Users["vasya"], "Vasya"));
        var partnerId = Guid.NewGuid();
        await SendAsync(active, new AddSeasonPlayer(partnerId, _site.Users["petya"], "Petya"));

        await StartAsync(roller, active);
        await PostOkAsync(roller, $"{Season(active)}/coop/invite", new { commandId = Guid.NewGuid(), partnerId });
        using var invitations = await partner.GetAsync($"{Season(active)}/interactions", Ct);
        var invitation = Assert.Single((await OkAsync(invitations)).EnumerateArray()).GetProperty("id").GetGuid();
        await PostOkAsync(partner, $"{Season(active)}/interactions/{invitation}/answer",
            new { commandId = Guid.NewGuid(), accept = true });
        var coopRun = await FinishAsync(roller, active, 6, 4, 4);

        var oldRoll = new Roll(archivedRun, 1, 1, 4, 0m);
        var newRoll = new Roll(coopRun, 8, 2, 8, 1m);
        Assert.Equal(new Recap(2, 2, 0, 9m, newRoll, oldRoll), await ReadRecapAsync(roller));
        Assert.Equal(new Recap(2, 1, 0, 6m, null, null), await ReadRecapAsync(partner));

        await PostOkAsync(admin, $"{AdminSeason(active)}/runs/{coopRun}/reject",
            new { commandId = Guid.NewGuid(), comment = "Proof did not match the run" });
        Assert.Equal(new Recap(2, 1, 0, 3m, oldRoll, oldRoll), await ReadRecapAsync(roller));
        Assert.Equal(new Recap(2, 0, 0, 0m, null, null), await ReadRecapAsync(partner));
    }

    [Fact]
    public async Task A_caller_cannot_get_another_accounts_summary_even_with_a_user_id_and_their_own_drop_adds_no_hours()
    {
        using var player = await _site.SignedInAsync("vasya");
        var season = SiteFactory.SeasonId;
        await StartAsync(player, season);
        var completed = await FinishAsync(player, season, 3, 1);
        await StartAsync(player, season);
        await PostOkAsync(player, $"{Season(season)}/drop");
        await StartAsync(player, season);

        var roll = new Roll(completed, 1, 1, 4, 0m);
        Assert.Equal(new Recap(1, 1, 1, 3m, roll, roll), await ReadRecapAsync(player));

        var foreign = $"{Statistics}?userId={_site.Users["vasya"]}";
        foreach (var login in new[] { "masha", "zritel", "admin" })
        {
            using var other = await _site.SignedInAsync(login);
            Assert.Equal(new Recap(0, 0, 0, 0m, null, null), await ReadRecapAsync(other, foreign));
        }

        using var anonymous = await _site.AnonymousAsync();
        using var forbidden = await anonymous.GetAsync(foreign, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);
    }

    [Fact]
    public async Task A_former_player_can_still_read_their_own_archived_recap_as_a_spectator()
    {
        using var player = await _site.SignedInAsync("vasya");
        using var admin = await _site.SignedInAsync("admin");
        var season = SiteFactory.SeasonId;
        await StartAsync(player, season);
        var run = await FinishAsync(player, season, 3, 4);
        await ArchiveAsync(admin, season, run);
        await PostOkAsync(admin, $"/api/admin/accounts/{_site.Users["vasya"]}",
            new { commandId = Guid.NewGuid(), name = "Vasya", role = "spectator" });
        using var formerPlayer = await _site.SignedInAsync("vasya");

        var roll = new Roll(run, 4, 1, 4, 1m);
        Assert.Equal(new Recap(1, 1, 0, 3m, roll, roll), await ReadRecapAsync(formerPlayer));
    }

    private async Task SendAsync(Guid seasonId, ICommand command)
    {
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var outcome = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), seasonId, command, _site.Users["admin"]), Ct);
        Assert.True(outcome.IsAccepted, outcome.Rejection?.Detail);
    }

    private static string Season(Guid seasonId) => $"/api/seasons/{seasonId}";
    private static string AdminSeason(Guid seasonId) => $"/api/admin/seasons/{seasonId}";

    private static async Task StartAsync(HttpClient player, Guid seasonId)
    {
        await PostOkAsync(player, $"{Season(seasonId)}/roll");
        await PostOkAsync(player, $"{Season(seasonId)}/start");
    }

    private async Task<Guid> FinishAsync(HttpClient player, Guid seasonId, decimal hours, params int[] dice)
    {
        _random.Set(dice);
        await PostOkAsync(player, $"{Season(seasonId)}/complete",
            new { commandId = Guid.NewGuid(), difficulty = "normal", estimatedHours = hours, hoursSource = HoursSource });
        Assert.Equal(0, _random.Remaining);
        using var response = await player.GetAsync(Season(seasonId), Ct);
        var run = (await OkAsync(response)).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        return run;
    }

    private static async Task ArchiveAsync(HttpClient admin, Guid seasonId, Guid runId)
    {
        await SetStatusAsync(admin, seasonId, "closing");
        await PostOkAsync(admin, $"{AdminSeason(seasonId)}/runs/{runId}/approve",
            new { commandId = Guid.NewGuid(), comment = "Proof confirmed" });
        await SetStatusAsync(admin, seasonId, "finished");
        await SetStatusAsync(admin, seasonId, "archived");
    }

    private static Task SetStatusAsync(HttpClient admin, Guid seasonId, string to) =>
        PostOkAsync(admin, $"{AdminSeason(seasonId)}/status", new { commandId = Guid.NewGuid(), to });

    private static async Task<Recap> ReadRecapAsync(HttpClient client, string url = Statistics)
    {
        using var response = await client.GetAsync(url, Ct);
        var recap = await OkAsync(response);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return new Recap(recap.GetProperty("seasonCount").GetInt32(), recap.GetProperty("completedRuns").GetInt32(),
            recap.GetProperty("droppedRuns").GetInt32(), recap.GetProperty("totalHours").GetDecimal(),
            ReadRoll(recap, "bestRoll"), ReadRoll(recap, "worstRoll"));
    }

    private static Roll? ReadRoll(JsonElement recap, string name) =>
        recap.TryGetProperty(name, out var roll) && roll.ValueKind != JsonValueKind.Null
            ? new Roll(roll.GetProperty("runId").GetGuid(), roll.GetProperty("sum").GetInt64(),
                roll.GetProperty("minimum").GetInt64(), roll.GetProperty("maximum").GetInt64(),
                roll.GetProperty("luck").GetDecimal())
            : null;

    private sealed record Recap(int SeasonCount, int CompletedRuns, int DroppedRuns, decimal TotalHours, Roll? Best, Roll? Worst);
    private sealed record Roll(Guid RunId, long Sum, long Minimum, long Maximum, decimal Luck);

    private sealed class CompletionDice : IRandomSource
    {
        private readonly Queue.SeededRandom _fallback = new(11);
        private Queue<int> _dice = new();

        public int Remaining => _dice.Count;

        public void Set(int[] dice) => _dice = new Queue<int>(dice);

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (!_dice.TryDequeue(out var value))
            {
                return _fallback.NextInt(minInclusive, maxExclusive);
            }

            Assert.InRange(value, minInclusive, maxExclusive - 1);
            return value;
        }
    }
}
