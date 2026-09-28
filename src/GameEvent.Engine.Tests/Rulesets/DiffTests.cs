using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rulesets;

/// <summary>
/// C3: «было/стало» for the rules history page. RulesetDiff compares two versions leaf by leaf: the path is the
/// camelCase JSON path (array elements with an index), values are JSON text in the engine format, null when the
/// field does not exist on that side. Changes come sorted by path.
/// </summary>
public class DiffTests
{
    private static readonly Ruleset s_base = TestRuleset.Create();

    [Fact]
    public void Equal_rulesets_have_no_changes()
    {
        Assert.Empty(RulesetDiff.Between(s_base, TestRuleset.Create()));
    }

    [Fact]
    public void Changed_number_is_one_leaf_change()
    {
        var after = s_base with { Reward = s_base.Reward with { DiceCount = s_base.Reward.DiceCount with { Max = 12 } } };

        var changes = RulesetDiff.Between(s_base, after);

        Assert.Equal([new RulesetChange("reward.diceCount.max", "10", "12")], changes);
    }

    [Fact]
    public void Changed_enum_and_flag_are_json_text()
    {
        var after = s_base with
        {
            Reward = s_base.Reward with { DiceCount = s_base.Reward.DiceCount with { Rounding = Rounding.Floor } },
            Drop = s_base.Drop with { AffectsPosition = false },
        };

        var changes = RulesetDiff.Between(s_base, after);

        Assert.Equal(
            [
                new RulesetChange("drop.affectsPosition", "true", "false"),
                new RulesetChange("reward.diceCount.rounding", "\"nearest\"", "\"floor\""),
            ],
            changes);
    }

    [Fact]
    public void Changed_array_element_has_an_index_in_the_path()
    {
        var after = s_base with { Finish = s_base.Finish with { BonusByOrder = [10, 7, 6, 4] } };

        var changes = RulesetDiff.Between(s_base, after);

        Assert.Equal([new RulesetChange("finish.bonusByOrder[1]", "8", "7")], changes);
    }

    [Fact]
    public void Longer_array_shows_the_added_elements_as_new()
    {
        var after = s_base with { Finish = s_base.Finish with { BonusByOrder = [10, 8, 6, 4, 2] } };

        var changes = RulesetDiff.Between(s_base, after);

        Assert.Equal([new RulesetChange("finish.bonusByOrder[4]", null, "2")], changes);
    }

    [Fact]
    public void Shorter_array_shows_the_removed_elements_as_gone()
    {
        var after = s_base with { Finish = s_base.Finish with { BonusByOrder = [10, 8] } };

        var changes = RulesetDiff.Between(s_base, after);

        Assert.Equal(
            [
                new RulesetChange("finish.bonusByOrder[2]", "6", null),
                new RulesetChange("finish.bonusByOrder[3]", "4", null),
            ],
            changes);
    }

    [Fact]
    public void Array_of_objects_is_compared_down_to_the_leaves()
    {
        var steps = s_base.Bets.PayoutByHoursPerDay.ToArray();
        steps[1] = steps[1] with { Multiplier = 2.5m };
        var after = s_base with { Bets = s_base.Bets with { PayoutByHoursPerDay = [.. steps] } };

        var change = Assert.Single(RulesetDiff.Between(s_base, after));

        Assert.Equal("bets.payoutByHoursPerDay[1].multiplier", change.Path);
        Assert.Equal("2.5", change.After);
    }

    [Fact]
    public void Replaced_nested_object_unfolds_to_its_leaves()
    {
        // extreme was { sides: 6, grantEvent: good }
        var after = s_base with
        {
            Reward = s_base.Reward with
            {
                DieByDifficulty = s_base.Reward.DieByDifficulty with { Extreme = new DieRule { Sides = 8, GrantEvent = null } },
            },
        };

        var changes = RulesetDiff.Between(s_base, after);

        // Nulls are written explicitly in the engine format (D-48), so grantEvent exists on both sides
        Assert.Equal(
            [
                new RulesetChange("reward.dieByDifficulty.extreme.grantEvent", "\"good\"", "null"),
                new RulesetChange("reward.dieByDifficulty.extreme.sides", "6", "8"),
            ],
            changes);
    }

    [Fact]
    public void Nullable_value_becoming_null_is_a_change_to_json_null()
    {
        var after = s_base with { Roll = s_base.Roll with { RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent, Amount = null } } };

        var changes = RulesetDiff.Between(s_base, after);

        Assert.Equal(
            [
                new RulesetChange("roll.rerollCost.amount", "5", "null"),
                new RulesetChange("roll.rerollCost.kind", "\"coins\"", "\"badEvent\""),
            ],
            changes);
    }

    [Fact]
    public void Changes_come_sorted_by_path()
    {
        // Changed in the document order: reward, drop, bets, weeklyChallenge
        var after = s_base with
        {
            Reward = s_base.Reward with { DiceCount = s_base.Reward.DiceCount with { Max = 12 } },
            Drop = s_base.Drop with { PenaltyDice = s_base.Drop.PenaltyDice with { Sides = 6 } },
            Bets = s_base.Bets with { MaxStake = 20 },
            WeeklyChallenge = s_base.WeeklyChallenge with { DefaultRewardCoins = 15 },
        };

        var paths = RulesetDiff.Between(s_base, after).Select(c => c.Path).ToList();

        Assert.Equal(
            ["bets.maxStake", "drop.penaltyDice.sides", "reward.diceCount.max", "weeklyChallenge.defaultRewardCoins"],
            paths);
    }

    [Fact]
    public void Diff_is_symmetric_in_before_and_after()
    {
        var after = s_base with { Finish = s_base.Finish with { BonusByOrder = [10, 8], BonusAfterList = 1 } };

        var forward = RulesetDiff.Between(s_base, after);
        var backward = RulesetDiff.Between(after, s_base);

        Assert.Equal(forward.Select(c => new RulesetChange(c.Path, c.After, c.Before)), backward);
    }
}
