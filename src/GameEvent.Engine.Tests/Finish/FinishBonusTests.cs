using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Finish.FinishSetup;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// Finishers after the first hold a decreasing bonus (P6; SPEC «Финишировавшие не первыми получают убывающий бонус …
/// бонус выдаётся один раз»; Q-4, D-99): among the standing finishers ordered by their order number, place 1 (the first)
/// holds nothing, place 2 <c>finish.bonusByOrder[0]</c>, place 3 <c>[1]</c>…, past the list <c>finish.bonusAfterList</c>
/// (<see cref="FinishLine.Bonus"/>). It is paid as <c>PointsChanged(+, FinishBonus)</c> in the command of the finish and
/// kept in <see cref="FinishState.Bonus"/>: at any moment a player holds at most one bonus. Orders are 1, 2, 3… and are
/// never reused. Recalculation after a revoke — <see cref="FirstPlaceReassignTests"/>.
/// </summary>
public class FinishBonusTests
{
    [Fact]
    public void Second_finisher_gets_the_first_bonus_of_the_list()
    {
        var s = New();
        FinishRun(s, "Вася");
        var petya = s.PlayerId("Петя");

        var (runId, at) = Complete(s, "Петя", [3, 1]);

        Assert.Equal(new PlayerFinished(petya, runId, 2, at, 0), Assert.Single(s.LastEvents<PlayerFinished>()));
        Assert.Equal([(petya, 10, PointsReason.FinishBonus)], BonusChanges(s));
        Assert.True(IndexOf<PointsChanged>(s, e => e.Reason == PointsReason.FinishBonus) > IndexOf<PlayerFinished>(s), "The bonus follows the finish.");

        // 4 for the dice and 10 for the finish; the position is the finish; the bonus is held in the finish
        Assert.Equal((14, LinearMap.FinishId), (s.Player("Петя").Points, s.Player("Петя").CellId));
        Assert.Equal(new FinishState(2, runId, at, Frozen: false, Bonus: 10, Surplus: 0), FinishOf(s, "Петя"));
    }

    [Fact]
    public void Bonuses_follow_the_list_and_then_the_value_after_it()
    {
        // Seven finishers in turn: the first none, then 10, 8, 6, 4, then 2 and 2; nobody else's bonus moves
        var s = New(players: 7);
        int[] expected = [0, 10, 8, 6, 4, 2, 2];

        for (var i = 0; i < Names.Length; i++)
        {
            var (runId, at) = Complete(s, Names[i], [3, 1]);
            var player = s.PlayerId(Names[i]);

            Assert.Equal(new PlayerFinished(player, runId, i + 1, at, 0), Assert.Single(s.LastEvents<PlayerFinished>()));
            List<(Guid, int, PointsReason)> bonuses = expected[i] > 0 ? [(player, expected[i], PointsReason.FinishBonus)] : [];
            Assert.Equal(bonuses, BonusChanges(s));
            Assert.Equal(4 + expected[i], s.Player(Names[i]).Points);
        }

        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));
        Assert.Equal(Enumerable.Range(1, 7), Names.Select(n => FinishOf(s, n)!.Order));
        Assert.Equal(expected, Names.Select(n => FinishOf(s, n)!.Bonus));
    }

    [Fact]
    public void Bonus_is_paid_once()
    {
        // Петя finished second (+10); his next completion adds its dice only
        var s = New();
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        var finish = FinishOf(s, "Петя");

        Complete(s, "Петя", [2, 2]);

        Assert.Empty(s.LastEvents<PlayerFinished>());
        Assert.Empty(BonusChanges(s));
        Assert.Equal(finish, FinishOf(s, "Петя"));
        Assert.Equal(4 + 10 + 4, s.Player("Петя").Points);
        Assert.Single(s.Log.OfType<PointsChanged>(), e => e.PlayerId == s.PlayerId("Петя") && e.Reason == PointsReason.FinishBonus);
    }

    [Fact]
    public void Zero_bonus_writes_no_points_change()
    {
        // bonusByOrder [10], bonusAfterList 0: the third finisher finishes without a bonus event (zero changes are not logged)
        var s = New(ruleset: r => r with { Finish = r.Finish with { BonusByOrder = [10], BonusAfterList = 0 } });
        FinishRun(s, "Вася");
        FinishRun(s, "Петя");
        Assert.Equal([(s.PlayerId("Петя"), 10, PointsReason.FinishBonus)], BonusChanges(s));

        var (runId, at) = Complete(s, "Маша", [3, 1]);

        Assert.Equal(new PlayerFinished(s.PlayerId("Маша"), runId, 3, at, 0), Assert.Single(s.LastEvents<PlayerFinished>()));
        Assert.Empty(BonusChanges(s));
        Assert.Equal((4, 0), (s.Player("Маша").Points, FinishOf(s, "Маша")!.Bonus));
    }

    [Fact]
    public void Empty_list_gives_every_later_finisher_the_value_after_it()
    {
        var s = New(ruleset: r => r with { Finish = r.Finish with { BonusByOrder = [], BonusAfterList = 3 } });
        FinishRun(s, "Вася");

        FinishRun(s, "Петя");

        Assert.Equal([(s.PlayerId("Петя"), 3, PointsReason.FinishBonus)], BonusChanges(s));
    }

    [Fact]
    public void Bonus_goes_to_the_finisher_only()
    {
        var s = New();
        FinishRun(s, "Вася");
        var others = s.State.Players.Values.Where(p => p.PlayerId != s.PlayerId("Петя")).ToList();

        FinishRun(s, "Петя");

        Assert.All(s.LastEvents<PointsChanged>(), e => Assert.Equal(s.PlayerId("Петя"), e.PlayerId));
        Assert.All(others, p => Assert.Equal(p, s.State.Players[p.PlayerId]));
    }

    // ---- FinishLine.Bonus ----

    private static FinishRules Rules(int[] list, int after) =>
        new() { RequireApprovalForFirst = true, BonusByOrder = [.. list], BonusAfterList = after };

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 10)]
    [InlineData(3, 8)]
    [InlineData(4, 6)]
    [InlineData(5, 4)]
    [InlineData(6, 2)]
    [InlineData(40, 2)]
    public void Bonus_by_place_among_the_standing_finishers(int place, int bonus) =>
        Assert.Equal(bonus, FinishLine.Bonus(Rules([10, 8, 6, 4], 2), place));

    [Fact]
    public void Bonus_with_an_empty_list_is_the_value_after_it_for_everyone_but_the_first()
    {
        var rules = Rules([], 5);

        Assert.Equal((0, 5, 5), (FinishLine.Bonus(rules, 1), FinishLine.Bonus(rules, 2), FinishLine.Bonus(rules, 9)));
    }

    [Fact]
    public void Finish_rules_of_the_test_ruleset_are_the_ones_these_tests_rely_on()
    {
        var rules = TestRuleset.Create().Finish;

        Assert.True(rules.RequireApprovalForFirst);
        Assert.Equal((EquatableArray<int>)[10, 8, 6, 4], rules.BonusByOrder);
        Assert.Equal(2, rules.BonusAfterList);
    }
}
