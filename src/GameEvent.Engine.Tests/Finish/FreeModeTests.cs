using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// The first finisher plays on in free mode (P4, T3; SPEC «Первое место … дальше он играет без зачёта и не влияет на
/// остальных»; K-5; the freeze amendment; D-09, D-16, D-99). The cycle goes on from Idle: roll, start, complete, drop.
/// His position is fixed from the finish. While the finish is provisional his points and coins count as usual (the drop
/// penalty takes points only, the bad event is created); once frozen nothing is given or taken: a completion writes only
/// the run and its dice, a drop only the run and the exclusion, a correction only itself. His runs after the finish do
/// not make the game «completed in the season» for others (D-16), even if the finish is revoked later; the game he plays
/// is busy as any other.
/// </summary>
public class FreeModeTests
{
    // ---- Frozen first ----

    [Fact]
    public void Frozen_first_keeps_playing_the_cycle()
    {
        var s = New();
        FrozenFirst(s, "Вася");

        s.Roll("Вася");
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
        s.Start("Вася");
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
        s.NextRandom(2, 2).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
    }

    [Fact]
    public void Frozen_first_completion_writes_the_dice_only()
    {
        // «выше сложной» would grant a good event and 2 + 2 would give 4 points, 4 cells and 6 coins to anyone else
        var s = New();
        FrozenFirst(s, "Вася");
        var before = s.Player("Вася");

        var (runId, _) = Complete(s, "Вася", [2, 2], Difficulty.Extreme);

        Assert.Equal([typeof(RunCompleted), typeof(CompletionRolled)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal([2, 2], s.LastEvents<CompletionRolled>().Single().Dice.Select(d => d.Value));
        Assert.Equal(RunStatus.Completed, s.State.Runs[runId].Status);
        Assert.Equal((before.Points, before.Coins, before.CellId), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void Frozen_first_completion_does_not_finish_again()
    {
        var s = New();
        var finishing = FrozenFirst(s, "Вася");
        var finish = FinishOf(s, "Вася");

        Complete(s, "Вася", [4, 4]);

        Assert.Empty(s.LastEvents<PlayerFinished>());
        Assert.Equal(finish, FinishOf(s, "Вася"));
        Assert.Equal(finishing, FinishOf(s, "Вася")!.RunId);
    }

    [Fact]
    public void Frozen_first_drop_has_no_penalty_and_no_bad_event()
    {
        var s = New();
        FrozenFirst(s, "Вася");
        var runId = Playing(s, "Вася");
        var game = s.State.Runs[runId].GameId;
        var before = s.Player("Вася");

        s.NextRandom(2, 3).Act(new DropRun(s.PlayerId("Вася")));

        // The drop itself and the exclusion; no points, no move, no bad event
        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunDropped), typeof(GameExcluded)], s.Last.Events.Select(e => e.GetType()));
        Assert.Contains(new GameExcluded(s.PlayerId("Вася"), game, ExclusionReason.Dropped), s.Last.Events);
        Assert.Equal(RunStatus.Dropped, s.State.Runs[runId].Status);
        Assert.Equal(TurnPhase.Idle, s.Player("Вася").Phase);
        Assert.Equal((before.Points, before.Coins, before.CellId), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void Frozen_first_tech_reroll_turned_into_a_drop_has_no_penalty()
    {
        var s = New();
        FrozenFirst(s, "Вася");
        var runId = Playing(s, "Вася");
        s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);
        var before = s.Player("Вася");

        s.NextRandom(2, 3).Act(new ConvertTechRerollToDrop(runId, "это был дроп"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(TechRerollConvertedToDrop)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(RunStatus.Dropped, s.State.Runs[runId].Status);
        Assert.Equal((before.Points, before.Coins, before.CellId), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void Frozen_first_hours_correction_changes_the_dice_but_not_points_or_coins()
    {
        // A run after the freeze 6 → 12 hours: two more dice (2, 2) would give 4 points and 6 coins to anyone else
        var s = New();
        FrozenFirst(s, "Вася");
        var (runId, _) = Complete(s, "Вася", [3, 1]);
        var before = s.Player("Вася");

        s.NextRandom(2, 2).Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunHoursCorrected)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal([3, 1, 2, 2], s.State.Runs[runId].Dice.Select(d => d.Value));
        Assert.Equal((before.Points, before.Coins, before.CellId), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Fact]
    public void Frozen_first_difficulty_change_changes_the_dice_but_not_points()
    {
        // A run after the freeze: normal 3 + 1 → hard ⌈3·6/4⌉ + ⌈1·6/4⌉ = 5 + 2
        var s = New();
        FrozenFirst(s, "Вася");
        var (runId, _) = Complete(s, "Вася", [3, 1]);
        var before = s.Player("Вася");

        s.Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "По пруфу — сложная"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunDifficultyChanged)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal([5, 2], s.State.Runs[runId].Dice.Select(d => d.Value));
        Assert.Equal((before.Points, before.Coins, before.CellId), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Fact]
    public void Frozen_first_place_is_final_a_reduction_of_his_finishing_run_changes_nothing()
    {
        // D-99: the finishing run 3 + 1 (surplus 0), 6 → 3 hours drops the die 1 — for anyone else a revoke and a step back
        var s = New();
        var runId = FrozenFirst(s, "Вася");
        var before = s.Player("Вася");

        s.Act(new CorrectRunHours(runId, 3, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunHoursCorrected)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal([3], s.State.Runs[runId].Dice.Select(d => d.Value));
        Assert.Equal(before, s.Player("Вася"));
        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));
    }

    [Fact]
    public void Frozen_first_place_is_final_an_increase_of_his_finishing_run_changes_nothing()
    {
        // 6 → 12 hours adds 2 + 2: no points, no coins, no surplus
        var s = New();
        var runId = FrozenFirst(s, "Вася");
        var before = s.Player("Вася");

        s.NextRandom(2, 2).Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunHoursCorrected)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(before, s.Player("Вася"));
    }

    [Fact]
    public void Frozen_first_place_is_final_a_lower_difficulty_of_his_finishing_run_changes_nothing()
    {
        // Normal 3 + 1 → easy 2 + 1: k = 1 > surplus 0, yet no revoke, no move, no points
        var s = New();
        var runId = FrozenFirst(s, "Вася");
        var before = s.Player("Вася");

        s.Act(new ChangeRunDifficulty(runId, Difficulty.Easy, "По пруфу — лёгкая"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunDifficultyChanged)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(before, s.Player("Вася"));
    }

    [Fact]
    public void Rejecting_a_run_of_the_frozen_first_takes_nothing()
    {
        // Run P was completed while the finish was provisional (+4 points, +6 coins); after the freeze its reject takes nothing
        var s = New();
        var finishing = FinishRun(s, "Вася");
        var (provisional, _) = Complete(s, "Вася", [2, 2]);
        Approve(s, finishing);
        ScenarioAssert.Accepted(s);
        var before = s.Player("Вася");

        Reject(s, provisional);

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(ProofRejected)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(RunStatus.Rejected, s.State.Runs[provisional].Status);
        Assert.Equal(before, s.Player("Вася"));
        Assert.True(FinishOf(s, "Вася")!.Frozen);
    }

    [Fact]
    public void Rejecting_a_run_the_frozen_first_completed_after_the_freeze_takes_nothing()
    {
        var s = New();
        FrozenFirst(s, "Вася");
        var (later, _) = Complete(s, "Вася", [2, 2]);
        var before = s.Player("Вася");

        Reject(s, later);

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(ProofRejected)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(before, s.Player("Вася"));
    }

    // ---- Rerolls of the frozen first are free (D-99: his coins must not change) ----

    private static Ruleset PaidRerolls(Ruleset r, RerollCostKind kind) =>
        r with
        {
            Roll = r.Roll with
            {
                FreeRerollsPerRoll = 0,
                RerollCost = kind == RerollCostKind.Coins
                    ? new RerollCost { Kind = kind, Amount = 5 }
                    : new RerollCost { Kind = kind },
            },
        };

    [Fact]
    public void Frozen_first_rerolls_without_paying_coins()
    {
        // No free rerolls per roll, a reroll costs 5 coins; Вася has 6 and is frozen
        var s = New(ruleset: r => PaidRerolls(r, RerollCostKind.Coins));
        FrozenFirst(s, "Вася");
        s.Roll("Вася");
        var before = s.Player("Вася");

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.IsType<GameRerolled>(s.Last.Events[0]);
        Assert.Empty(s.LastEvents<CoinsChanged>());
        Assert.Equal(before.Coins, s.Player("Вася").Coins);
        Assert.Equal(TurnPhase.Rolling, s.Player("Вася").Phase);
    }

    [Fact]
    public void Frozen_first_rerolls_without_coins_at_all()
    {
        // The boundary: with no coins a frozen first is not refused «not enough coins»
        var s = New(ruleset: r => PaidRerolls(r, RerollCostKind.Coins));
        FrozenFirst(s, "Вася");
        s.Roll("Вася");

        // The admin's own adjustment is not a freeze breach (D-21): he takes the coins away
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Сброс монеток", CoinsDelta: -s.Player("Вася").Coins));
        ScenarioAssert.Accepted(s);

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<CoinsChanged>());
        Assert.Equal(0, s.Player("Вася").Coins);
    }

    [Fact]
    public void Frozen_first_rerolls_without_a_bad_event()
    {
        var s = New(ruleset: r => PaidRerolls(r, RerollCostKind.BadEvent));
        FrozenFirst(s, "Вася");
        s.Roll("Вася");

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
        Assert.Empty(s.State.ManualEffects);
    }

    [Fact]
    public void Frozen_first_rerolls_without_spending_a_coupon()
    {
        var s = New(ruleset: r => PaidRerolls(r, RerollCostKind.Coins));
        FrozenFirst(s, "Вася");
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Купон", ResourceDeltas: [new ResourceDelta("freeRerolls", 1)]));
        ScenarioAssert.Accepted(s);
        s.Roll("Вася");

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<ResourceChanged>());
        Assert.Equal(1, s.Player("Вася").Resources["freeRerolls"]);
    }

    [Fact]
    public void Provisional_first_pays_for_a_reroll_as_usual()
    {
        // The boundary: before the freeze his coins count as usual
        var s = New(ruleset: r => PaidRerolls(r, RerollCostKind.Coins));
        FinishRun(s, "Вася");
        s.Roll("Вася");

        s.Act(new Reroll(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new CoinsChanged(s.PlayerId("Вася"), -5, CoinsReason.Reroll, null)], s.LastEvents<CoinsChanged>());
    }

    // ---- Provisional first ----

    [Fact]
    public void Provisional_first_completion_counts_points_and_coins_but_does_not_move()
    {
        var s = New();
        var vasya = s.PlayerId("Вася");
        FinishRun(s, "Вася");

        var (runId, _) = Complete(s, "Вася", [2, 2]);

        Assert.Equal([new PointsChanged(vasya, 4, PointsReason.CompletionRoll, runId)], s.LastEvents<PointsChanged>());
        Assert.Equal([new CoinsChanged(vasya, CoinsPerRun, CoinsReason.CompletionReward, runId)], s.LastEvents<CoinsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<PlayerFinished>());
        Assert.Equal((8, 2 * CoinsPerRun, LinearMap.FinishId), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Fact]
    public void Provisional_first_drop_takes_points_only_and_creates_the_bad_event()
    {
        var s = New();
        var vasya = s.PlayerId("Вася");
        FinishRun(s, "Вася");
        var runId = Playing(s, "Вася");

        s.NextRandom(2, 3).Act(new DropRun(vasya));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PointsChanged(vasya, -5, PointsReason.DropPenalty, runId)], s.LastEvents<PointsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        var created = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal((vasya, EventKind.Bad, ManualEffectSource.Drop, (Guid?)runId), (created.PlayerId, created.DrawEvent, created.Source, created.RunId));
        Assert.Equal((4 - 5, LinearMap.FinishId), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Provisional_first_hours_correction_changes_points_and_coins_and_adds_to_the_surplus()
    {
        // The finishing run 6 → 12 hours adds 2 + 2: 4 points, 6 coins, and the 4 steps burn at the finish (Q-3)
        var s = New();
        var vasya = s.PlayerId("Вася");
        var runId = FinishRun(s, "Вася");

        s.NextRandom(2, 2).Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PointsChanged(vasya, 4, PointsReason.RunCorrection, runId)], s.LastEvents<PointsChanged>());
        Assert.Equal([new CoinsChanged(vasya, 6, CoinsReason.RunCorrection, runId)], s.LastEvents<CoinsChanged>());
        Assert.Equal([new FinishSurplusChanged(vasya, 4)], s.LastEvents<FinishSurplusChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal((8, LinearMap.FinishId, 4), (s.Player("Вася").Points, s.Player("Вася").CellId, FinishOf(s, "Вася")!.Surplus));
    }

    [Fact]
    public void Provisional_first_reject_of_a_later_run_takes_points_and_coins_but_not_cells()
    {
        var s = New();
        var vasya = s.PlayerId("Вася");
        FinishRun(s, "Вася");
        var (later, _) = Complete(s, "Вася", [2, 2]);

        Reject(s, later);

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PointsChanged(vasya, -4, PointsReason.ProofRejected, later)], s.LastEvents<PointsChanged>());
        Assert.Equal([new CoinsChanged(vasya, -CoinsPerRun, CoinsReason.ProofRejected, later)], s.LastEvents<CoinsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<PlayerFinishRevoked>());
        Assert.Equal((4, LinearMap.FinishId), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.NotNull(FinishOf(s, "Вася"));
    }

    // ---- Games of the first after the finish (D-16) ----

    [Fact]
    public void Game_of_the_finishing_run_is_completed_in_the_season()
    {
        // Two games: Вася finishes with one; Петя is always offered the other
        var s = New(players: 2, games: 2);
        var finishing = FinishRun(s, "Вася");
        var game = s.State.Runs[finishing].GameId;

        for (var i = 0; i < 10; i++)
        {
            s.Roll("Петя");
            Assert.NotEqual(game, s.LastEvents<GameRolled>().Single().GameId);
            s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Сброс", DiscardOffer: true));
            ScenarioAssert.Accepted(s);
        }
    }

    [Fact]
    public void Game_the_provisional_first_completed_after_the_finish_stays_available_to_others()
    {
        // Two games: A finished Вася, B he completed in free mode; Петя gets B, never as «уже прошёл»
        var s = New(players: 2, games: 2);
        FinishRun(s, "Вася");
        var (later, _) = Complete(s, "Вася", [2, 2]);
        var gameB = s.State.Runs[later].GameId;

        s.Roll("Петя");

        var rolled = s.LastEvents<GameRolled>().Single();
        Assert.Equal(gameB, rolled.GameId);
        Assert.DoesNotContain(rolled.Misses, m => m.GameId == gameB);
    }

    [Fact]
    public void Game_the_frozen_first_completed_stays_available_to_others()
    {
        var s = New(players: 2, games: 2);
        FrozenFirst(s, "Вася");
        var (later, _) = Complete(s, "Вася", [2, 2]);

        s.Roll("Петя");

        Assert.Equal(s.State.Runs[later].GameId, s.LastEvents<GameRolled>().Single().GameId);
    }

    [Fact]
    public void Game_the_first_is_playing_is_busy()
    {
        // A is completed (the finishing run), B is being played by Вася: nothing is left for Петя
        var s = New(players: 2, games: 2);
        FinishRun(s, "Вася");
        Playing(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Петя"), RejectionCodes.NoAvailableGames);
    }

    [Fact]
    public void Games_completed_in_free_mode_stay_available_after_the_finish_is_revoked()
    {
        // D-16: the run keeps «does not count for the race» from its completion; A comes back by the reject itself
        var s = New(players: 2, games: 2);
        var finishing = FinishRun(s, "Вася");
        Complete(s, "Вася", [2, 2]);
        Reject(s, finishing);
        ScenarioAssert.Accepted(s);
        Assert.Null(FinishOf(s, "Вася"));

        for (var i = 0; i < 10; i++)
        {
            s.Roll("Петя");
            Assert.DoesNotContain(s.LastEvents<GameRolled>().Single().Misses, m => m.Reason == RollMissReason.CompletedInSeason);
            s.Act(new AdjustPlayer(s.PlayerId("Петя"), "Сброс", DiscardOffer: true));
            ScenarioAssert.Accepted(s);
        }
    }

    [Fact]
    public void Games_of_a_later_finisher_after_his_finish_are_completed_in_the_season()
    {
        // Only the first is out of the race: Петя finished second, his next game is «уже прошёл» for others
        var s = New(players: 3, games: 3);
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        var (later, _) = Complete(s, "Петя", [2, 2]);
        var game = s.State.Runs[later].GameId;

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Roll("Маша"), RejectionCodes.NoAvailableGames);
        Assert.Contains(s.State.Runs.Values, r => r.GameId == game && r.Status == RunStatus.Completed);
    }
}
