using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// Commands go through one queue; one command is one transaction: log events and projection rows
/// are written together or not at all (CLAUDE.md invariants 2 and 3, TEST_MATRIX L1, L4, L7).
/// </summary>
public class CommandQueueTests
{
    private static readonly Guid s_season = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid s_vasya = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Command_through_the_queue_writes_log_and_projection_that_agree()
    {
        await using var h = await QueueHarness.StartAsync();

        // When a season is played through the queue
        await AcceptedAsync(h, new CreateSeason(s_season, "Тестовый сезон", RulesetJson.Default()));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));
        await AcceptedAsync(h, new RollGame(s_vasya));
        await AcceptedAsync(h, new StartRun(s_vasya));
        await AcceptedAsync(h, new CompleteRun(s_vasya, Difficulty.Normal));

        // Then the log is contiguous and the projection equals the fold of the log (L4)
        await using var db = h.NewDb();
        var sequences = await db.Events.Where(e => e.SeasonId == s_season).OrderBy(e => e.Sequence).Select(e => e.Sequence).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(1, sequences.Count).Select(i => (long)i), sequences);

        var (replayed, _) = await EventLogReader.ReplaySeasonAsync(db, s_season, TestContext.Current.CancellationToken);
        Assert.Equal(replayed, await SeasonProjection.ReadAsync(db, replayed, TestContext.Current.CancellationToken));

        var player = await db.SeasonPlayers.SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(player.Points > 0);
        Assert.Equal(TurnPhase.Idle, player.Phase);
        Assert.Equal(RunStatus.Completed, (await db.Runs.SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Single(await db.Seasons.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Crash_in_the_middle_of_a_command_leaves_nothing_behind()
    {
        var fault = new FailingSaveInterceptor();
        await using var h = await QueueHarness.StartAsync(fault);
        await AcceptedAsync(h, new CreateSeason(s_season, "Тестовый сезон", RulesetJson.Default()));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));
        await AcceptedAsync(h, new RollGame(s_vasya));
        await AcceptedAsync(h, new StartRun(s_vasya));
        var eventsBefore = await CountEventsAsync(h);

        // When completion crashes after writing but before commit
        fault.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.SendAsync(new CompleteRun(s_vasya, Difficulty.Normal), s_season));
        fault.Armed = false;

        // Then no event and no projection change survived
        Assert.Equal(eventsBefore, await CountEventsAsync(h));
        await using (var db = h.NewDb())
        {
            var player = await db.SeasonPlayers.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, player.Points);
            Assert.Equal(TurnPhase.Playing, player.Phase);
            Assert.Equal(RunStatus.Playing, (await db.Runs.SingleAsync(TestContext.Current.CancellationToken)).Status);
        }

        // And the queue keeps working from the committed state: the retry succeeds and the log stays contiguous
        await AcceptedAsync(h, new CompleteRun(s_vasya, Difficulty.Normal));
        await using var after = h.NewDb();
        var sequences = await after.Events.OrderBy(e => e.Sequence).Select(e => e.Sequence).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(1, sequences.Count).Select(i => (long)i), sequences);
        Assert.Equal(TurnPhase.Idle, (await after.SeasonPlayers.SingleAsync(TestContext.Current.CancellationToken)).Phase);
    }

    [Fact]
    public async Task Rejected_command_writes_nothing()
    {
        await using var h = await QueueHarness.StartAsync();
        await AcceptedAsync(h, new CreateSeason(s_season, "Тестовый сезон", RulesetJson.Default()));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));
        var before = await CountEventsAsync(h);

        var outcome = await h.SendAsync(new StartRun(s_vasya), s_season);

        Assert.False(outcome.IsAccepted);
        Assert.Equal(RejectionCodes.WrongPhase, outcome.Rejection!.Code);
        Assert.Empty(outcome.Events);
        Assert.Equal(before, await CountEventsAsync(h));
    }

    [Fact]
    public async Task Parallel_commands_run_one_at_a_time_without_losses()
    {
        await using var h = await QueueHarness.StartAsync();
        await AcceptedAsync(h, new CreateSeason(s_season, "Тестовый сезон", RulesetJson.Default()));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));

        // When 50 players are added concurrently
        var players = Enumerable.Range(1, 50).Select(i => Guid.Parse($"10000000-0000-0000-0000-{i:x12}")).ToList();
        var outcomes = await Task.WhenAll(players.Select(p => h.SendAsync(new AddSeasonPlayer(p, p, $"Игрок {p}"), s_season)));

        // Then every command is applied exactly once, in some order, with a gapless log
        Assert.All(outcomes, o => Assert.True(o.IsAccepted));
        await using var db = h.NewDb();
        Assert.Equal(50, await db.SeasonPlayers.CountAsync(TestContext.Current.CancellationToken));
        var sequences = await db.Events.OrderBy(e => e.Sequence).Select(e => e.Sequence).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(1, 52).Select(i => (long)i), sequences); // created, started, 50 players
    }

    [Fact]
    public async Task Same_command_id_twice_acts_once()
    {
        await using var h = await QueueHarness.StartAsync();
        await AcceptedAsync(h, new CreateSeason(s_season, "Тестовый сезон", RulesetJson.Default()));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));
        var commandId = Guid.NewGuid();

        // When a double click sends the same roll twice
        var first = await h.SendAsync(new RollGame(s_vasya), s_season, commandId);
        var second = await h.SendAsync(new RollGame(s_vasya), s_season, commandId);

        // Then the second returns the first's events and nothing new is written (L7)
        Assert.True(first.IsAccepted);
        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Events.Select(e => e.Sequence), second.Events.Select(e => e.Sequence));
        Assert.Equal(first.Events.Select(e => e.Event), second.Events.Select(e => e.Event));
        await using var db = h.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.CommandId == commandId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task After_restart_the_season_is_rebuilt_from_the_log()
    {
        await using var h = await QueueHarness.StartAsync();
        await AcceptedAsync(h, new CreateSeason(s_season, "Тестовый сезон", RulesetJson.Default()));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));
        await AcceptedAsync(h, new RollGame(s_vasya));

        // When the server restarts (in-memory state is lost)
        await h.RestartAsync();

        // Then the next command continues from the logged state and numbering
        var outcome = await AcceptedAsync(h, new StartRun(s_vasya));
        Assert.Equal(5, outcome.Events.Single().Sequence); // created, started, player, roll, start
    }

    [Fact]
    public async Task Database_runs_in_wal_mode()
    {
        await using var h = await QueueHarness.StartAsync();
        await using var db = h.NewDb();
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<Infrastructure.Queue.CommandOutcome> AcceptedAsync(QueueHarness h, ICommand command)
    {
        var outcome = await h.SendAsync(command, s_season);
        Assert.True(outcome.IsAccepted, $"{command} was rejected: {outcome.Rejection}");
        return outcome;
    }

    private static async Task<int> CountEventsAsync(QueueHarness h)
    {
        await using var db = h.NewDb();
        return await db.Events.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Projection_of_season_and_player_administration_equals_the_fold_of_the_log()
    {
        await using var h = await QueueHarness.StartAsync();
        var petya = Guid.Parse("10000000-0000-0000-0000-000000000002");

        // A season with every administration command of C2
        await AcceptedAsync(h, new CreateSeason(s_season, "Осень", RulesetJson.Default(), new DateTimeOffset(2026, 10, 20, 21, 0, 0, TimeSpan.Zero)));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));
        await AcceptedAsync(h, new AddSeasonPlayer(petya, petya, "Петя", CellId: "c3", Points: 5, Coins: 2));
        await AcceptedAsync(h, new SetSeasonDeadline(new DateTimeOffset(2026, 10, 25, 21, 0, 0, TimeSpan.Zero)));
        await AcceptedAsync(h, new AdjustPlayer(petya, "Бонус за стрим", CoinsDelta: 7, ResourceDeltas: [new ResourceDelta("tickets", 3), new ResourceDelta("stars", 1)]));
        await AcceptedAsync(h, new AdjustPlayer(petya, "Билеты сгорели", ResourceDeltas: [new ResourceDelta("tickets", -3)]));
        await AcceptedAsync(h, new SetPlayerInactive(petya, true));
        await AcceptedAsync(h, new RollGame(s_vasya));
        await AcceptedAsync(h, new AdjustPlayer(s_vasya, "Сброс по просьбе", DiscardOffer: true));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Closing));

        // Then every projected column equals the fold of the log (L4)
        await using var db = h.NewDb();
        var (replayed, _) = await EventLogReader.ReplaySeasonAsync(db, s_season, TestContext.Current.CancellationToken);
        Assert.Equal(replayed, await SeasonProjection.ReadAsync(db, replayed, TestContext.Current.CancellationToken));
        var row = await db.SeasonPlayers.SingleAsync(p => p.Id == petya, TestContext.Current.CancellationToken);
        Assert.Equal((9, true, "c3", 5), (row.Coins, row.IsInactive, row.CellId, row.Points));
        Assert.Equal("""{"stars":1}""", row.ResourcesJson);
        Assert.Equal("""{"segments":[{"cells":["start"]},{"cells":["c3"]}]}""", row.PathJson);
        var season = await db.Seasons.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((SeasonStatus.Closing, "Осень"), (season.Status, season.Name));
    }
}

