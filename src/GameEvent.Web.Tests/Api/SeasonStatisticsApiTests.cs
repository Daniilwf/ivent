using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>Personal season recap reads the caller's participation, including split coop completions (SPEC «Итоги сезона», D-716, D-793).</summary>
public sealed class SeasonStatisticsApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid SeasonId => SiteFactory.SeasonId;
    private static string Season => $"/api/seasons/{SeasonId}";
    private static string AdminSeason => $"/api/admin/seasons/{SeasonId}";
    private static string Statistics(Guid seasonId) => $"/api/seasons/{seasonId}/my-statistics";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();
    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task An_anonymous_reader_cannot_open_the_enabled_summary()
    {
        await EnableStatisticsAsync(SeasonId);
        using var anonymous = await _site.AnonymousAsync();

        using var response = await anonymous.GetAsync(Statistics(SeasonId), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_summary_is_available_only_to_the_seasons_own_participants()
    {
        var otherSeason = await _site.CreateSeasonAsync();
        await EnableStatisticsAsync(SeasonId);
        await EnableStatisticsAsync(otherSeason);

        foreach (var login in new[] { "zritel", "masha", "admin" })
        {
            using var nonParticipant = await _site.SignedInAsync(login);
            using var forbidden = await nonParticipant.GetAsync(Statistics(SeasonId), Ct);
            Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        }

        using var participant = await _site.SignedInAsync("vasya");
        using var unrelated = await participant.GetAsync(Statistics(otherSeason), Ct);
        using var missing = await participant.GetAsync(Statistics(Guid.NewGuid()), Ct);
        using var malformed = await participant.GetAsync("/api/seasons/not-a-guid/my-statistics", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unrelated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);

        Assert.Equal((SeasonId, 0, null, null), await ReadStatisticsAsync(participant));
    }

    [Fact]
    public async Task The_feature_is_hidden_by_default_and_available_when_enabled()
    {
        using var participant = await _site.SignedInAsync("vasya");
        using var disabled = await participant.GetAsync(Statistics(SeasonId), Ct);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        await EnableStatisticsAsync(SeasonId);

        Assert.Equal((SeasonId, 0, null, null), await ReadStatisticsAsync(participant));
    }

    [Fact]
    public async Task The_profile_offers_recaps_only_to_the_owner_for_enabled_seasons()
    {
        using var participant = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");
        using var admin = await _site.SignedInAsync("admin");
        var profilePath = $"/api/users/{_site.Users["vasya"]}";
        var otherSeason = await _site.CreateSeasonAsync();
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var joined = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), otherSeason,
            new AddSeasonPlayer(Guid.NewGuid(), _site.Users["vasya"], "Vasya"), AuthorId: null), Ct);
        Assert.True(joined.IsAccepted, joined.Rejection?.Detail);

        var initial = await ProfileSeasonAsync(participant, profilePath, SeasonId);
        Assert.False(initial.TryGetProperty("seasonRecapAvailable", out _));

        await EnableStatisticsAsync(SeasonId);
        var offered = await ProfileSeasonAsync(participant, profilePath, SeasonId);
        Assert.True(offered.GetProperty("seasonRecapAvailable").GetBoolean());
        var disabled = await ProfileSeasonAsync(participant, profilePath, otherSeason);
        Assert.False(disabled.TryGetProperty("seasonRecapAvailable", out _));

        foreach (var viewer in new[] { spectator, admin })
        {
            var foreign = await ProfileSeasonAsync(viewer, profilePath, SeasonId);
            Assert.False(foreign.TryGetProperty("seasonRecapAvailable", out _));
        }

        var recap = await ReadJsonAsync(participant, Statistics(SeasonId));
        Assert.Equal(offered.GetProperty("seasonName").GetString(), recap.GetProperty("seasonName").GetString());
    }

    private static async Task<JsonElement> ProfileSeasonAsync(HttpClient viewer, string path, Guid seasonId)
    {
        using var response = await viewer.GetAsync(path, Ct);
        var profile = await OkAsync(response);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return Assert.Single(profile.GetProperty("seasons").EnumerateArray(), season => season.GetProperty("seasonId").GetGuid() == seasonId);
    }

    [Fact]
    public async Task Saved_rules_with_a_retired_field_preserve_member_access()
    {
        await EnableStatisticsAsync(SeasonId);
        await using (var db = _site.NewDb())
        {
            var season = await db.Seasons.SingleAsync(row => row.Id == SeasonId, Ct);
            var oldRules = JsonNode.Parse(season.RulesetJson)!.AsObject();
            oldRules["retiredSeasonSetting"] = true;
            season.RulesetJson = oldRules.ToJsonString();
            await db.SaveChangesAsync(Ct);
        }

        using var participant = await _site.SignedInAsync("vasya");
        Assert.Equal((SeasonId, 0, null, null), await ReadStatisticsAsync(participant));
    }

    [Fact]
    public async Task Only_the_callers_latest_ratings_for_their_completed_runs_set_the_extremes()
    {
        using var vasya = await _site.SignedInAsync("vasya");
        using var petya = await _site.SignedInAsync("petya");
        await EnableStatisticsAsync(SeasonId);
        var edited = await CompleteAsync(vasya);
        Assert.Equal((SeasonId, 1, null, null), await ReadStatisticsAsync(vasya));
        await ReviewAsync(vasya, edited, 1);
        await ReviewAsync(vasya, await CompleteAsync(vasya), 3);
        await ReviewAsync(vasya, edited, 8);
        await ReviewAsync(petya, await CompleteAsync(petya), 10);
        Assert.Equal((SeasonId, 2, 8, 3), await ReadStatisticsAsync(vasya));
        Assert.Equal((SeasonId, 1, 10, 10), await ReadStatisticsAsync(petya));
    }

    [Fact]
    public async Task Playing_dropped_and_rejected_runs_cannot_raise_the_completion_count_or_rating()
    {
        using var vasya = await _site.SignedInAsync("vasya");
        using var petya = await _site.SignedInAsync("petya");
        using var admin = await _site.SignedInAsync("admin");
        await ReviewAsync(vasya, await CompleteAsync(vasya), 4);
        var rejected = await CompleteAsync(vasya);
        await ReviewAsync(vasya, rejected, 9);
        await RejectAsync(admin, rejected);
        await StartAsync(vasya);
        await PostOkAsync(vasya, $"{Season}/drop");
        await StartAsync(petya);
        await EnableStatisticsAsync(SeasonId);

        Assert.Equal((SeasonId, 1, 4, 4), await ReadStatisticsAsync(vasya));
        Assert.Equal((SeasonId, 0, null, null), await ReadStatisticsAsync(petya));
    }

    [Fact]
    public async Task Both_coop_participants_count_the_run_but_only_the_author_has_its_rating()
    {
        await using (var db = _site.NewDb())
        {
            await db.Games.ExecuteUpdateAsync(games => games.SetProperty(game => game.IsCoop, true), Ct);
        }
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Features = rules.Features with { Coop = true } }));
        using var roller = await _site.SignedInAsync("vasya");
        using var partner = await _site.SignedInAsync("petya");
        using var admin = await _site.SignedInAsync("admin");

        await StartAsync(roller);
        await JoinCoopAsync(roller, partner);
        var coopRun = await FinishAsync(roller);
        await ReviewAsync(roller, coopRun, 7);
        await EnableStatisticsAsync(SeasonId);

        Assert.Equal((SeasonId, 1, 7, 7), await ReadStatisticsAsync(roller));
        Assert.Equal((SeasonId, 1, null, null), await ReadStatisticsAsync(partner));

        await RejectAsync(admin, coopRun);
        Assert.Equal((SeasonId, 0, null, null), await ReadStatisticsAsync(roller));
        Assert.Equal((SeasonId, 0, null, null), await ReadStatisticsAsync(partner));
    }

    [Fact]
    public async Task Both_players_get_full_coop_hours_using_a_run_correction_until_the_proof_is_rejected()
    {
        const decimal CoopHours = 8.25m;
        const decimal CorrectedHours = 10.75m;
        const decimal SoloHours = 4.5m;
        await SetUpCoopHoursAsync(CoopHours);
        using var roller = await _site.SignedInAsync("vasya");
        using var partner = await _site.SignedInAsync("petya");
        using var admin = await _site.SignedInAsync("admin");

        await StartAsync(roller);
        await JoinCoopAsync(roller, partner);
        var coopRun = await FinishAsync(roller);
        Assert.Equal((1, CoopHours), await ReadHoursAsync(roller));
        Assert.Equal((1, CoopHours), await ReadHoursAsync(partner));

        await PostOkAsync(admin, $"{AdminSeason}/runs/{coopRun}/hours", new
        {
            commandId = Guid.NewGuid(),
            hours = CorrectedHours,
            comment = "Уточнили длину прохождения",
        });
        await using (var db = _site.NewDb())
        {
            await db.Games.ExecuteUpdateAsync(games => games.SetProperty(game => game.Hours, SoloHours), Ct);
        }
        await CompleteAsync(roller);
        Assert.Equal((2, CorrectedHours + SoloHours), await ReadHoursAsync(roller));
        Assert.Equal((1, CorrectedHours), await ReadHoursAsync(partner));

        await RejectAsync(admin, coopRun);
        Assert.Equal((1, SoloHours), await ReadHoursAsync(roller));
        Assert.Equal((0, 0m), await ReadHoursAsync(partner));
    }

    [Fact]
    public async Task Leaving_coop_and_playing_or_dropping_other_games_add_no_hours()
    {
        const decimal GameHours = 8.25m;
        await SetUpCoopHoursAsync(GameHours);
        using var roller = await _site.SignedInAsync("vasya");
        using var partner = await _site.SignedInAsync("petya");

        await StartAsync(roller);
        await JoinCoopAsync(roller, partner);
        await PostOkAsync(partner, $"{Season}/coop/leave", new { commandId = Guid.NewGuid() });
        await FinishAsync(roller);

        await StartAsync(partner);
        await PostOkAsync(partner, $"{Season}/drop");
        await StartAsync(partner);
        Assert.Equal((1, GameHours), await ReadHoursAsync(roller));
        Assert.Equal((0, 0m), await ReadHoursAsync(partner));
    }

    [Fact]
    public async Task A_former_player_can_read_their_archived_summary_after_becoming_a_spectator()
    {
        using var vasya = await _site.SignedInAsync("vasya");
        using var admin = await _site.SignedInAsync("admin");
        var run = await CompleteAsync(vasya);
        await ReviewAsync(vasya, run, 6);
        await EnableStatisticsAsync(SeasonId);
        await PostOkAsync(admin, $"{AdminSeason}/status", new { commandId = Guid.NewGuid(), to = "closing" });
        await PostOkAsync(admin, $"{AdminSeason}/runs/{run}/approve", new { commandId = Guid.NewGuid(), comment = "Видел прохождение" });
        await PostOkAsync(admin, $"{AdminSeason}/status", new { commandId = Guid.NewGuid(), to = "finished" });
        await PostOkAsync(admin, $"{AdminSeason}/status", new { commandId = Guid.NewGuid(), to = "archived" });
        await PostOkAsync(admin, $"/api/admin/accounts/{_site.Users["vasya"]}",
            new { commandId = Guid.NewGuid(), name = "Василий", role = "spectator" });
        using var historicalParticipant = await _site.SignedInAsync("vasya");
        using var spectator = await _site.SignedInAsync("zritel");

        using var forbidden = await spectator.GetAsync(Statistics(SeasonId), Ct);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal((SeasonId, 1, 6, 6), await ReadStatisticsAsync(historicalParticipant));
        var archived = await ProfileSeasonAsync(historicalParticipant, $"/api/users/{_site.Users["vasya"]}", SeasonId);
        Assert.True(archived.GetProperty("seasonRecapAvailable").GetBoolean());
    }

    [Fact]
    public async Task Attack_counts_use_only_the_callers_season_and_an_undo_removes_the_hit_from_both_summaries()
    {
        await RulesAsync(_site, Flags(flags => flags with { Items = true, SeasonStatistics = true }));
        using var admin = await _site.SignedInAsync("admin");
        using var vasya = await _site.SignedInAsync("vasya");
        using var petya = await _site.SignedInAsync("petya");
        var attacker = _site.Players["vasya"];
        var target = _site.Players["petya"];
        var otherSeason = await _site.CreateSeasonAsync();
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var joined = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), otherSeason,
            new AddSeasonPlayer(Guid.NewGuid(), _site.Users["vasya"], "Vasya"), AuthorId: null), Ct);
        Assert.True(joined.IsAccepted, joined.Rejection?.Detail);
        await EnableStatisticsAsync(otherSeason);

        await PostOkAsync(admin, $"{AdminSeason}/content/publish", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = 0,
            pack = EconomyApiTests.RepositoryPack(),
            comment = "Test pack",
        });
        await PostOkAsync(admin, $"{AdminSeason}/players/{attacker}/inventory", new
        {
            commandId = Guid.NewGuid(),
            giveObjectId = "bird-thief",
            comment = "Test item",
        });
        await _site.SendAsync(new AdjustPlayer(target, "Test coins", CoinsDelta: 10));
        var economy = await ReadJsonAsync(vasya, $"{Season}/economy");
        var item = Assert.Single(economy.GetProperty("items").EnumerateArray()).GetProperty("instanceId").GetGuid();
        var commandId = Guid.NewGuid();
        await PostOkAsync(vasya, $"{Season}/items/{item}/use", new { commandId, targetPlayerId = target });

        Assert.Equal((1, 0), await ReadAttacksAsync(vasya, SeasonId));
        Assert.Equal((0, 1), await ReadAttacksAsync(petya, SeasonId));
        Assert.Equal((0, 0), await ReadAttacksAsync(vasya, otherSeason));
        await PostOkAsync(admin, $"{AdminSeason}/undo", new { commandId = Guid.NewGuid(), targetCommandId = commandId, comment = "Wrong target" });
        Assert.Equal((0, 0), await ReadAttacksAsync(vasya, SeasonId));
        Assert.Equal((0, 0), await ReadAttacksAsync(petya, SeasonId));
    }

    private static async Task<(int Made, int Received)> ReadAttacksAsync(HttpClient player, Guid seasonId)
    {
        using var response = await player.GetAsync(Statistics(seasonId), Ct);
        var stats = await OkAsync(response);
        var counts = (stats.GetProperty("attacksMade").GetInt32(), stats.GetProperty("attacksReceived").GetInt32());
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return counts;
    }

    private async Task EnableStatisticsAsync(Guid seasonId)
    {
        using var admin = await _site.SignedInAsync("admin");
        var current = await ReadJsonAsync(admin, $"/api/seasons/{seasonId}/rules");
        var rules = JsonNode.Parse(current.GetProperty("ruleset").GetRawText())!;
        rules["features"]!["seasonStatistics"] = true;
        using var response = await admin.PutAsJsonAsync($"/api/admin/seasons/{seasonId}/rules", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = current.GetProperty("version").GetInt32(),
            ruleset = rules,
        }, Ct);
        await OkAsync(response);
    }

    private async Task SetUpCoopHoursAsync(decimal hours)
    {
        await using (var db = _site.NewDb())
        {
            await db.Games.ExecuteUpdateAsync(games => games.SetProperty(game => game.IsCoop, true)
                .SetProperty(game => game.Hours, hours), Ct);
        }
        await RulesAsync(_site, Flags(flags => flags with { Coop = true, SeasonStatistics = true }));
    }

    private async Task JoinCoopAsync(HttpClient roller, HttpClient partner)
    {
        await PostOkAsync(roller, $"{Season}/coop/invite",
            new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"] });
        var invitation = Assert.Single((await ReadJsonAsync(partner, $"{Season}/interactions")).EnumerateArray());
        await PostOkAsync(partner, $"{Season}/interactions/{invitation.GetProperty("id").GetGuid()}/answer",
            new { commandId = Guid.NewGuid(), accept = true });
    }

    private static async Task StartAsync(HttpClient player)
    {
        await PostOkAsync(player, $"{Season}/roll");
        await PostOkAsync(player, $"{Season}/start");
    }

    private async Task<Guid> CompleteAsync(HttpClient player)
    {
        await StartAsync(player);
        return await FinishAsync(player);
    }

    private async Task<Guid> FinishAsync(HttpClient player)
    {
        await PostOkAsync(player, $"{Season}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var view = await ReadJsonAsync(player, Season);
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        return view.GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
    }

    private static Task ReviewAsync(HttpClient player, Guid runId, int rating) =>
        PostOkAsync(player, $"{Season}/runs/{runId}/review", new { commandId = Guid.NewGuid(), rating });

    private static Task RejectAsync(HttpClient admin, Guid runId) =>
        PostOkAsync(admin, $"{AdminSeason}/runs/{runId}/reject", new { commandId = Guid.NewGuid(), comment = "На скрине другая игра" });

    private static async Task<JsonElement> ReadJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        return await OkAsync(response);
    }

    private static async Task<(Guid SeasonId, int CompletedRuns, int? BestRating, int? WorstRating)> ReadStatisticsAsync(HttpClient player)
    {
        var stats = await ReadJsonAsync(player, Statistics(SeasonId));
        return (stats.GetProperty("seasonId").GetGuid(), stats.GetProperty("completedRuns").GetInt32(),
            Rating(stats, "bestRating"), Rating(stats, "worstRating"));
    }

    private static async Task<(int CompletedRuns, decimal TotalHours)> ReadHoursAsync(HttpClient player)
    {
        var stats = await ReadJsonAsync(player, Statistics(SeasonId));
        Assert.True(stats.TryGetProperty("totalHours", out var hours), "The personal season summary must contain totalHours.");
        return (stats.GetProperty("completedRuns").GetInt32(), hours.GetDecimal());
    }

    private static int? Rating(JsonElement body, string name)
    {
        var value = body.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
    }
}
