using GameEvent.Engine.Effects;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// How a coop ends before or after its completion (EV29, EV38; D-522, invariant 13): the partner leaves without a penalty
/// and without an exclusion; the roller's drop or tech reroll is his alone and frees the partner; the admin marking the
/// partner inactive frees him too; a reject takes from each his share of points, cells and coins (D-321 per player); an
/// undo of the completion waits for the later commands of either player. Scenarios S5.10-17…-20, -24, -25, -27.
/// </summary>
public class CoopEndTests
{
    // ---- The partner leaves (S5.10-17) ----

    [Fact]
    public void The_partner_leaves_without_a_penalty_and_may_roll_at_once()
    {
        var s = New();
        var run = s.CoopRun();
        var petya = s.PlayerId("Петя");
        var before = s.Player("Петя");
        var now = s.Clock.UtcNow;

        s.Act(new LeaveCoop(petya));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new CoopLeft(run, petya, CoopLeaveReason.Left, now), Assert.Single(s.Last.Events));
        Assert.Null(s.State.Runs[run].Coop);
        Assert.Equal(RunStatus.Playing, s.State.Runs[run].Status);
        var after = s.Player("Петя");
        Assert.Equal((TurnPhase.Idle, (Guid?)null), (after.Phase, after.ActiveRunId));
        Assert.Equal((before.Points, before.Coins, before.CellId), (after.Points, after.Coins, after.CellId));
        Assert.DoesNotContain(after.Exclusions, x => x.GameId == s.GameId(CoopGame));

        s.RollTitle("Петя", SoloGame);
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void After_the_partner_left_the_completion_is_the_rollers_alone()
    {
        var s = New();
        s.CoopRun();
        s.Act(new LeaveCoop(s.PlayerId("Петя")));

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<CoopRewardSplit>());
        Assert.Equal((7, CoinsPerRun, "c7"), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
        Assert.Equal((0, 0, LinearMap.StartId), (s.Player("Петя").Points, s.Player("Петя").Coins, s.Player("Петя").CellId));
    }

    [Fact]
    public void Only_the_partner_leaves()
    {
        var s = New();
        s.CoopRun();

        s.Rejects(new LeaveCoop(s.PlayerId("Маша")), RejectionCodes.CoopNotPartner);
        s.Rejects(new LeaveCoop(s.PlayerId("Вася")), RejectionCodes.CoopNotPartner);

        // A player alone in his own run is no partner either
        s.PlayTitle("Маша", SoloGame);
        s.Rejects(new LeaveCoop(s.PlayerId("Маша")), RejectionCodes.CoopNotPartner);
    }

    // ---- The roller drops (S5.10-18) ----

    [Fact]
    public void The_rollers_drop_ends_the_coop_and_its_penalty_is_his_alone()
    {
        // Вася first walks 8 cells, so the drop's 2d4 (1 + 1) has points and cells to take
        var s = New();
        s.CompleteTitle("Вася", OtherSoloGame, 4, 4);
        var run = s.CoopRun();
        var (vasya, petya) = (s.PlayerId("Вася"), s.PlayerId("Петя"));
        var petyaBefore = s.Player("Петя");
        var now = s.Clock.UtcNow;

        s.NextRandom(1, 1).Act(new DropRun(vasya));

        ScenarioAssert.Accepted(s);
        var events = s.Last.Events.ToList();
        var left = events.FindIndex(e => e is CoopLeft);
        Assert.True(left >= 0, "The drop did not end the coop.");
        Assert.Equal(new CoopLeft(run, petya, CoopLeaveReason.RollerDropped, now), events[left]);
        var penalty = events.FindIndex(e => e is PointsChanged { Reason: PointsReason.DropPenalty });
        Assert.True(penalty >= 0, "The drop wrote no penalty.");
        Assert.True(left < penalty, "The coop must end before the drop's penalty.");

        // The penalty and the bad event are Вася's only
        Assert.All(s.LastEvents<PointsChanged>(), e => Assert.Equal(vasya, e.PlayerId));
        Assert.All(s.LastEvents<PlayerMoved>(), e => Assert.Equal(vasya, e.PlayerId));
        Assert.All(s.LastEvents<ManualEffectCreated>(), e => Assert.Equal(vasya, e.PlayerId));
        Assert.All(s.LastEvents<GameExcluded>(), e => Assert.Equal(vasya, e.PlayerId));
        Assert.Contains(s.Player("Вася").Exclusions, x => x.GameId == s.GameId(CoopGame) && x.Reason == ExclusionReason.Dropped);

        // Петя is free, as he was, and the game is not excluded for him
        var petyaAfter = s.Player("Петя");
        Assert.Equal((TurnPhase.Idle, (Guid?)null), (petyaAfter.Phase, petyaAfter.ActiveRunId));
        Assert.Equal((petyaBefore.Points, petyaBefore.Coins, petyaBefore.CellId), (petyaAfter.Points, petyaAfter.Coins, petyaAfter.CellId));
        Assert.DoesNotContain(petyaAfter.Exclusions, x => x.GameId == s.GameId(CoopGame));
        Assert.Null(s.State.Runs[run].Coop);
    }

    // ---- The roller tech-rerolls (S5.10-19) ----

    [Fact]
    public void The_rollers_tech_reroll_ends_the_coop_and_his_next_run_has_no_partner()
    {
        var s = New();
        var run = s.CoopRun();
        var (vasya, petya) = (s.PlayerId("Вася"), s.PlayerId("Петя"));
        var now = s.Clock.UtcNow;

        s.Act(new TechReroll(vasya, TechRerollReason.DoesNotLaunch, null));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new CoopLeft(run, petya, CoopLeaveReason.RollerTechRerolled, now), Assert.Single(s.LastEvents<CoopLeft>()));
        Assert.Contains(s.Player("Вася").Exclusions, x => x.GameId == s.GameId(CoopGame) && x.Reason == ExclusionReason.TechRerolled);
        Assert.DoesNotContain(s.Player("Петя").Exclusions, x => x.GameId == s.GameId(CoopGame));
        Assert.Equal((TurnPhase.Idle, (Guid?)null), (s.Player("Петя").Phase, s.Player("Петя").ActiveRunId));

        // The tech reroll rolled again: the new run is Вася's alone
        s.Start("Вася");
        var next = s.ActiveRun("Вася");
        Assert.NotEqual(run, next);
        Assert.Null(s.State.Runs[next].Coop);
    }

    // ---- The partner marked inactive (S5.10-24) ----

    [Fact]
    public void A_partner_marked_inactive_leaves_the_coop_and_the_roller_completes_alone()
    {
        var s = New();
        var run = s.CoopRun();
        var petya = s.PlayerId("Петя");
        var now = s.Clock.UtcNow;

        s.Act(new SetPlayerInactive(petya, IsInactive: true));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new CoopLeft(run, petya, CoopLeaveReason.PartnerInactive, now), Assert.Single(s.LastEvents<CoopLeft>()));
        Assert.Null(s.State.Runs[run].Coop);

        s.NextRandom(3, 4).Complete("Вася");
        Assert.Empty(s.LastEvents<CoopRewardSplit>());
        Assert.Equal(7, s.Player("Вася").Points);
        Assert.Equal(0, s.Player("Петя").Points);
    }

    // ---- Reject (S5.10-20) ----

    [Fact]
    public void A_reject_takes_from_each_his_share_of_points_cells_and_coins()
    {
        var s = New();
        var run = s.CoopRun();
        var (vasya, petya) = (s.PlayerId("Вася"), s.PlayerId("Петя"));
        s.CompleteWith("Вася", 3, 4);

        s.Act(new RejectProof(run, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        Assert.Contains(new PointsChanged(vasya, -4, PointsReason.ProofRejected, run), s.Last.Events);
        Assert.Contains(new PointsChanged(petya, -3, PointsReason.ProofRejected, run), s.Last.Events);
        Assert.Contains(new CoinsChanged(vasya, -3, CoinsReason.ProofRejected, run), s.Last.Events);
        Assert.Contains(new CoinsChanged(petya, -3, CoinsReason.ProofRejected, run), s.Last.Events);
        var moves = s.LastEvents<PlayerMoved>().ToDictionary(m => m.PlayerId);
        Assert.Equal(("c4", LinearMap.StartId, MoveReason.ProofRejected), (moves[vasya].From, moves[vasya].To, moves[vasya].Reason));
        Assert.Equal(("c3", LinearMap.StartId, MoveReason.ProofRejected), (moves[petya].From, moves[petya].To, moves[petya].Reason));
        foreach (var player in new[] { "Вася", "Петя" })
        {
            Assert.Equal((0, 0, LinearMap.StartId), (s.Player(player).Points, s.Player(player).Coins, s.Player(player).CellId));
        }

        Assert.Equal(RunStatus.Rejected, s.State.Runs[run].Status);
    }

    [Fact]
    public void A_reject_returns_the_partner_who_moved_since_by_his_share_only()
    {
        // D-321 per player: Петя moved on by a run of his own after the coop one — the reject takes 3 cells back from where
        // he stands, not to the cell before the coop move
        var s = New();
        var run = s.CoopRun();
        s.CompleteWith("Вася", 3, 4);
        s.CompleteTitle("Петя", SoloGame, 1, 1);
        Assert.Equal("c5", s.Player("Петя").CellId);

        s.Act(new RejectProof(run, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        Assert.Equal((2, "c2"), (s.Player("Петя").Points, s.Player("Петя").CellId));
    }

    // ---- Undo (S5.10-25, EV38) ----

    [Fact]
    public void The_undo_of_the_completion_waits_for_the_partners_later_command()
    {
        var s = New();
        s.CoopRun();
        var before = s.State;
        s.CompleteWith("Вася", 3, 4);
        var completion = s.LastCommandId;
        s.RollTitle("Петя", SoloGame);
        var roll = s.LastCommandId;

        s.Rejects(new UndoCommand(completion, "Ошибка админа"), RejectionCodes.UndoDependents);
        Assert.Contains(roll, s.Last.Rejection!.Related);

        s.Act(new UndoCommand(roll, "Ошибка админа"));
        ScenarioAssert.Accepted(s);
        s.Act(new UndoCommand(completion, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Comparable(before), Comparable(s.State));
    }

    [Fact]
    public void The_undo_of_the_accept_brings_both_back()
    {
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var id = s.Invite("Вася", "Петя");
        var before = s.State;
        s.Accept("Петя", id);
        var accept = s.LastCommandId;

        s.Act(new UndoCommand(accept, "Ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(Comparable(before), Comparable(s.State));
        Assert.Null(s.State.Runs[run].Coop);
        Assert.Equal(TurnPhase.Idle, s.Player("Петя").Phase);
        Assert.Equal(InteractionStatus.Pending, s.Invitation(id).Status);
    }

    // ---- The flag switched off mid-coop (S5.10-27, Н-25) ----

    [Fact]
    public void With_the_flag_switched_off_mid_coop_the_completion_is_solo_and_frees_the_partner()
    {
        // Н-25, нужно решение: our default — the coop freezes; the completion is not split, the partner is freed
        var s = New();
        var run = s.CoopRun();
        s.WithRuleset(r => r with { Features = r.Features with { Coop = false } });

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<CoopRewardSplit>());
        Assert.Equal(7, s.Player("Вася").Points);
        Assert.Equal((0, 0, LinearMap.StartId), (s.Player("Петя").Points, s.Player("Петя").Coins, s.Player("Петя").CellId));
        Assert.Equal((TurnPhase.Idle, (Guid?)null), (s.Player("Петя").Phase, s.Player("Петя").ActiveRunId));
        Assert.Equal(RunStatus.Completed, s.State.Runs[run].Status);
    }
}
