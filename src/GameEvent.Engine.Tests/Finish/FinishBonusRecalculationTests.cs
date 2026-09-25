using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// Finish bonuses are not recalculated retroactively (D-113): a finisher keeps the bonus table in force at his finish; a
/// change of the rules alone moves no bonus. The admin's deliberate «Пересчитать бонусы по текущим правилам» brings every
/// standing finisher to the current table and pays the differences at their places, in the log.
/// </summary>
public class FinishBonusRecalculationTests
{
    [Fact]
    public void Recalculation_brings_every_finisher_to_the_current_list()
    {
        // Вася 1, Петя 2 (+10), Маша 3 (+8) by [10, 8, 6, 4]; the list becomes [20, 15]: Петя 10 → 20, Маша 8 → 15
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        FinishRun(s, "Маша");
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20, 15] } });

        s.Act(new RecalculateFinishBonuses());

        ScenarioAssert.Accepted(s);
        Assert.Equal(new FinishBonusRulesRefreshed(s.State.RulesetVersion, new FinishBonusRules([20, 15], 2)), s.Last.Events[0]);
        Assert.Equal(
            new[] { (s.PlayerId("Петя"), 10, PointsReason.FinishBonus), (s.PlayerId("Маша"), 7, PointsReason.FinishBonus) }.OrderBy(x => x.Item1),
            BonusChanges(s).OrderBy(x => x.Player));
        Assert.Equal((0, 20, 15), (FinishOf(s, "Вася")!.Bonus, FinishOf(s, "Петя")!.Bonus, FinishOf(s, "Маша")!.Bonus));
        Assert.All(Names.Take(3), n => Assert.Equal(new FinishBonusRules([20, 15], 2), FinishOf(s, n)!.BonusRules));
        Assert.Equal((24, 19), (s.Player("Петя").Points, s.Player("Маша").Points));
    }

    [Fact]
    public void A_lower_list_takes_the_difference_back()
    {
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [3] } });

        s.Act(new RecalculateFinishBonuses());

        Assert.Equal([(s.PlayerId("Петя"), -7, PointsReason.FinishBonusRevoked)], BonusChanges(s));
        Assert.Equal((3, 7), (FinishOf(s, "Петя")!.Bonus, s.Player("Петя").Points));
    }

    [Fact]
    public void After_a_recalculation_a_revoke_pays_the_new_place_by_the_refreshed_list()
    {
        var s = New();
        FinishRun(s, "Вася");
        var petyaRun = FinishRun(s, "Петя");
        FinishRun(s, "Маша");
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [30, 1] } });
        s.Act(new RecalculateFinishBonuses());
        Assert.Equal(1, FinishOf(s, "Маша")!.Bonus);

        Reject(s, petyaRun);

        Assert.Equal(30, FinishOf(s, "Маша")!.Bonus);
    }

    [Fact]
    public void A_list_unchanged_since_every_finish_leaves_nothing_to_recalculate()
    {
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new RecalculateFinishBonuses()), RejectionCodes.FinishNothingToRecalculate);
    }

    [Fact]
    public void Without_finishers_there_is_nothing_to_recalculate()
    {
        var s = New();
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20] } });

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new RecalculateFinishBonuses()), RejectionCodes.FinishNothingToRecalculate);
    }

    [Fact]
    public void A_second_recalculation_finds_nothing_to_do()
    {
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20] } });
        s.Act(new RecalculateFinishBonuses());

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new RecalculateFinishBonuses()), RejectionCodes.FinishNothingToRecalculate);
    }

    [Fact]
    public void Only_the_finish_list_counts_for_a_recalculation()
    {
        // Another rule changed, the finish list did not: every finisher already holds the current list
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        s.WithRuleset(r => r with { Finish = r.Finish with { RequireApprovalForFirst = !r.Finish.RequireApprovalForFirst } });

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new RecalculateFinishBonuses()), RejectionCodes.FinishNothingToRecalculate);
    }

    [Fact]
    public void The_frozen_first_stays_without_a_bonus()
    {
        var s = New();
        FrozenFirst(s, "Вася");
        var points = s.Player("Вася").Points;
        FinishRun(s, "Петя");
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20] } });

        s.Act(new RecalculateFinishBonuses());

        Assert.DoesNotContain(BonusChanges(s), c => c.Player == s.PlayerId("Вася"));
        Assert.Equal((0, points, true), (FinishOf(s, "Вася")!.Bonus, s.Player("Вася").Points, FinishOf(s, "Вася")!.Frozen));
    }

    [Fact]
    public void With_only_the_first_finished_there_is_nothing_to_recalculate()
    {
        // The first holds no bonus at his place: his list changes nothing
        var s = New();
        FinishRun(s, "Вася");
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20] } });

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new RecalculateFinishBonuses()), RejectionCodes.FinishNothingToRecalculate);
    }

    [Fact]
    public void The_recalculation_does_not_freeze_the_first()
    {
        // Approval for the first is switched off while Вася's finish is provisional: the bonus button leaves him as he is
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        s.WithRuleset(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false, BonusByOrder = [20] } });

        s.Act(new RecalculateFinishBonuses());

        ScenarioAssert.Accepted(s);
        Assert.DoesNotContain(s.Last.Events, e => e is PlayerFrozen);
        Assert.False(FinishOf(s, "Вася")!.Frozen);
        Assert.Equal(20, FinishOf(s, "Петя")!.Bonus);
    }

    [Fact]
    public void A_draft_season_has_nobody_to_recalculate()
    {
        var s = New();
        var draft = s.State with { Status = SeasonStatus.Draft };

        var result = SeasonEngine.Execute(draft, new RecalculateFinishBonuses(), s.Context());

        Assert.Equal(RejectionCodes.SeasonNotActive, result.Decision.Rejection!.Code);
    }

    [Theory]
    [InlineData(SeasonStatus.Finished)]
    [InlineData(SeasonStatus.Archived)]
    public void After_the_season_is_finished_the_bonuses_are_fixed(SeasonStatus status)
    {
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20] } });
        s.MoveStatusToForcingFinish(status);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new RecalculateFinishBonuses()), RejectionCodes.SeasonClosed);
    }

    [Fact]
    public void A_recalculation_is_allowed_while_the_season_is_closing()
    {
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20] } });
        s.MoveStatusTo(SeasonStatus.Closing);

        s.Act(new RecalculateFinishBonuses());

        ScenarioAssert.Accepted(s);
        Assert.Equal(20, FinishOf(s, "Петя")!.Bonus);
    }

    [Fact]
    public void Before_the_season_exists_there_is_nothing_to_recalculate()
    {
        var s = Scenario.New().AsDraft();
        var empty = SeasonEngine.Execute(SeasonState.Empty, new RecalculateFinishBonuses(), s.Context());

        Assert.Equal(RejectionCodes.SeasonNotCreated, empty.Decision.Rejection!.Code);
    }

    [Fact]
    public void Undoing_the_recalculation_brings_back_the_bonuses_and_the_lists()
    {
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        var before = FinishOf(s, "Петя");
        var points = s.Player("Петя").Points;
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20] } });
        s.Act(new RecalculateFinishBonuses());

        s.Act(new UndoCommand(s.LastCommandId, "ошибка админа"));

        ScenarioAssert.Accepted(s);
        Assert.Equal((before, points), (FinishOf(s, "Петя"), s.Player("Петя").Points));
    }

    [Fact]
    public void Replaying_the_log_gives_the_same_lists_and_bonuses()
    {
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        s.WithRuleset(r => r with { Finish = r.Finish with { BonusByOrder = [20, 15] } });
        FinishRun(s, "Маша");
        s.Act(new RecalculateFinishBonuses());

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }
}
