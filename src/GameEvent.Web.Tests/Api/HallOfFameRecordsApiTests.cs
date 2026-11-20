using System.Net;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

public sealed class HallOfFameRecordsApiTests : IAsyncLifetime
{
    private const string Url = "/api/hall-of-fame/records";
    private readonly SiteFactory _site = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();
    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task An_anonymous_visitor_cannot_read_the_records()
    {
        var anonymous = await _site.AnonymousAsync();
        using var response = await anonymous.GetAsync(Url, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_spectator_sees_no_record_when_completed_games_have_no_positive_recorded_hours()
    {
        var season = await CreateSeasonAsync(Guid.NewGuid(), "No recorded hours");
        await FinishAsync(season);
        await using (var db = _site.NewDb())
        {
            AddProjectionRun(db, season, Guid.NewGuid(), "Unknown length", RunStatus.Completed, hours: null);
            AddProjectionRun(db, season, Guid.NewGuid(), "Zero length", RunStatus.Completed, hours: 0m);
            AddProjectionRun(db, season, Guid.NewGuid(), "Negative length", RunStatus.Completed, hours: -1m);
            await db.SaveChangesAsync(Ct);
        }

        var spectator = await _site.SignedInAsync("zritel");
        Assert.Empty(await ReadAsync(spectator));
    }

    [Fact]
    public async Task A_real_archived_coop_run_is_one_record_and_uses_its_saved_hours_after_a_pool_edit()
    {
        const decimal RecordedHours = 10.25m;
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Features = rules.Features with { Coop = true } }));
        await using (var setup = _site.NewDb())
        {
            await setup.Games.ExecuteUpdateAsync(games => games.SetProperty(game => game.IsCoop, true)
                .SetProperty(game => game.Hours, RecordedHours), Ct);
        }

        var roller = await _site.SignedInAsync("vasya");
        var partner = await _site.SignedInAsync("petya");
        var admin = await _site.SignedInAsync("admin");
        var spectator = await _site.SignedInAsync("zritel");
        var seasonId = SiteFactory.SeasonId;
        var seasonRoute = $"/api/seasons/{seasonId}";
        var adminRoute = $"/api/admin/seasons/{seasonId}";
        await PostOkAsync(roller, $"{seasonRoute}/roll");
        await PostOkAsync(roller, $"{seasonRoute}/start");
        await PostOkAsync(roller, $"{seasonRoute}/coop/invite", new { commandId = Guid.NewGuid(), partnerId = _site.Players["petya"] });
        using var inbox = await partner.GetAsync($"{seasonRoute}/interactions", Ct);
        var invitation = Assert.Single((await OkAsync(inbox)).EnumerateArray()).GetProperty("id").GetGuid();
        await PostOkAsync(partner, $"{seasonRoute}/interactions/{invitation}/answer", new { commandId = Guid.NewGuid(), accept = true });
        await PostOkAsync(roller, $"{seasonRoute}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });

        await using var db = _site.NewDb();
        var saved = await db.Runs.AsNoTracking().SingleAsync(run => run.SeasonId == seasonId, Ct);
        Assert.Equal((RunStatus.Completed, _site.Players["petya"], RecordedHours), (saved.Status, saved.PartnerId, saved.Hours));
        var game = await db.Games.AsNoTracking().SingleAsync(row => row.Id == saved.GameId, Ct);

        await PostOkAsync(admin, $"{adminRoute}/status", new { commandId = Guid.NewGuid(), to = "closing" });
        await PostOkAsync(admin, $"{adminRoute}/runs/{saved.Id}/approve", new { commandId = Guid.NewGuid(), comment = "Saw the completion" });
        await PostOkAsync(admin, $"{adminRoute}/status", new { commandId = Guid.NewGuid(), to = "finished" });
        await PostOkAsync(admin, $"{adminRoute}/status", new { commandId = Guid.NewGuid(), to = "archived" });

        await db.Games.Where(row => row.Id == saved.GameId).ExecuteUpdateAsync(edit => edit.SetProperty(row => row.Hours, 91m), Ct);
        Assert.Equal(new GameLengthRecord(seasonId, "Тестовый сезон", saved.GameId, game.Title, saved.Id, RecordedHours),
            Assert.Single(await ReadAsync(spectator)));
    }

    [Fact]
    public async Task All_decimal_maximum_ties_are_ordered_and_unfinished_or_hidden_candidates_are_excluded()
    {
        var older = await CreateSeasonAsync(Id(3), "Older archive");
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        var newerSecond = await CreateSeasonAsync(Id(2), "Newer second");
        var newerFirst = await CreateSeasonAsync(Id(1), "Newer first");
        var closing = await CreateSeasonAsync(Id(4), "Still closing");
        var active = new SeasonFixture(SiteFactory.SeasonId, "Тестовый сезон", _site.Players["vasya"]);
        await FinishAsync(newerSecond);
        await FinishAsync(newerFirst);
        await SendAsync(closing.Id, new ChangeSeasonStatus(SeasonStatus.Closing));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        await FinishAsync(older);
        await SendAsync(older.Id, new ChangeSeasonStatus(SeasonStatus.Archived));

        await using var db = _site.NewDb();
        var olderTie = AddProjectionRun(db, older, Id(10), "Earlier season record", RunStatus.Completed, 10.25m);
        var secondTie = AddProjectionRun(db, newerSecond, Id(11), "Other recent season record", RunStatus.Completed, 10.250m);
        var laterRunTie = AddProjectionRun(db, newerFirst, Id(13), "Alpha record", RunStatus.Completed, 10.250m);
        var earlierRunTie = AddProjectionRun(db, newerFirst, Id(12), "Zulu record", RunStatus.Completed, 10.25m);
        AddProjectionRun(db, newerFirst, Id(9), "Shorter 9.9", RunStatus.Completed, 9.9m);
        AddProjectionRun(db, newerFirst, Id(15), "Shorter 10.2", RunStatus.Completed, 10.2m);
        AddProjectionRun(db, closing, Id(16), "Closing candidate", RunStatus.Completed, 80m);
        AddProjectionRun(db, active, Id(17), "Active candidate", RunStatus.Completed, 81m);
        AddProjectionRun(db, older, Id(18), "Rejected candidate", RunStatus.Rejected, 82m);
        AddProjectionRun(db, older, Id(19), "Dropped candidate", RunStatus.Dropped, 83m);
        AddProjectionRun(db, older, Id(20), "Hidden game", RunStatus.Completed, 84m, hiddenGame: true);
        await db.SaveChangesAsync(Ct);

        var spectator = await _site.SignedInAsync("zritel");
        Assert.Equal([earlierRunTie, laterRunTie, secondTie, olderTie], await ReadAsync(spectator));
    }

    private static Guid Id(int suffix) => new(0x17310000, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix));

    private async Task<SeasonFixture> CreateSeasonAsync(Guid id, string name)
    {
        var player = Guid.NewGuid();
        await SendAsync(id, new CreateSeason(id, name, RulesetJson.Default()));
        await SendAsync(id, new AddSeasonPlayer(player, _site.Users["vasya"], "Vasya"));
        await SendAsync(id, new ChangeSeasonStatus(SeasonStatus.Active));
        return new SeasonFixture(id, name, player);
    }

    private async Task FinishAsync(SeasonFixture season)
    {
        await SendAsync(season.Id, new ChangeSeasonStatus(SeasonStatus.Closing));
        await SendAsync(season.Id, new ChangeSeasonStatus(SeasonStatus.Finished));
    }

    private async Task SendAsync(Guid seasonId, ICommand command)
    {
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var result = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), seasonId, command, _site.Users["admin"]), Ct);
        Assert.True(result.IsAccepted, $"{command.GetType().Name}: {result.Rejection?.Detail}");
    }

    private GameLengthRecord AddProjectionRun(GameEventDbContext db, SeasonFixture season, Guid runId, string title,
        RunStatus status, decimal? hours, bool hiddenGame = false)
    {
        var gameId = Guid.NewGuid();
        db.Games.Add(new GameRecord { Id = gameId, Title = title, TagsJson = "[]", Hours = 100m, IsDeleted = hiddenGame });
        db.Runs.Add(new RunRecord
        {
            Id = runId,
            SeasonId = season.Id,
            PlayerId = season.PlayerId,
            GameId = gameId,
            Status = status,
            Hours = hours,
            RolledAt = _site.Clock.UtcNow,
            StartedAt = _site.Clock.UtcNow,
            CompletedAt = status is RunStatus.Completed or RunStatus.Rejected ? _site.Clock.UtcNow : null,
            SnapshotJson = "{}",
            DiceJson = "[]",
            ChallengeDiceJson = "[]",
        });
        return new GameLengthRecord(season.Id, season.Name, gameId, title, runId, hours.GetValueOrDefault());
    }

    private static async Task<GameLengthRecord[]> ReadAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Url, Ct);
        return [.. (await OkAsync(response)).GetProperty("longestGames").EnumerateArray().Select(game => new GameLengthRecord(
            game.GetProperty("seasonId").GetGuid(), game.GetProperty("seasonName").GetString()!,
            game.GetProperty("gameId").GetGuid(), game.GetProperty("title").GetString()!,
            game.GetProperty("runId").GetGuid(), game.GetProperty("hours").GetDecimal()))];
    }

    private sealed record SeasonFixture(Guid Id, string Name, Guid PlayerId);
    private sealed record GameLengthRecord(Guid SeasonId, string SeasonName, Guid GameId, string Title, Guid RunId, decimal Hours);
}
