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
