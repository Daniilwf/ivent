using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>D-938: credited completion points per saved game hour, with full hours for each split-coop participant.</summary>
public sealed partial class AdminPointsPerHourApiTests : IAsyncLifetime
{
    private const string HoursSource = "https://howlongtobeat.com/game/2231";
    private readonly DiceScript _random = new();
    private readonly SiteFactory _site;

    public AdminPointsPerHourApiTests() => _site = new() { Random = _random };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid SeasonId => SiteFactory.SeasonId;
    private static string Season(Guid seasonId) => $"/api/seasons/{seasonId}";
    private static string AdminSeason(Guid seasonId) => $"/api/admin/seasons/{seasonId}";

    public async ValueTask InitializeAsync()
    {
        await _site.SeedAsync();
        // All rolled games need an explicit estimate: each scenario controls the saved length without guessing a draw.
        await using var db = _site.NewDb();
        await db.Games.ExecuteUpdateAsync(games => games.SetProperty(game => game.Hours, (decimal?)null), Ct);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_participant_without_a_credited_completion_has_no_hour_rate_instead_of_a_fake_zero()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");

        Assert.Equal(new Metric(0, 0m, null), await MetricAsync(admin, "vasya"));
        Assert.Equal(new Metric(0, 0m, null), await MetricAsync(admin, "petya"));
    }

    [Fact]
    public async Task A_pending_proof_contributes_nothing_until_the_admin_credits_its_completion()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var run = await CompleteAsync(player, hours: 3m, faces: [3]);

        Assert.Equal(new Metric(0, 0m, null), await MetricAsync(admin, "vasya"));
        await ApproveAsync(admin, run.Id);

        Assert.Equal(new Metric(3, 3m, 1m), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task The_rate_divides_total_credited_points_by_total_hours_instead_of_averaging_run_rates()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var shortRun = await CompleteAsync(player, hours: 3m, faces: [2]);
        await ApproveAsync(admin, shortRun.Id);
        var longerRun = await CompleteAsync(player, hours: 5m, faces: [1, 3]);
        await ApproveAsync(admin, longerRun.Id);

        // Two points in three hours and four in five: six in eight, not the mean of 2/3 and 4/5.
        Assert.Equal(new Metric(6, 8m, 0.75m), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task Saved_hour_corrections_include_added_dice_and_remove_the_last_extra_die_again()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var run = await CompleteAsync(player, hours: 3m, faces: [3]);
        await ApproveAsync(admin, run.Id);

        _random.Set(3);
        await CorrectHoursAsync(admin, run.Id, 6m);
        Assert.Equal(0, _random.Remaining);
        Assert.Equal(new Metric(6, 6m, 1m), await MetricAsync(admin, "vasya"));

        await CorrectHoursAsync(admin, run.Id, 3m);
        Assert.Equal(new Metric(3, 3m, 1m), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task The_difficulty_confirmed_by_the_proof_sets_the_credited_completion_points()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var run = await CompleteAsync(player, hours: 2m, faces: [4], difficulty: "hard");

        await ApproveAsync(admin, run.Id, difficulty: "easy");

        // A 4 on d6 becomes ceil(4*2/6)=2 on the proof's d2; the saved two hours do not change.
        Assert.Equal(new Metric(2, 2m, 1m), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task Rejected_and_undone_back_to_pending_proofs_add_nothing_until_the_run_is_approved()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var run = await CompleteAsync(player, hours: 3m, faces: [3]);
        var rejection = Guid.NewGuid();
        await PostOkAsync(admin, $"{AdminSeason(SeasonId)}/runs/{run.Id}/reject",
            new { commandId = rejection, comment = "Пруф оказался неверным" });

        Assert.Equal(new Metric(0, 0m, null), await MetricAsync(admin, "vasya"));
        await UndoAsync(admin, rejection);
        Assert.Equal(new Metric(0, 0m, null), await MetricAsync(admin, "vasya"));
        await ApproveAsync(admin, run.Id);

        Assert.Equal(new Metric(3, 3m, 1m), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task Undoing_approval_returns_the_run_to_pending_and_removes_its_points_and_hours()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var run = await CompleteAsync(player, hours: 3m, faces: [3]);
        var approval = await ApproveAsync(admin, run.Id);
        await UndoAsync(admin, approval);

        Assert.Equal(new Metric(0, 0m, null), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task Unrelated_balance_corrections_drop_penalties_and_an_active_run_do_not_change_completion_efficiency()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var run = await CompleteAsync(player, hours: 3m, faces: [3]);
        await ApproveAsync(admin, run.Id);
        await PostOkAsync(admin, $"{AdminSeason(SeasonId)}/players/{_site.Players["vasya"]}/adjust",
            new { commandId = Guid.NewGuid(), pointsDelta = 100, comment = "Отдельная правка баланса" });
        await StartAsync(player);
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        _random.Set(1, 1);
        await PostOkAsync(player, $"{Season(SeasonId)}/drop");
        Assert.Equal(0, _random.Remaining);
        await StartAsync(player);
        var publicState = await ReadAsync(player, Season(SeasonId));
        var actualBalance = publicState.GetProperty("players").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == _site.Players["vasya"]).GetProperty("points").GetInt32();
        Assert.NotEqual(3, actualBalance);

        Assert.Equal(new Metric(3, 3m, 1m), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task Each_split_coop_partner_gets_full_hours_and_only_their_allocated_odd_roll_points()
    {
        await CoopOnAsync(_site);
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var roller = await SignInAsync(host, "vasya");
        using var partner = await SignInAsync(host, "petya");
        await StartAsync(roller);
        await JoinCoopAsync(roller, partner);
        var run = await FinishAsync(roller, hours: 5m, faces: [4, 1]);
        await ApproveAsync(admin, run.Id);

        Assert.Equal(new Metric(3, 5m, 0.6m), await MetricAsync(admin, "vasya"));
        Assert.Equal(new Metric(2, 5m, 0.4m), await MetricAsync(admin, "petya"));
    }

    [Fact]
    public async Task A_zero_point_coop_share_still_credits_the_partners_full_hours_and_a_real_zero_rate()
    {
        await CoopOnAsync(_site);
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var roller = await SignInAsync(host, "vasya");
        using var partner = await SignInAsync(host, "petya");
        await StartAsync(roller);
        await JoinCoopAsync(roller, partner);
        var run = await FinishAsync(roller, hours: 1m, faces: [1]);
        await ApproveAsync(admin, run.Id);

        Assert.Equal(new Metric(1, 1m, 1m), await MetricAsync(admin, "vasya"));
        Assert.Equal(new Metric(0, 1m, 0m), await MetricAsync(admin, "petya"));
    }

    [Fact]
    public async Task Leaving_coop_before_completion_gives_the_former_partner_neither_points_nor_hours()
    {
        await CoopOnAsync(_site);
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var roller = await SignInAsync(host, "vasya");
        using var partner = await SignInAsync(host, "petya");
        await StartAsync(roller);
        await JoinCoopAsync(roller, partner);
        await PostOkAsync(partner, $"{Season(SeasonId)}/coop/leave");
        var run = await FinishAsync(roller, hours: 5m, faces: [4, 1]);
        await ApproveAsync(admin, run.Id);

        Assert.Equal(new Metric(5, 5m, 1m), await MetricAsync(admin, "vasya"));
        Assert.Equal(new Metric(0, 0m, null), await MetricAsync(admin, "petya"));
    }

    [Fact]
    public async Task An_approved_run_with_no_dice_has_real_hours_and_a_zero_rate_not_missing_data()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        await EventSeason.SetUpAsync(admin, EventSeason.NotEvenSweating);
        await EventSeason.DropAsync(player);
        var run = await CompleteAsync(player, hours: 3m, faces: []);
        Assert.Empty(run.View.GetProperty("dice").EnumerateArray());
        await ApproveAsync(admin, run.Id);

        Assert.Equal(new Metric(0, 3m, 0m), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task A_frozen_first_finisher_keeps_actual_awarded_points_when_saved_hours_and_dice_are_corrected()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        await _site.SendAsync(new AdjustPlayer(_site.Players["vasya"], "Перед финишем", CellId: beforeFinish));
        var run = await CompleteAsync(player, hours: 3m, faces: [3]);
        await ApproveAsync(admin, run.Id);
        Assert.True((await ReadAsync(player, Season(SeasonId))).GetProperty("me").GetProperty("finish").GetProperty("frozen").GetBoolean());

        _random.Set(3);
        await CorrectHoursAsync(admin, run.Id, 6m);
        Assert.Equal(0, _random.Remaining);

        // Freeze suppresses the additional awarded points; the corrected saved length still becomes six hours.
        Assert.Equal(new Metric(3, 6m, 0.5m), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task Free_mode_completions_do_not_inflate_a_frozen_first_finishers_rate()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        await _site.SendAsync(new AdjustPlayer(_site.Players["vasya"], "Перед финишем", CellId: beforeFinish));
        var finish = await CompleteAsync(player, hours: 3m, faces: [3]);
        await ApproveAsync(admin, finish.Id);
        var free = await CompleteAsync(player, hours: 6m, faces: [4, 4]);
        await ApproveAsync(admin, free.Id);

        Assert.Equal(new Metric(3, 3m, 1m), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task Archived_rates_stay_in_their_season_and_do_not_follow_the_account_into_a_new_draft()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        var run = await CompleteAsync(player, hours: 3m, faces: [3]);
        await ApproveAsync(admin, run.Id);
        foreach (var to in new[] { "closing", "finished", "archived" })
        {
            await PostOkAsync(admin, $"{AdminSeason(SeasonId)}/status", new { commandId = Guid.NewGuid(), to });
        }
        var other = Guid.NewGuid();
        await PostOkAsync(admin, "/api/admin/seasons", new { commandId = Guid.NewGuid(), seasonId = other, name = "Другой сезон" });
        await PostOkAsync(admin, $"{AdminSeason(other)}/players", new { commandId = Guid.NewGuid(), userId = _site.Users["vasya"] });

        Assert.Equal(new Metric(3, 3m, 1m), await MetricAsync(admin, "vasya"));
        var foreignPlayer = Assert.Single((await ReadAsync(admin, $"{AdminSeason(other)}/statistics")).GetProperty("players").EnumerateArray());
        Assert.NotEqual(_site.Players["vasya"], foreignPlayer.GetProperty("playerId").GetGuid());
        Assert.Equal(new Metric(0, 0m, null), ReadMetric(foreignPlayer));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("vasya", HttpStatusCode.Forbidden)]
    [InlineData("zritel", HttpStatusCode.Forbidden)]
    public async Task Completion_efficiency_is_available_only_to_the_real_admin(string? login, HttpStatusCode expected)
    {
        using var host = WithStatistics();
        using var client = login is null ? host.CreateClient() : await SignInAsync(host, login);

        using var response = await client.GetAsync($"{AdminSeason(SeasonId)}/statistics", Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000099")]
    public async Task An_invalid_or_missing_season_cannot_return_efficiency(string seasonId)
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");

        using var response = await admin.GetAsync($"/api/admin/seasons/{seasonId}/statistics", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_new_rate_does_not_enable_the_admin_statistics_feature_by_default()
    {
        using var admin = await _site.SignedInAsync("admin");

        using var response = await admin.GetAsync($"{AdminSeason(SeasonId)}/statistics", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private WebApplicationFactory<Program> WithStatistics() =>
        _site.WithWebHostBuilder(builder => builder.UseSetting("Site:AdminStatistics", "true"));

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> host, string login)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct);
        response.EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private static async Task StartAsync(HttpClient player)
    {
        await PostOkAsync(player, $"{Season(SeasonId)}/roll");
        await PostOkAsync(player, $"{Season(SeasonId)}/start");
    }

    private async Task<Completed> CompleteAsync(HttpClient player, decimal hours, int[] faces, string difficulty = "normal")
    {
        await StartAsync(player);
        return await FinishAsync(player, hours, faces, difficulty);
    }

    private async Task<Completed> FinishAsync(HttpClient player, decimal hours, int[] faces, string difficulty = "normal")
    {
        _random.Set(faces);
        await PostOkAsync(player, $"{Season(SeasonId)}/complete",
            new { commandId = Guid.NewGuid(), difficulty, estimatedHours = hours, hoursSource = HoursSource });
        Assert.Equal(0, _random.Remaining);
        var run = (await ReadAsync(player, Season(SeasonId))).GetProperty("me").GetProperty("lastCompleted");
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        return new Completed(run.GetProperty("id").GetGuid(), run);
    }

    private static async Task<Guid> ApproveAsync(HttpClient admin, Guid runId, string? difficulty = null)
    {
        var commandId = Guid.NewGuid();
        await PostOkAsync(admin, $"{AdminSeason(SeasonId)}/runs/{runId}/approve",
            new { commandId, difficulty, comment = "Прохождение подтверждено" });
        return commandId;
    }

    private static Task CorrectHoursAsync(HttpClient admin, Guid runId, decimal hours) =>
        PostOkAsync(admin, $"{AdminSeason(SeasonId)}/runs/{runId}/hours",
            new { commandId = Guid.NewGuid(), hours, comment = "Уточнили сохранённую длину" });

    private static Task UndoAsync(HttpClient admin, Guid targetCommandId) =>
        PostOkAsync(admin, $"{AdminSeason(SeasonId)}/undo",
            new { commandId = Guid.NewGuid(), targetCommandId, comment = "Отмена неверной модерации" });

    private async Task JoinCoopAsync(HttpClient roller, HttpClient partner)
    {
        await PostOkAsync(roller, $"{Season(SeasonId)}/coop/invite",
            new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"] });
        var invitation = Assert.Single((await ReadAsync(partner, $"{Season(SeasonId)}/interactions")).EnumerateArray());
        await PostOkAsync(partner, $"{Season(SeasonId)}/interactions/{invitation.GetProperty("id").GetGuid()}/answer",
            new { commandId = Guid.NewGuid(), accept = true });
    }

    private async Task<Metric> MetricAsync(HttpClient admin, string login)
    {
        using var response = await admin.GetAsync($"{AdminSeason(SeasonId)}/statistics", Ct);
        var report = await OkAsync(response);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return ReadMetric(report.GetProperty("players").EnumerateArray()
            .Single(player => player.GetProperty("playerId").GetGuid() == _site.Players[login]));
    }

    private static Metric ReadMetric(JsonElement player)
    {
        Assert.True(player.TryGetProperty("completedRunPoints", out var points), "The report must include actual credited completion points.");
        Assert.True(player.TryGetProperty("completedRunHours", out var hours), "The report must include saved credited game hours.");
        Assert.True(player.TryGetProperty("pointsPerHour", out var rate), "The report must distinguish no sample from a zero rate.");
        return new Metric(points.GetInt64(), hours.GetDecimal(), rate.ValueKind == JsonValueKind.Null ? null : rate.GetDecimal());
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        return await OkAsync(response);
    }

    private sealed record Completed(Guid Id, JsonElement View);
    private sealed record Metric(long Points, decimal Hours, decimal? Rate);

    private sealed class DiceScript : IRandomSource
    {
        private readonly Queue.SeededRandom _fallback = new(11);
        private Queue<int> _faces = new();
        public int Remaining => _faces.Count;
        public void Set(params int[] faces) => _faces = new Queue<int>(faces);

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (!_faces.TryDequeue(out var face)) return _fallback.NextInt(minInclusive, maxExclusive);
            Assert.InRange(face, minInclusive, maxExclusive - 1);
            return face;
        }
    }
}
