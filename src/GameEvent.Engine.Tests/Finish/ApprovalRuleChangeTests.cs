using GameEvent.Engine.Finish;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// «Одобрение первого» changed mid-season (D-115, by the principle of D-113): a finisher keeps the rule in force when he
/// finished. A provisional first stays provisional until his runs are approved, even if the rule is switched off; a first
/// frozen at once stays frozen if it is switched on. Nobody is frozen at an unrelated moment.
/// </summary>
public class ApprovalRuleChangeTests
{
    [Fact]
    public void Switching_approval_off_does_not_freeze_a_provisional_first()
    {
        // Вася finished first with approval required; the admin switches it off; Петя finishes second
        var s = New();
        var vasyaRun = FinishRun(s, "Вася");
        s.WithRuleset(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } });

        FinishRun(s, "Петя");

        Assert.DoesNotContain(s.Last.Events, e => e is PlayerFrozen);
        Assert.False(FinishOf(s, "Вася")!.Frozen);
        Assert.Equal(true, FinishOf(s, "Вася")!.ApprovalRequired);

        // His own runs approved — then he is frozen, as the rule at his finish says
        Approve(s, vasyaRun);

        Assert.True(FinishOf(s, "Вася")!.Frozen);
    }

    [Fact]
    public void Switching_approval_on_leaves_a_first_frozen_at_once_frozen()
    {
        var s = New(ruleset: r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } });
        var vasyaRun = FinishRun(s, "Вася");
        Assert.True(FinishOf(s, "Вася")!.Frozen);

        s.WithRuleset(r => r with { Finish = r.Finish with { RequireApprovalForFirst = true } });
        Reject(s, vasyaRun);

        // D-99: frozen without required approval, he keeps his final place; the later rule does not reach back
        Assert.NotNull(FinishOf(s, "Вася"));
        Assert.True(FinishOf(s, "Вася")!.Frozen);
        Assert.Equal(false, FinishOf(s, "Вася")!.ApprovalRequired);
    }

    [Fact]
    public void A_first_who_finished_after_the_switch_follows_the_new_rule()
    {
        var s = New();
        s.WithRuleset(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } });

        FinishRun(s, "Вася");

        ScenarioAssert.Accepted(s);
        Assert.True(FinishOf(s, "Вася")!.Frozen);
    }
}
