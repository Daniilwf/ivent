using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
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

    [Fact]
    public async Task Projection_of_pending_choices_equals_the_fold_of_the_log()
    {
        await using var h = await QueueHarness.StartAsync();
        var petya = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var ct = TestContext.Current.CancellationToken;

        // Given a season with a choice of 3 (D-91); each pool category has two games
        await AcceptedAsync(h, new CreateSeason(s_season, "Осень", RulesetJson.Default()));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));
        await AcceptedAsync(h, new AddSeasonPlayer(petya, petya, "Петя"));
        var rules = RulesetJson.Default();
        await AcceptedAsync(h, new ChangeRuleset(rules with { Roll = rules.Roll with { ChoiceCount = 3 } }));

        // When both roll, each is left waiting for a choice of a whole category (the other's is reserved, D-06)
        await AcceptedAsync(h, new RollGame(s_vasya));
        await AcceptedAsync(h, new RollGame(petya));

        // Then while the choices are pending the projection equals the fold of the log and keeps them (L4, T2)
        var pending = await AssertProjectionEqualsReplayAsync(h, ct);
        var vasyaChoice = Assert.IsType<PendingChoice>(pending.Players[s_vasya].Choice);
        Assert.IsType<PendingChoice>(pending.Players[petya].Choice);
        await using (var db = h.NewDb())
        {
            Assert.All(await db.SeasonPlayers.ToListAsync(ct), row =>
            {
                Assert.NotNull(row.ChoiceJson);
                Assert.Equal(TurnPhase.Rolling, row.Phase);
            });
        }

        // When Вася picks an option and the admin discards Петя's choice
        await AcceptedAsync(h, new MakeChoice(s_vasya, vasyaChoice.ChoiceId, vasyaChoice.Options[0].Id));
        await AcceptedAsync(h, new AdjustPlayer(petya, "Завис выбор", DiscardOffer: true));

        // Then the projection still equals the fold: Вася plays the chosen game, Петя is idle, no choice is stored
        var after = await AssertProjectionEqualsReplayAsync(h, ct);
        Assert.Equal(TurnPhase.Playing, after.Players[s_vasya].Phase);
        Assert.Equal(TurnPhase.Idle, after.Players[petya].Phase);
        await using var final = h.NewDb();
        Assert.All(await final.SeasonPlayers.ToListAsync(ct), row => Assert.Null(row.ChoiceJson));
        var run = await final.Runs.SingleAsync(ct);
        Assert.Equal((s_vasya, RunStatus.Playing, vasyaChoice.Options[0].Game!.GameId), (run.PlayerId, run.Status, run.GameId));
    }

    [Fact]
    public async Task Projection_of_exclusions_equals_the_fold_of_the_log()
    {
        await using var h = await QueueHarness.StartAsync();
        var petya = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var ct = TestContext.Current.CancellationToken;
        await AcceptedAsync(h, new CreateSeason(s_season, "Осень", RulesetJson.Default()));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));
        await AcceptedAsync(h, new AddSeasonPlayer(petya, petya, "Петя"));

        // When Вася declares «Уже проходил» twice in a row and Петя once (D-92: PlayerGameExclusion)
        var vasyaFirst = OfferedIn(await AcceptedAsync(h, new RollGame(s_vasya)));
        var vasyaSecond = OfferedIn(await AcceptedAsync(h, new DeclareAlreadyPlayed(s_vasya, vasyaFirst)));
        await AcceptedAsync(h, new DeclareAlreadyPlayed(s_vasya, vasyaSecond));
        var petyaGame = OfferedIn(await AcceptedAsync(h, new RollGame(petya)));
        await AcceptedAsync(h, new DeclareAlreadyPlayed(petya, petyaGame));

        // Then the projection equals the fold of the log, and the table holds one row per player and game
        var state = await AssertProjectionEqualsReplayAsync(h, ct);
        Assert.Equal(
            new[] { vasyaFirst, vasyaSecond }.Order(),
            state.Players[s_vasya].Exclusions.Select(x => x.GameId));
        await using (var db = h.NewDb())
        {
            var rows = await db.Exclusions.AsNoTracking().ToListAsync(ct);
            Assert.Equal(
                state.Players.Values.SelectMany(p => p.Exclusions.Select(x => (p.PlayerId, x.GameId, x.Reason))).Order(),
                rows.Select(r => (r.PlayerId, r.GameId, r.Reason)).Order());
            Assert.All(rows, r => Assert.Equal(ExclusionReason.AlreadyPlayed, r.Reason));
        }

        // And after a restart and an admin discard the exclusions stay (they never shrink, D-08)
        await h.RestartAsync();
        await AcceptedAsync(h, new AdjustPlayer(s_vasya, "Сброс", DiscardOffer: true));
        var after = await AssertProjectionEqualsReplayAsync(h, ct);
        Assert.Equal(state.Players[s_vasya].Exclusions, after.Players[s_vasya].Exclusions);
        await using var final = h.NewDb();
        Assert.Equal(3, await final.Exclusions.CountAsync(ct));
    }

    [Fact]
    public async Task Projection_of_rerolls_and_manual_effects_equals_the_fold_of_the_log()
    {
        await using var h = await QueueHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var rules = RulesetJson.Default();
        await AcceptedAsync(h, new CreateSeason(s_season, "Осень", rules));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));
        await AcceptedAsync(h, new AdjustPlayer(s_vasya, "Приз", CoinsDelta: rules.Roll.RerollCost.Amount!.Value, ResourceDeltas: [new ResourceDelta("freeRerolls", 1)]));

        // When Вася rerolls: free rerolls of the roll, then the coupon, then coins (D-93)
        await AcceptedAsync(h, new RollGame(s_vasya));
        for (var i = 0; i < rules.Roll.FreeRerollsPerRoll + 2; i++)
        {
            await AcceptedAsync(h, new Reroll(s_vasya));
        }

        // Then the projection keeps the counter and the spent coupon and coins, equal to the fold of the log
        var state = await AssertProjectionEqualsReplayAsync(h, ct);
        Assert.Equal(rules.Roll.FreeRerollsPerRoll + 2, state.Players[s_vasya].RerollsThisRoll);
        Assert.Equal(0, state.Players[s_vasya].Coins);
        await using (var db = h.NewDb())
        {
            var row = await db.SeasonPlayers.AsNoTracking().SingleAsync(ct);
            Assert.Equal(state.Players[s_vasya].RerollsThisRoll, row.RerollsThisRoll);
        }

        // When the paid reroll costs a bad event, a manual effect waits (PendingManualEffect)
        var badEvent = rules with { Roll = rules.Roll with { RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent } } };
        await AcceptedAsync(h, new ChangeRuleset(badEvent));
        await AcceptedAsync(h, new Reroll(s_vasya));

        var withEffect = await AssertProjectionEqualsReplayAsync(h, ct);
        var effect = Assert.Single(withEffect.ManualEffects.Values);
        Assert.Equal((s_vasya, EventKind.Bad, ManualEffectSource.PaidReroll, (Guid?)null), (effect.PlayerId, effect.DrawEvent, effect.Source, effect.RunId));
        await using (var db = h.NewDb())
        {
            var rows = await db.ManualEffects.AsNoTracking().ToListAsync(ct);
            var row = Assert.Single(rows);
            Assert.Equal(
                (effect.EffectId, s_season, s_vasya, EventKind.Bad, ManualEffectSource.PaidReroll, (Guid?)null),
                (row.Id, row.SeasonId, row.PlayerId, row.DrawEvent, row.Source, row.RunId));
        }

        // And after a restart and starting the game the counter is 0 again, the effect stays
        await h.RestartAsync();
        await AcceptedAsync(h, new StartRun(s_vasya));
        var playing = await AssertProjectionEqualsReplayAsync(h, ct);
        Assert.Equal(0, playing.Players[s_vasya].RerollsThisRoll);
        Assert.Single(playing.ManualEffects);
        await using var final = h.NewDb();
        Assert.Equal(0, (await final.SeasonPlayers.AsNoTracking().SingleAsync(ct)).RerollsThisRoll);
    }

    [Fact]
    public async Task Projection_of_drops_tech_rerolls_and_a_conversion_equals_the_fold_of_the_log()
    {
        await using var h = await QueueHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await AcceptedAsync(h, new CreateSeason(s_season, "Осень", RulesetJson.Default()));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));

        // Given Вася walked forward with one completed game
        await AcceptedAsync(h, new RollGame(s_vasya));
        await AcceptedAsync(h, new StartRun(s_vasya));
        await AcceptedAsync(h, new CompleteRun(s_vasya, Difficulty.Hard));

        // When he drops one game (D-94: penalty, exclusion «dropped», a bad event) ...
        var dropped = OfferedIn(await AcceptedAsync(h, new RollGame(s_vasya)));
        await AcceptedAsync(h, new StartRun(s_vasya));
        await AcceptedAsync(h, new DropRun(s_vasya));

        // ... and tech-rerolls the next one (exclusion «tech-rerolled», a new roll at once)
        var techRerolled = OfferedIn(await AcceptedAsync(h, new RollGame(s_vasya)));
        await AcceptedAsync(h, new StartRun(s_vasya));
        await AcceptedAsync(h, new TechReroll(s_vasya, TechRerollReason.Other, "Нужен геймпад"));

        // Then the projection equals the fold: run statuses, exclusions with their reasons, points, position, effects
        var state = await AssertProjectionEqualsReplayAsync(h, ct);
        Assert.Equal(
            [new GameExclusion(dropped, ExclusionReason.Dropped), new GameExclusion(techRerolled, ExclusionReason.TechRerolled)],
            state.Players[s_vasya].Exclusions.OrderBy(x => x.Reason));
        await using (var db = h.NewDb())
        {
            var rows = await db.Exclusions.AsNoTracking().ToListAsync(ct);
            Assert.Equal(
                new[] { (s_vasya, dropped, ExclusionReason.Dropped), (s_vasya, techRerolled, ExclusionReason.TechRerolled) }.Order(),
                rows.Select(r => (r.PlayerId, r.GameId, r.Reason)).Order());
            var runs = await db.Runs.AsNoTracking().ToListAsync(ct);
            Assert.Equal(RunStatus.Dropped, runs.Single(r => r.GameId == dropped).Status);
            Assert.Equal(RunStatus.TechRerolled, runs.Single(r => r.GameId == techRerolled).Status);
            var player = await db.SeasonPlayers.AsNoTracking().SingleAsync(ct);
            Assert.Equal((state.Players[s_vasya].Points, state.Players[s_vasya].CellId), (player.Points, player.CellId));
            Assert.Equal(state.Players[s_vasya].Phase, player.Phase);
            var effect = Assert.Single(await db.ManualEffects.AsNoTracking().ToListAsync(ct));
            Assert.Equal(ManualEffectSource.Drop, effect.Source);
        }

        // When, after a restart, the admin turns the tech reroll into a drop
        await h.RestartAsync();
        var techRun = state.Runs.Values.Single(r => r.Status == RunStatus.TechRerolled).RunId;
        await AcceptedAsync(h, new ConvertTechRerollToDrop(techRun, "Игра запускалась"));

        // Then the exclusion row's reason is updated to «dropped», the run is dropped, a second bad event waits
        var after = await AssertProjectionEqualsReplayAsync(h, ct);
        Assert.All(after.Players[s_vasya].Exclusions, x => Assert.Equal(ExclusionReason.Dropped, x.Reason));
        await using var final = h.NewDb();
        var finalRows = await final.Exclusions.AsNoTracking().ToListAsync(ct);
        Assert.Equal(2, finalRows.Count);
        Assert.All(finalRows, r => Assert.Equal(ExclusionReason.Dropped, r.Reason));
        Assert.Equal(RunStatus.Dropped, (await final.Runs.AsNoTracking().SingleAsync(r => r.Id == techRun, ct)).Status);
        Assert.Equal(2, await final.ManualEffects.CountAsync(ct));
        Assert.Equal(after.Players[s_vasya].Points, (await final.SeasonPlayers.AsNoTracking().SingleAsync(ct)).Points);
    }

    [Fact]
    public async Task Projection_of_challenge_dice_hours_source_coins_and_reviews_equals_the_fold_of_the_log()
    {
        await using var h = await QueueHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        const string Source = "https://howlongtobeat.com/game/2231";
        await AcceptedAsync(h, new CreateSeason(s_season, "Осень", RulesetJson.Default()));
        await AcceptedAsync(h, new ChangeSeasonStatus(SeasonStatus.Active));
        await AcceptedAsync(h, new AddSeasonPlayer(s_vasya, s_vasya, "Вася"));

        // Given the pool knows no hours, so the completion carries an estimate with its source (D-96)
        await using (var db = h.NewDb())
        {
            await db.Games.ExecuteUpdateAsync(g => g.SetProperty(x => x.Hours, (decimal?)null), ct);
        }

        // When Вася completes on «выше сложной» with the challenge and a review
        await AcceptedAsync(h, new RollGame(s_vasya));
        await AcceptedAsync(h, new StartRun(s_vasya));
        await AcceptedAsync(h, new CompleteRun(
            s_vasya, Difficulty.Extreme, EstimatedHours: 6, HoursSource: Source, ChallengeDone: true, Review: new RunReview(8, "Страшно")));

        // Then the projection equals the fold: challenge dice apart, the source, coins, the good event and the review row
        var state = await AssertProjectionEqualsReplayAsync(h, ct);
        var run = Assert.Single(state.Runs.Values);
        Assert.Single(run.ChallengeDice); // default ruleset: challengeBonus.extraDice 1
        Assert.Equal(Source, run.HoursSource);
        Assert.Equal(new RunReview(8, "Страшно"), run.Review);
        Assert.Equal(6, state.Players[s_vasya].Coins); // default ruleset: 1 coin per hour, min 3
        await using (var db = h.NewDb())
        {
            var runRow = await db.Runs.AsNoTracking().SingleAsync(ct);
            Assert.Equal(Source, runRow.HoursSource);
            Assert.NotEqual("[]", runRow.ChallengeDiceJson);
            var review = Assert.Single(await db.Reviews.AsNoTracking().ToListAsync(ct));
            Assert.Equal(
                (run.RunId, s_season, s_vasya, run.GameId, 8, (string?)"Страшно"),
                (review.RunId, review.SeasonId, review.PlayerId, review.GameId, review.Rating, review.Text));
            Assert.Equal(6, (await db.SeasonPlayers.AsNoTracking().SingleAsync(ct)).Coins);
            var effect = Assert.Single(await db.ManualEffects.AsNoTracking().ToListAsync(ct));
            Assert.Equal((EventKind.Good, ManualEffectSource.Difficulty, (Guid?)run.RunId), (effect.DrawEvent, effect.Source, effect.RunId));
        }

        // When, after a restart, he changes the review and drops the text
        await h.RestartAsync();
        await AcceptedAsync(h, new ReviewRun(s_vasya, run.RunId, new RunReview(3, null)));

        // Then the one review row is replaced, not duplicated
        var after = await AssertProjectionEqualsReplayAsync(h, ct);
        Assert.Equal(new RunReview(3, null), after.Runs[run.RunId].Review);
        await using var final = h.NewDb();
        var replaced = Assert.Single(await final.Reviews.AsNoTracking().ToListAsync(ct));
        Assert.Equal((3, (string?)null), (replaced.Rating, replaced.Text));
    }

    private static Guid OfferedIn(Infrastructure.Queue.CommandOutcome outcome) =>
        outcome.Events.Select(e => e.Event).OfType<GameRolled>().Single().GameId;

    private static async Task<SeasonState> AssertProjectionEqualsReplayAsync(QueueHarness h, CancellationToken ct)
    {
        await using var db = h.NewDb();
        var (replayed, _) = await EventLogReader.ReplaySeasonAsync(db, s_season, ct);
        Assert.Equal(replayed, await SeasonProjection.ReadAsync(db, replayed, ct));
        return replayed;
    }
}

