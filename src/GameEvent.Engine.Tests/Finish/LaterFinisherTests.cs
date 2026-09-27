using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// Finishers after the first go on with the cycle, their points grow, their position is fixed (T4, P6, RR7; SPEC
/// «Финишировавшие не первыми … продолжают набирать очки. Их позиция после финиша больше не меняется»; D-09, D-99):
/// a completion gives its points, coins and event as usual but no move; the drop penalty (and a tech reroll turned into
/// a drop) takes points only and the bad event is created; a reject of a run after the finish takes its points and coins
/// but no cells.
/// </summary>
public class LaterFinisherTests
{
    /// <summary>Вася finishes first, Петя second (4 + 10 points, the finish, 6 coins).</summary>
    private static (Scenario S, Guid Petya) PetyaSecond()
    {
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        Assert.Equal(2, FinishOf(s, "Петя")!.Order);
        return (s, s.PlayerId("Петя"));
    }

    [Fact]
    public void No_movement_after_finish()
    {
        var (s, petya) = PetyaSecond();

        var (runId, _) = Complete(s, "Петя", [2, 2]);

        Assert.Equal([new PointsChanged(petya, 4, PointsReason.CompletionRoll, runId)], s.LastEvents<PointsChanged>());
        Assert.Equal([new CoinsChanged(petya, CoinsPerRun, CoinsReason.CompletionReward, runId)], s.LastEvents<CoinsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal((18, 2 * CoinsPerRun, LinearMap.FinishId), (s.Player("Петя").Points, s.Player("Петя").Coins, s.Player("Петя").CellId));
    }

    [Fact]
    public void Later_finisher_keeps_playing_the_cycle()
    {
        var (s, _) = PetyaSecond();

        s.Roll("Петя").Start("Петя");
        Assert.Equal(TurnPhase.Playing, s.Player("Петя").Phase);
        s.NextRandom(1, 1).Complete("Петя");

        ScenarioAssert.Accepted(s);
        Assert.Equal(TurnPhase.Idle, s.Player("Петя").Phase);
    }

    [Fact]
    public void Later_finisher_gets_the_difficulty_event_as_usual()
    {
        // «выше сложной» grants a good event: it acts on the player himself, so it stays
        var (s, petya) = PetyaSecond();

        var (runId, _) = Complete(s, "Петя", [2, 2], Difficulty.Extreme);

        var created = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal((petya, (EventKind?)EventKind.Good, ManualEffectSource.Difficulty, (Guid?)runId), (created.PlayerId, created.DrawEvent, created.Source, created.RunId));
    }

    [Fact]
    public void Later_finisher_is_not_frozen_and_not_first()
    {
        var (s, _) = PetyaSecond();

        Complete(s, "Петя", [2, 2]);

        Assert.False(FinishOf(s, "Петя")!.Frozen);
        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));
    }

    // ---- Drop (RR7) ----

    [Fact]
    public void Later_finisher_drop_takes_points_only_and_creates_the_bad_event()
    {
        var (s, petya) = PetyaSecond();
        var runId = Playing(s, "Петя");
        var game = s.State.Runs[runId].GameId;

        s.NextRandom(2, 3).Act(new DropRun(petya));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PointsChanged(petya, -5, PointsReason.DropPenalty, runId)], s.LastEvents<PointsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Contains(new GameExcluded(petya, game, ExclusionReason.Dropped), s.Last.Events);
        var created = Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal((petya, (EventKind?)EventKind.Bad, ManualEffectSource.Drop), (created.PlayerId, created.DrawEvent, created.Source));
        Assert.Equal((14 - 5, LinearMap.FinishId), (s.Player("Петя").Points, s.Player("Петя").CellId));
        Assert.Equal(2, FinishOf(s, "Петя")!.Order);
    }

    [Fact]
    public void Later_finisher_tech_reroll_turned_into_a_drop_takes_points_only()
    {
        var (s, petya) = PetyaSecond();
        var runId = Playing(s, "Петя");
        s.Act(new TechReroll(petya, TechRerollReason.DoesNotLaunch, null));
        ScenarioAssert.Accepted(s);

        s.NextRandom(2, 3).Act(new ConvertTechRerollToDrop(runId, "это был дроп"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PointsChanged(petya, -5, PointsReason.DropPenalty, runId)], s.LastEvents<PointsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Single(s.LastEvents<ManualEffectCreated>());
        Assert.Equal((14 - 5, LinearMap.FinishId), (s.Player("Петя").Points, s.Player("Петя").CellId));
    }

    [Fact]
    public void Drop_penalty_of_a_player_who_has_not_finished_still_moves_him_back()
    {
        // The boundary: the rule is about finishers only — Маша on c2 is thrown back as before
        var (s, _) = PetyaSecond();
        Complete(s, "Маша", [1, 1]);
        var runId = Playing(s, "Маша");

        s.NextRandom(1, 1).Act(new DropRun(s.PlayerId("Маша")));

        ScenarioAssert.Accepted(s);
        var moved = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal(("c2", LinearMap.StartId, -2, MoveReason.DropPenalty, (Guid?)runId), (moved.From, moved.To, moved.Steps, moved.Reason, moved.RunId));
    }

    // ---- Reject of a run after the finish ----

    [Fact]
    public void Rejecting_a_run_after_the_finish_takes_points_and_coins_but_no_cells()
    {
        var (s, petya) = PetyaSecond();
        var (later, _) = Complete(s, "Петя", [2, 2]);

        Reject(s, later);

        ScenarioAssert.Accepted(s);
        Assert.Equal([new PointsChanged(petya, -4, PointsReason.ProofRejected, later)], s.LastEvents<PointsChanged>());
        Assert.Equal([new CoinsChanged(petya, -CoinsPerRun, CoinsReason.ProofRejected, later)], s.LastEvents<CoinsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<PlayerFinishRevoked>());
        Assert.Equal((14, LinearMap.FinishId), (s.Player("Петя").Points, s.Player("Петя").CellId));
        Assert.Equal(2, FinishOf(s, "Петя")!.Order);
    }
}
