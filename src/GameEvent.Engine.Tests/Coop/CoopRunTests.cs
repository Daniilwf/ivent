using GameEvent.Engine.Content;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Coop.CoopTestScenario;

namespace GameEvent.Engine.Tests.Coop;

/// <summary>
/// A coop run is one run of the roller with a partner (EV25, EV27, EV44; D-520, D-521, D-522, invariant 4): the game is
/// taken while it runs and completed for everybody after; the partner has no run of his own; only the roller completes,
/// drops or tech-rerolls it; one throw by the roller's pipeline — the partner's pending changes of the throw wait for his
/// own next throw (S5.10-13, S5.10-26, Н-24); one proof, one approval for both. Scenarios S5.10-01, -13, -21, -26.
/// </summary>
public class CoopRunTests
{
    [Fact]
    public void A_coop_run_is_one_run()
    {
        var s = New();
        var portal = s.GameId(CoopGame);
        var run = s.CoopRun();

        // While it runs: one run of the game, the roller's, which the partner plays too
        Assert.Equal([run], s.State.Runs.Values.Where(r => r.GameId == portal).Select(r => r.RunId));
        Assert.DoesNotContain(s.State.Runs.Values, r => r.PlayerId == s.PlayerId("Петя"));
        Assert.Equal(run, s.Player("Петя").ActiveRunId);

        s.CompleteWith("Вася", 3, 4);

        // Completed: still one run, both free
        var completed = Assert.Single(s.State.Runs.Values, r => r.GameId == portal);
        Assert.Equal((run, RunStatus.Completed, s.PlayerId("Вася")), (completed.RunId, completed.Status, completed.PlayerId));
        Assert.DoesNotContain(s.State.Runs.Values, r => r.PlayerId == s.PlayerId("Петя"));
        foreach (var player in new[] { "Вася", "Петя" })
        {
            Assert.Equal((TurnPhase.Idle, (Guid?)null), (s.Player(player).Phase, s.Player(player).ActiveRunId));
        }
    }

    [Fact]
    public void The_coop_game_is_taken_for_everybody_while_it_runs()
    {
        var s = New();
        var portal = s.GameId(CoopGame);
        s.CoopRun();

        Assert.Contains(PoolStats.Taken(s.State, Guid.Empty), m => m.GameId == portal && m.Reason == RollMissReason.BeingPlayed);

        // The draw lands on «Portal 2» (first in the pool): a miss, Маша is offered another game
        s.NextRandom(0, 0).Roll("Маша");
        Assert.NotEqual(portal, s.Player("Маша").Offer!.GameId);
    }

    [Fact]
    public void After_the_completion_the_coop_game_is_completed_for_all_and_the_partner_never_gets_a_run_of_it()
    {
        var s = New();
        var portal = s.GameId(CoopGame);
        s.CoopRun();
        s.CompleteWith("Вася", 3, 4);

        Assert.Contains(PoolStats.Taken(s.State, Guid.Empty), m => m.GameId == portal && m.Reason == RollMissReason.CompletedInSeason);

        // EV44: the partner's own roll misses it like anybody's
        s.NextRandom(0, 0).Roll("Петя");
        Assert.NotEqual(portal, s.Player("Петя").Offer!.GameId);
        s.NextRandom(0, 0).Roll("Маша");
        Assert.NotEqual(portal, s.Player("Маша").Offer!.GameId);
        Assert.Single(s.State.Runs.Values, r => r.GameId == portal);
    }

    [Fact]
    public void The_partner_neither_completes_nor_drops_nor_tech_rerolls_the_coop_run()
    {
        var s = New();
        s.CoopRun();
        var petya = s.PlayerId("Петя");

        s.Rejects(new CompleteRun(petya, Difficulty.Normal, null, null, false, null), RejectionCodes.CoopRollerOnly);
        s.Rejects(new DropRun(petya), RejectionCodes.CoopRollerOnly);
        s.Rejects(new TechReroll(petya, TechRerollReason.DoesNotLaunch, null), RejectionCodes.CoopRollerOnly);
    }

    [Fact]
    public void The_partner_takes_the_rollers_snapshot_as_it_is()
    {
        var s = New();
        var run = s.PlayTitle("Вася", CoopGame);
        var snapshot = s.State.Runs[run].Snapshot;

        s.Accept("Петя", s.Invite("Вася", "Петя"));

        Assert.Equal(snapshot, s.State.Runs[run].Snapshot);
    }

    [Fact]
    public void The_completion_throws_once_for_both()
    {
        var s = New();
        var run = s.CoopRun();

        s.NextRandom(3, 4).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var rolled = Assert.Single(s.LastEvents<CompletionRolled>());
        Assert.Equal((run, s.PlayerId("Вася")), (rolled.RunId, rolled.PlayerId));
        Assert.Equal([new Die(4, 3), new Die(4, 4)], s.State.Runs[run].Dice);
        Assert.Single(s.LastEvents<RunCompleted>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_throw_goes_by_the_rollers_pipeline_and_the_partners_pending_change_waits(bool hostile)
    {
        // S5.10-13, S5.10-26 (Н-24): Вася holds an extra die for his next throw, Петя «+5» (his own, or Маша's curse-like
        // hostile change): the shared throw is Вася's — his extra die counts, Петя's change waits for Петя's own next throw
        var s = New(r => r with { Features = r.Features with { Items = true } });
        var run = s.CoopRun();
        var petyasChange = new DiceModifier("charm", DiceStage.Add, ContentValue.Of(5), Hostile: hostile);
        s.AppendCraftedEvents(
            new NextDiceModified(s.PlayerId("Вася"), new DiceModifier("extra-die", DiceStage.Count, ContentValue.Of(1))),
            new NextDiceModified(s.PlayerId("Петя"), petyasChange));

        s.NextRandom(1, 2, 3).Complete("Вася");

        ScenarioAssert.Accepted(s);
        var split = Assert.Single(s.LastEvents<CoopRewardSplit>());
        Assert.Equal((run, 6), (split.RunId, split.Total));
        Assert.Equal(6, s.Player("Вася").Points + s.Player("Петя").Points);
        Assert.Empty(s.Player("Вася").Wallet.NextDice);
        Assert.Equal([petyasChange], s.Player("Петя").Wallet.NextDice);
    }

    [Fact]
    public void One_approval_serves_both()
    {
        var s = New();
        var run = s.CoopRun();
        s.CompleteWith("Вася", 3, 4);

        s.Act(new ApproveProof(run, null, "Видел на стриме"));

        ScenarioAssert.Accepted(s);
        var approved = Assert.Single(s.LastEvents<ProofApproved>());
        Assert.Equal((run, s.PlayerId("Вася")), (approved.RunId, approved.PlayerId));
        s.Rejects(new ApproveProof(run, null, "Ещё раз"), RejectionCodes.ProofAlreadyReviewed);

        // The approval took nothing back from either
        Assert.Equal((4, 3), (s.Player("Вася").Points, s.Player("Петя").Points));
    }
}
