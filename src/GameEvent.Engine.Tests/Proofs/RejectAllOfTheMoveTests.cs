using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Proofs;

/// <summary>
/// A reject cancels everything the run's move gave (the owner's decision 2026-09-28, SPEC «Награда за прохождение»; D-327):
/// the run's points and the points bonus of the cell its move stopped on, whenever it was gained; the position by D-321;
/// coins and items of the move's cells — none exist yet (stage 4). A points bonus of another stop (a drop penalty) stays.
/// «Отклонить со штрафом дропа» (<see cref="RejectProofWithDropPenalty"/>) adds the drop penalty of the season's rules:
/// <see cref="ProofRejectPenalized"/> with the dice, then the penalty's points, push back and bad event, as a drop.
/// Games of 3 hours give one d4; the pinned drop penalty is 2d4 with a bad event.
/// </summary>
public class RejectAllOfTheMoveTests
{
    private const string Comment = "Фейковое прохождение";

    private static Scenario Pool(MapGraph? map = null)
    {
        var s = Scenario.New();
        if (map is not null)
        {
            s.WithMap(map);
        }

        return s.WithCategory("Horror")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror").WithGame("Kuon", 6, "Horror")
            .WithPlayers("Вася", "Петя");
    }

    private static Guid Throw(Scenario s, string title, params int[] dice)
    {
        s.RollTitle("Вася", title).Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(dice).Complete("Вася");
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    /// <summary>start → a → p → b → c → d → finish, p gives 3 points.</summary>
    private static MapGraph BonusMap() => MapBuilder.New().Path("start", "a", "p", "b", "c", "d", "finish").Bonus("p", 3).Build();

    // ---- The points bonus of the move ----

    [Fact]
    public void Fake_run_for_a_points_bonus_loses_the_bonus_too()
    {
        // Given Вася threw 2 onto the bonus cell: 2 + 3 points
        var s = Pool(BonusMap());
        var run = Throw(s, "Silent Hill", 2);
        Assert.Equal(5, s.Player("Вася").Points);

        // When the run is rejected
        s.Act(new RejectProof(run, Comment));

        // Then its dice and the bonus of its stop go in one change, and he is back on the start
        ScenarioAssert.Accepted(s);
        Assert.Equal(new PointsChanged(s.PlayerId("Вася"), -5, PointsReason.ProofRejected, run), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(("start", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Bonus_of_the_move_goes_even_after_a_move_since()
    {
        var s = Pool(BonusMap());
        var run = Throw(s, "Silent Hill", 2);
        Throw(s, "Fatal Frame", 1);
        Assert.Equal(("b", 6), (s.Player("Вася").CellId, s.Player("Вася").Points));

        s.Act(new RejectProof(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(("a", 1), (s.Player("Вася").CellId, s.Player("Вася").Points));
    }

    [Fact]
    public void Bonus_of_a_drop_penalty_stop_stays_with_the_player()
    {
        // Kuon (3 + 2) → d; a drop's penalty 1 + 2 stops on p (+3): that bonus is the drop's, not the run's
        var s = Pool(BonusMap());
        var kuon = Throw(s, "Kuon", 3, 2);
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 2).Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        Assert.Equal(("p", 5), (s.Player("Вася").CellId, s.Player("Вася").Points));

        s.Act(new RejectProof(kuon, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PointsChanged(s.PlayerId("Вася"), -5, PointsReason.ProofRejected, kuon), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(0, s.Player("Вася").Points);
    }

    [Fact]
    public void Run_keeps_the_bonus_its_move_stopped_on()
    {
        var s = Pool(BonusMap());
        var run = Throw(s, "Silent Hill", 2);

        Assert.Equal(3, s.State.Runs[run].CellPoints);
    }

    // ---- «Отклонить со штрафом дропа» ----

    [Fact]
    public void Reject_with_the_drop_penalty_takes_the_move_back_then_pushes_like_a_drop()
    {
        // Given Вася: 3 → c3, then the fake 4 → c7
        var s = Pool();
        Throw(s, "Silent Hill", 3);
        var fake = Throw(s, "Fatal Frame", 4);
        var vasya = s.PlayerId("Вася");

        // When the admin rejects it with the drop penalty: dice 1 and 2
        s.NextRandom(1, 2).Act(new RejectProofWithDropPenalty(fake, Comment));

        // Then the reject's own events, the penalty dice, 3 more points and cells off and the bad event
        ScenarioAssert.Accepted(s);
        var penalized = Assert.Single(s.LastEvents<ProofRejectPenalized>());
        Assert.Equal(new ProofRejectPenalized(fake, vasya, [new Die(4, 1), new Die(4, 2)], s.Clock.UtcNow), penalized);
        Assert.Equal(
            [new PointsChanged(vasya, -4, PointsReason.ProofRejected, fake), new PointsChanged(vasya, -3, PointsReason.DropPenalty, fake)],
            s.LastEvents<PointsChanged>());
        Assert.Equal(
            [
                new PlayerMoved(vasya, "c7", "c3", -4, ["c6", "c5", "c4", "c3"], MoveReason.ProofRejected, fake),
                new PlayerMoved(vasya, "c3", "start", -3, ["c2", "c1", "start"], MoveReason.DropPenalty, fake),
            ],
            s.LastEvents<PlayerMoved>());
        Assert.Contains(s.LastEvents<ManualEffectCreated>(), e => e.DrawEvent == EventKind.Bad && e.Source == ManualEffectSource.Drop);
        Assert.Equal(("start", 0), (s.Player("Вася").CellId, s.Player("Вася").Points));

        // The rejected run stays rejected, not dropped; its game is free again
        Assert.Equal(RunStatus.Rejected, s.State.Runs[fake].Status);
        Assert.DoesNotContain(s.Player("Вася").Exclusions, x => x.GameId == s.State.Runs[fake].GameId);
    }

    [Fact]
    public void Plain_reject_throws_no_penalty()
    {
        var s = Pool();
        var run = Throw(s, "Silent Hill", 3);

        s.Act(new RejectProof(run, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<ProofRejectPenalized>());
        Assert.DoesNotContain(s.LastEvents<PointsChanged>(), p => p.Reason == PointsReason.DropPenalty);
    }

    [Fact]
    public void Penalty_of_a_reject_follows_the_drops_in_a_row()
    {
        // A drop before the fake run: with one extra die per drop in a row the penalty throws 3 dice
        var s = Scenario.New().WithRuleset(r => r with { Drop = r.Drop with { ConsecutiveExtraDice = 1 } }).WithCategory("Horror")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror")
            .WithPlayers("Вася");
        s.RollTitle("Вася", "Silent Hill").Start("Вася").Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        var fake = Throw(s, "Fatal Frame", 4);

        s.Act(new RejectProofWithDropPenalty(fake, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(3, Assert.Single(s.LastEvents<ProofRejectPenalized>()).PenaltyDice.Count);
    }

    [Fact]
    public void Reject_with_the_drop_penalty_needs_a_comment_like_any_reject()
    {
        var s = Pool();
        var run = Throw(s, "Silent Hill", 3);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new RejectProofWithDropPenalty(run, " ")), RejectionCodes.CommentRequired);
    }

    [Fact]
    public void Penalized_reject_is_not_a_drop_of_the_streak()
    {
        // D-324: a rejected run is neither a drop nor a counted completion, with the penalty or without it
        var s = Scenario.New().WithRuleset(r => r with { Drop = r.Drop with { ConsecutiveExtraDice = 1 } }).WithCategory("Horror")
            .WithGame("Silent Hill", 3, "Horror").WithGame("Fatal Frame", 3, "Horror").WithGame("Siren", 3, "Horror").WithGame("Kuon", 3, "Horror")
            .WithPlayers("Вася");
        s.RollTitle("Вася", "Silent Hill").Start("Вася").Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        var fake = Throw(s, "Fatal Frame", 4);
        s.Act(new RejectProofWithDropPenalty(fake, Comment));
        ScenarioAssert.Accepted(s);

        s.RollTitle("Вася", "Siren").Start("Вася").Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        Assert.Equal(3, Assert.Single(s.LastEvents<RunDropped>()).PenaltyDice.Count);
    }

    [Fact]
    public void Undo_of_a_penalized_reject_gives_everything_back()
    {
        var s = Pool();
        Throw(s, "Silent Hill", 3);
        var fake = Throw(s, "Fatal Frame", 4);
        var before = s.State;
        s.NextRandom(1, 2).Act(new RejectProofWithDropPenalty(fake, Comment));
        ScenarioAssert.Accepted(s);

        s.Act(new Engine.Undo.UndoCommand(s.History[^1].CommandId, "Ошибся кнопкой"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(before.Players[s.PlayerId("Вася")] with { PointsTick = s.Player("Вася").PointsTick }, s.Player("Вася"));
        Assert.Equal(before.Runs[fake], s.State.Runs[fake]);
        Assert.Equal(before.ManualEffects, s.State.ManualEffects);
    }
}
