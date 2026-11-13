using GameEvent.Engine.Finish;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// INV-2: an approval at a lower difficulty (Q-5, D-98) that revokes the first's finish makes the next finisher first;
/// if all his runs up to the finish are approved he is frozen at once (D-99) — and, as every freeze of an approval (Q-3),
/// <see cref="PlayerFrozen"/> is the last event of the command, after <see cref="ProofApproved"/>, not in the middle of
/// the difficulty change.
/// </summary>
public class LoweredApprovalFreezeTests
{
    [Fact]
    public void Approval_at_a_lower_difficulty_revoking_the_first_freezes_the_next_after_the_approval()
    {
        var s = New();
        var (petya, masha) = (s.PlayerId("Петя"), s.PlayerId("Маша"));

        // Петя first on hard d6 3 + 1, Маша second (bonus 10) with her run approved — not first, so not frozen
        var (petyaRun, _) = Complete(s, "Петя", [3, 1], Difficulty.Hard);
        var mashaRun = FinishRun(s, "Маша");
        Approve(s, mashaRun);
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<PlayerFrozen>());

        // Proven normal: d6 3, 1 → d4 2, 1 — one step short, Петя's finish is revoked and Маша becomes first
        s.Act(new ApproveProof(petyaRun, Difficulty.Normal, "На скрине нормальная сложность"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new PlayerFinishRevoked(petya, petyaRun), Assert.Single(s.LastEvents<PlayerFinishRevoked>()));
        Assert.Equal(masha, FinishLine.First(s.State));
        Assert.Equal(
            [new ProofApproved(petyaRun, petya, true, "На скрине нормальная сложность", s.Clock.UtcNow), new PlayerFrozen(masha)],
            s.Last.Events.TakeLast(2));
        Assert.Single(s.LastEvents<PlayerFrozen>());
        Assert.True(FinishOf(s, "Маша")!.Frozen);
        Assert.Equal((0, LinearMap.FinishId), (FinishOf(s, "Маша")!.Bonus, s.Player("Маша").CellId));
        Assert.Equal((3, "c3"), (s.Player("Петя").Points, s.Player("Петя").CellId));
    }
}
