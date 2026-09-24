using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Queue;

/// <summary>Edge cases of the queue: wrong addressing, reused ids, several seasons, cancellation, shutdown.</summary>
public class CommandQueueRobustnessTests
{
    private static readonly Guid s_seasonA = Guid.Parse("30000000-0000-0000-0000-00000000000a");
    private static readonly Guid s_seasonB = Guid.Parse("30000000-0000-0000-0000-00000000000b");
    private static readonly Guid s_vasya = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid s_petya = Guid.Parse("10000000-0000-0000-0000-000000000002");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_season_sent_to_another_season_is_rejected_and_writes_nothing()
    {
        await using var h = await QueueHarness.StartAsync();

        var outcome = await h.SendAsync(new CreateSeason(s_seasonB, "Тестовый сезон", RulesetJson.Default()), s_seasonA);

        Assert.False(outcome.IsAccepted);
        Assert.Equal(RejectionCodes.SeasonMismatch, outcome.Rejection!.Code);
        await using var db = h.NewDb();
        Assert.Equal(0, await db.Events.CountAsync(Ct));
        Assert.Equal(0, await db.Seasons.CountAsync(Ct));
    }

    [Fact]
    public async Task Season_command_addressed_to_the_global_log_is_rejected()
    {
        await using var h = await QueueHarness.StartAsync();

        var outcome = await h.SendAsync(new CreateSeason(Guid.Empty, "Тестовый сезон", RulesetJson.Default()), Guid.Empty);

        Assert.Equal(RejectionCodes.SeasonMismatch, outcome.Rejection?.Code);
        await using var db = h.NewDb();
        Assert.Equal(0, await db.Events.CountAsync(Ct));
    }

    [Fact]
    public async Task Command_id_reused_by_a_different_command_is_rejected()
    {
        await using var h = await QueueHarness.StartAsync();
        await Accepted(h, new CreateSeason(s_seasonA, "Тестовый сезон", RulesetJson.Default()), s_seasonA);
        await Accepted(h, new ChangeSeasonStatus(SeasonStatus.Active), s_seasonA);
        var id = Guid.NewGuid();
        await Accepted(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"), s_seasonA, id);

        var otherCommand = await h.SendAsync(new RollGame(s_vasya), s_seasonA, id);
        var otherSeason = await h.SendAsync(new AddSeasonPlayer(s_vasya, s_vasya, "Вася"), s_seasonB, id);

        Assert.Equal(RejectionCodes.CommandIdReused, otherCommand.Rejection?.Code);
        Assert.Equal(RejectionCodes.CommandIdReused, otherSeason.Rejection?.Code);
        await using var db = h.NewDb();
        Assert.Equal(2, await db.Events.CountAsync(Ct));
    }

    [Fact]
    public async Task Command_id_reused_by_another_author_is_rejected()
    {
        await using var h = await QueueHarness.StartAsync();
        await Accepted(h, new CreateSeason(s_seasonA, "Тестовый сезон", RulesetJson.Default()), s_seasonA);
        await Accepted(h, new ChangeSeasonStatus(SeasonStatus.Active), s_seasonA);
        await Accepted(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"), s_seasonA);
        await Accepted(h, new AddSeasonPlayer(s_petya, s_petya, "Петя"), s_seasonA);
        var id = Guid.NewGuid();
        var first = await h.Bus.SendAsync(new CommandEnvelope(id, s_seasonA, new RollGame(s_vasya), s_vasya), Ct);
        Assert.True(first.IsAccepted);

        // Petya guesses Vasya's command id: he must not get Vasya's result as a "duplicate"
        var other = await h.Bus.SendAsync(new CommandEnvelope(id, s_seasonA, new RollGame(s_petya), s_petya), Ct);

        Assert.Equal(RejectionCodes.CommandIdReused, other.Rejection?.Code);
    }

    [Fact]
    public async Task Two_seasons_keep_separate_logs_numbering_and_state()
    {
        await using var h = await QueueHarness.StartAsync();
        await Accepted(h, new CreateSeason(s_seasonA, "Тестовый сезон", RulesetJson.Default()), s_seasonA);
        await Accepted(h, new ChangeSeasonStatus(SeasonStatus.Active), s_seasonA);
        await Accepted(h, new CreateSeason(s_seasonB, "Тестовый сезон", RulesetJson.Default()), s_seasonB);
        await Accepted(h, new ChangeSeasonStatus(SeasonStatus.Active), s_seasonB);
        await Accepted(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"), s_seasonA);
        await Accepted(h, new AddSeasonPlayer(s_petya, s_petya, "Петя"), s_seasonB);
        await Accepted(h, new RollGame(s_vasya), s_seasonA);

        // Vasya is not in season B: the caches do not mix
        var cross = await h.SendAsync(new RollGame(s_vasya), s_seasonB);
        Assert.Equal(RejectionCodes.PlayerUnknown, cross.Rejection?.Code);

        await using var db = h.NewDb();
        foreach (var (season, count) in new[] { (s_seasonA, 3), (s_seasonB, 2) })
        {
            var sequences = await db.Events.Where(e => e.SeasonId == season).OrderBy(e => e.Sequence).Select(e => e.Sequence).ToListAsync(Ct);
            Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), sequences);
            var (replayed, _) = await EventLogReader.ReplaySeasonAsync(db, season, Ct);
            Assert.Equal(replayed, await SeasonProjection.ReadAsync(db, replayed, Ct));
        }
    }

    [Fact]
    public async Task Command_still_runs_when_the_caller_stops_waiting_and_a_retry_is_a_duplicate()
    {
        var block = new BlockingSaveInterceptor();
        await using var h = await QueueHarness.StartAsync(block);
        await Accepted(h, new CreateSeason(s_seasonA, "Тестовый сезон", RulesetJson.Default()), s_seasonA);
        await Accepted(h, new ChangeSeasonStatus(SeasonStatus.Active), s_seasonA);
        await Accepted(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"), s_seasonA);
        var id = Guid.NewGuid();

        // The command is held inside the processor, then the browser request is cancelled
        block.Armed = true;
        using var cts = new CancellationTokenSource();
        var waiting = h.SendCancellableAsync(new RollGame(s_vasya), s_seasonA, id, cts.Token);
        await block.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        block.Release();

        // The retry with the same id finds it done
        var retry = await h.SendAsync(new RollGame(s_vasya), s_seasonA, id);
        Assert.True(retry.IsDuplicate);
        Assert.IsType<GameRolled>(Assert.Single(retry.Events).Event);
    }

    [Fact]
    public async Task Failure_after_the_commit_reloads_the_season_from_the_log()
    {
        var fault = new FailingAfterCommitInterceptor();
        await using var h = await QueueHarness.StartAsync(fault);
        await Accepted(h, new CreateSeason(s_seasonA, "Тестовый сезон", RulesetJson.Default()), s_seasonA);
        await Accepted(h, new ChangeSeasonStatus(SeasonStatus.Active), s_seasonA);
        await Accepted(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"), s_seasonA);

        // The roll is committed, but the caller gets an error
        fault.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.SendAsync(new RollGame(s_vasya), s_seasonA));
        fault.Armed = false;

        // The next command sees the committed roll (not a stale cache) and continues the numbering
        var start = await Accepted(h, new StartRun(s_vasya), s_seasonA);
        Assert.Equal(4, Assert.Single(start.Events).Sequence);
    }

    [Fact]
    public async Task Stopping_the_queue_completes_every_waiting_caller()
    {
        var block = new BlockingSaveInterceptor();
        await using var h = await QueueHarness.StartAsync(block);
        await Accepted(h, new CreateSeason(s_seasonA, "Тестовый сезон", RulesetJson.Default()), s_seasonA);
        await Accepted(h, new ChangeSeasonStatus(SeasonStatus.Active), s_seasonA);

        // Given the processor stuck in the first command and 29 more waiting in the queue
        block.Armed = true;
        var calls = Enumerable.Range(1, 30)
            .Select(i => h.SendAsync(new AddSeasonPlayer(Guid.Parse($"10000000-0000-0000-0000-{i:x12}"), Guid.Parse($"40000000-0000-0000-0000-{i:x12}"), $"Игрок {i}"), s_seasonA))
            .ToList();
        await block.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // When the server shuts down
        await h.StopAsync();

        // Then every call ends as cancelled, none hangs, and nothing was written
        var all = Task.WhenAll(calls.Select(c => c.ContinueWith(_ => { }, Ct, TaskContinuationOptions.None, TaskScheduler.Default)));
        await all.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.All(calls, c => Assert.True(c.IsCanceled, $"Call ended as {c.Status}"));
        await using var db = h.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(Ct));
    }

    [Fact]
    public async Task Connection_pragmas_are_applied()
    {
        await using var h = await QueueHarness.StartAsync();
        await using var db = h.NewDb();
        await db.Database.OpenConnectionAsync(Ct);

        Assert.Equal("wal", await PragmaAsync(db, "journal_mode"));
        Assert.Equal(1L, await PragmaAsync(db, "foreign_keys"));
        Assert.Equal(5000L, await PragmaAsync(db, "busy_timeout"));
        Assert.Equal(1L, await PragmaAsync(db, "synchronous")); // NORMAL
    }

    [Fact]
    public async Task Migrations_match_the_model()
    {
        await using var h = await QueueHarness.StartAsync();
        await using var db = h.NewDb();

        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task<object?> PragmaAsync(DbContext db, string name)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
#pragma warning disable CA2100 // test-only constant pragma names
        command.CommandText = $"PRAGMA {name};";
#pragma warning restore CA2100
        return await command.ExecuteScalarAsync(Ct);
    }

    private static async Task<CommandOutcome> Accepted(QueueHarness h, ICommand command, Guid season, Guid? id = null)
    {
        var outcome = await h.SendAsync(command, season, id);
        Assert.True(outcome.IsAccepted, $"{command} was rejected: {outcome.Rejection}");
        return outcome;
    }
}
