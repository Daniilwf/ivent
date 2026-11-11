using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// «Отклонить со штрафом дропа» around the finish (D-327 with D-99): the frozen first loses nothing and throws no penalty;
/// a later finisher pays the penalty in points only, his position is fixed.
/// </summary>
public class FinishRejectPenaltyTests
{
    [Fact]
    public void Frozen_first_gets_no_penalty()
    {
        var s = New();
        FrozenFirst(s, "Вася");
        var after = Complete(s, "Вася", [1, 1]).RunId;
        var points = s.Player("Вася").Points;

        s.Act(new RejectProofWithDropPenalty(after, "Обман"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<ProofRejectPenalized>());
        Assert.Equal(points, s.Player("Вася").Points);
    }

    [Fact]
    public void Later_finisher_pays_the_penalty_in_points_only()
    {
        // Вася finishes first, Петя second; Петя's run after his finish is rejected with the penalty
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        var after = Complete(s, "Петя", [1, 1]).RunId;
        var points = s.Player("Петя").Points;

        s.NextRandom(2, 3).Act(new RejectProofWithDropPenalty(after, "Обман"));

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<ProofRejectPenalized>());
        Assert.Contains(new PointsChanged(s.PlayerId("Петя"), -5, PointsReason.DropPenalty, after), s.LastEvents<PointsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Equal((LinearMap.FinishId, points - 2 - 5), (s.Player("Петя").CellId, s.Player("Петя").Points));
    }
}

/// <summary>
/// The preview of «Отклонить со штрафом дропа» the proof queue shows before the admin confirms (D-327): it says what the
/// penalty will take on the state the reject leaves, as the reject itself does.
/// </summary>
public class RejectPenaltyPreviewTests
{
    [Fact]
    public void Without_a_finish_the_penalty_takes_points_and_cells()
    {
        var s = New();
        var run = Complete(s, "Вася", [1, 1]).RunId;

        var preview = RejectPenaltyPreview.Of(s.State, s.State.Runs[run]);

        Assert.Equal(new RejectPenalty(2, 4, true, true, true), preview);
    }

    [Fact]
    public void Frozen_first_gets_no_preview()
    {
        var s = New();
        FrozenFirst(s, "Вася");
        var after = Complete(s, "Вася", [1, 1]).RunId;

        Assert.Null(RejectPenaltyPreview.Of(s.State, s.State.Runs[after]));
    }

    [Fact]
    public void Later_finishers_run_after_the_finish_takes_no_cells()
    {
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        var after = Complete(s, "Петя", [1, 1]).RunId;

        Assert.False(RejectPenaltyPreview.Of(s.State, s.State.Runs[after])!.AffectsPosition);
    }

    [Fact]
    public void A_surplus_that_covers_an_earlier_run_keeps_the_finish_and_the_penalty_takes_no_cells()
    {
        // Вася: 1 + 1 → c2, then 4 + 4 from c2 — two steps to the finish, surplus 6; the reject of the first run takes 2
        // against it, the finish stands and the penalty cannot move him
        var s = New();
        var earlier = Complete(s, "Вася", [1, 1]).RunId;
        Complete(s, "Вася", [4, 4]);
        Assert.Equal(6, s.Player("Вася").Finish!.Surplus);

        var preview = RejectPenaltyPreview.Of(s.State, s.State.Runs[earlier]);

        Assert.False(preview!.AffectsPosition);
        s.NextRandom(2, 3).Act(new RejectProofWithDropPenalty(earlier, Comment));
        ScenarioAssert.Accepted(s);
        Assert.NotNull(s.Player("Вася").Finish);
        Assert.Equal(LinearMap.FinishId, s.Player("Вася").CellId);
        Assert.DoesNotContain(s.LastEvents<PlayerMoved>(), m => m.Reason == MoveReason.DropPenalty);
    }

    [Fact]
    public void A_surplus_short_of_an_earlier_run_revokes_the_finish_and_the_penalty_takes_cells()
    {
        // Вася: 1 + 1 → c2, then 2 + 1 — surplus 1; the reject of the first run takes 2, the finish goes
        var s = New();
        var earlier = Complete(s, "Вася", [1, 1]).RunId;
        Complete(s, "Вася", [2, 1]);
        Assert.Equal(1, s.Player("Вася").Finish!.Surplus);

        Assert.True(RejectPenaltyPreview.Of(s.State, s.State.Runs[earlier])!.AffectsPosition);
    }
}
