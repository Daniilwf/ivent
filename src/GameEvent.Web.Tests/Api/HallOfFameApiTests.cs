using System.Net;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

public sealed class HallOfFameApiTests : IAsyncLifetime
{
    private const string Url = "/api/hall-of-fame";
    private readonly SiteFactory _site = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();
    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_session_is_required_to_read_the_hall_of_fame()
    {
        var anonymous = await _site.AnonymousAsync();
        using var response = await anonymous.GetAsync(Url, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_spectator_without_participation_sees_no_live_or_closing_leaders()
    {
        await _site.SendAsync(new AdjustPlayer(_site.Players["vasya"], "Current leader", PointsDelta: 40));
        var closing = Guid.NewGuid();
        await CreateSeasonAsync(closing, "Closing season");
        await SendAsync(closing, new AddSeasonPlayer(Guid.NewGuid(), _site.Users["masha"], "Closing leader", Points: 75));
        await SendAsync(closing, new ChangeSeasonStatus(SeasonStatus.Closing));

        var spectator = await _site.SignedInAsync("zritel");
        Assert.Empty(await ReadAsync(spectator));
    }

    [Fact]
    public async Task A_finished_season_shows_every_saved_first_place_in_row_order_with_saved_points()
    {
        var seasonId = Guid.NewGuid();
        var first = Guid.Parse("17200000-0000-0000-0000-000000000001");
        var second = Guid.Parse("17200000-0000-0000-0000-000000000002");
        var third = Guid.Parse("17200000-0000-0000-0000-000000000003");
        var rules = RulesetJson.Default() with { Ranking = new RankingRules { Tiebreakers = [] } };
        await CreateSeasonAsync(seasonId, "Shared first", rules);
        await SendAsync(seasonId, new AddSeasonPlayer(second, _site.Users["petya"], "Anna", Points: 17));
        await SendAsync(seasonId, new AddSeasonPlayer(first, _site.Users["vasya"], "Zoya", Points: 17));
        await SendAsync(seasonId, new AddSeasonPlayer(third, _site.Users["masha"], "Third", Points: 6));
        await FinishAsync(seasonId);

        await using (var db = _site.NewDb())
        {
            var saved = await db.SeasonResults.AsNoTracking().Where(row => row.SeasonId == seasonId)
                .OrderBy(row => row.Row).Select(row => new { row.PlayerId, row.Place, row.Points }).ToArrayAsync(Ct);
            Assert.Equal([(first, 1, 17), (second, 1, 17), (third, 3, 6)],
                saved.Select(row => (row.PlayerId, row.Place, row.Points)));

            // Diverge the live projection from the frozen result to prove that the read uses the saved points.
            await db.SeasonPlayers.Where(player => player.SeasonId == seasonId)
                .ExecuteUpdateAsync(update => update.SetProperty(player => player.Points, player => player.Id == third ? 900 : 0), Ct);
        }

        var spectator = await _site.SignedInAsync("zritel");
        var season = Assert.Single(await ReadAsync(spectator));
        Assert.Equal((seasonId, "Shared first"), (season.SeasonId, season.SeasonName));
        Assert.Equal([new Winner(first, "Zoya", 17, _site.Users["vasya"]), new Winner(second, "Anna", 17, _site.Users["petya"])],
            season.Winners);
    }

    [Fact]
    public async Task Seasons_sort_by_creation_and_id_and_a_deleted_archived_winner_keeps_their_history()
    {
        var older = Guid.Parse("17300000-0000-0000-0000-000000000003");
        var newerFirst = Guid.Parse("17300000-0000-0000-0000-000000000001");
        var newerSecond = Guid.Parse("17300000-0000-0000-0000-000000000002");
        var retiredWinner = Guid.NewGuid();
        var retiredUser = _site.Users["masha"];
        await CreateSeasonAsync(older, "Archived history");
        await SendAsync(older, new AddSeasonPlayer(retiredWinner, retiredUser, "Season nickname", Points: 44));

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        await CreateSeasonAsync(newerSecond, "Same timestamp, second");
        await CreateSeasonAsync(newerFirst, "Same timestamp, first");
        await SendAsync(newerSecond, new AddSeasonPlayer(Guid.NewGuid(), _site.Users["petya"], "Season Petya"));
        await SendAsync(newerFirst, new AddSeasonPlayer(Guid.NewGuid(), _site.Users["vasya"], "Season Vasya"));
        await FinishAsync(newerSecond);
        await FinishAsync(newerFirst);

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        await FinishAsync(older);
        await SendAsync(older, new ChangeSeasonStatus(SeasonStatus.Archived));
        var spectator = await _site.SignedInAsync("zritel");
        var before = await ReadAsync(spectator);
        Assert.Equal([newerFirst, newerSecond, older], before.Select(season => season.SeasonId));
        Assert.Equal(new Winner(retiredWinner, "Season nickname", 44, retiredUser), Assert.Single(before[^1].Winners));

        await SendAsync(Guid.Empty, new DeleteAccount(retiredUser));
        using var profile = await spectator.GetAsync($"/api/users/{retiredUser}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, profile.StatusCode);
        var after = await ReadAsync(spectator);
        Assert.Equal([newerFirst, newerSecond, older], after.Select(season => season.SeasonId));
        Assert.Equal("Archived history", after[^1].SeasonName);
        Assert.Equal(new Winner(retiredWinner, "Season nickname", 44, null), Assert.Single(after[^1].Winners));
    }

    [Fact]
    public async Task The_confirmed_first_finisher_is_the_winner_even_with_fewer_points()
    {
        var seasonId = SiteFactory.SeasonId;
        var player = await _site.SignedInAsync("vasya");
        var admin = await _site.SignedInAsync("admin");
        var spectator = await _site.SignedInAsync("zritel");
        var first = _site.Players["vasya"];
        var higherScore = _site.Players["petya"];
        await _site.SendAsync(new AdjustPlayer(higherScore, "Points bonus", PointsDelta: 50));
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        await _site.SendAsync(new AdjustPlayer(first, "Move before finish", CellId: beforeFinish));

        var seasonRoute = $"/api/seasons/{seasonId}";
        await PostOkAsync(player, $"{seasonRoute}/roll");
        await PostOkAsync(player, $"{seasonRoute}/start");
        await PostOkAsync(player, $"{seasonRoute}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        using var live = await player.GetAsync(seasonRoute, Ct);
        var runId = (await OkAsync(live)).GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
        await PostOkAsync(admin, $"/api/admin/seasons/{seasonId}/runs/{runId}/approve",
            new { commandId = Guid.NewGuid(), comment = "Confirmed finish" });
        await FinishAsync(seasonId);

        await using var db = _site.NewDb();
        var saved = await db.SeasonResults.AsNoTracking().Where(row => row.SeasonId == seasonId)
            .OrderBy(row => row.Row).ToArrayAsync(Ct);
        Assert.Equal([(first, 1), (higherScore, 2)], saved.Select(row => (row.PlayerId, row.Place)));
        Assert.True(saved[0].Points < saved[1].Points);
        var season = Assert.Single(await ReadAsync(spectator));
        Assert.Equal(new Winner(first, "vasya", saved[0].Points, _site.Users["vasya"]), Assert.Single(season.Winners));
    }

    private async Task CreateSeasonAsync(Guid id, string name, Ruleset? rules = null)
    {
        await SendAsync(id, new CreateSeason(id, name, rules ?? RulesetJson.Default()));
        await SendAsync(id, new ChangeSeasonStatus(SeasonStatus.Active));
    }

    private async Task FinishAsync(Guid seasonId)
    {
        await SendAsync(seasonId, new ChangeSeasonStatus(SeasonStatus.Closing));
        await SendAsync(seasonId, new ChangeSeasonStatus(SeasonStatus.Finished));
    }

    private async Task SendAsync(Guid seasonId, ICommand command)
    {
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var result = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), seasonId, command, _site.Users["admin"]), Ct);
        Assert.True(result.IsAccepted, $"{command.GetType().Name}: {result.Rejection?.Detail}");
    }

    private static async Task<Season[]> ReadAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Url, Ct);
        var body = await OkAsync(response);
        return [.. body.EnumerateArray().Select(season => new Season(
            season.GetProperty("seasonId").GetGuid(),
            season.GetProperty("seasonName").GetString()!,
            [.. season.GetProperty("winners").EnumerateArray().Select(ReadWinner)]))];
    }

    private static Winner ReadWinner(JsonElement winner)
    {
        var profile = winner.GetProperty("profileUserId");
        return new Winner(winner.GetProperty("playerId").GetGuid(), winner.GetProperty("name").GetString()!,
            winner.GetProperty("points").GetInt32(), profile.ValueKind == JsonValueKind.Null ? null : profile.GetGuid());
    }

    private sealed record Season(Guid SeasonId, string SeasonName, Winner[] Winners);
    private sealed record Winner(Guid PlayerId, string Name, int Points, Guid? ProfileUserId);
}
