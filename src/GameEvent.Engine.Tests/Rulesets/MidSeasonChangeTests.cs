using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rulesets;

/// <summary>
/// S1 / S2: everything about a run is fixed at roll time, together with the number of the season's ruleset version
/// it was rolled under. A mid-season change (ChangeRuleset) applies to the next roll, never to a rolled game.
/// </summary>
public class MidSeasonChangeTests
{
    private static Func<Ruleset, Ruleset> HoursPerDie(decimal hours) =>
        r => r with { Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { HoursPerDie = hours } } };

    /// <summary>Two 6-hour games; under the pinned hoursPerDie = 3 each gives 2 dice.</summary>
    private static Scenario TwoGames() =>
        Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror")
            .WithPlayers("Вася");

    [Fact]
    public void Roll_snapshot_records_the_ruleset_version_of_the_season()
    {
        var s = TwoGames().Roll("Вася");

        var snapshot = s.Player("Вася").Offer!.Snapshot;
        Assert.Equal(1, snapshot.RulesetVersion);
        Assert.Equal(s.State.Rules.Reward.DiceCount, snapshot.DiceCount);
    }

    [Fact]
    public void Change_after_roll_keeps_the_rolled_version_in_the_offer_and_the_run()
    {
        // Given Вася rolled under version 1
        var s = TwoGames().Roll("Вася");

        // When the admin changes the rules to version 2 before he starts
        s.WithRuleset(HoursPerDie(1));
        s.Start("Вася");

        // Then the run still plays by version 1
        Assert.Equal(2, s.State.RulesetVersion);
        var run = s.State.Runs[s.Player("Вася").ActiveRunId!.Value];
        Assert.Equal(1, run.Snapshot.RulesetVersion);
        Assert.Equal(3, run.Snapshot.DiceCount.HoursPerDie);
    }

    [Fact]
    public void Change_after_roll_does_not_change_the_dice_of_that_game()
    {
        var s = TwoGames().Roll("Вася").WithRuleset(HoursPerDie(1));

        s.Start("Вася").Complete("Вася", Difficulty.Normal);

        Assert.Equal(2, Assert.Single(s.LastEvents<CompletionRolled>()).Dice.Count);
    }

    [Fact]
    public void Roll_after_the_change_takes_the_new_version()
    {
        // Given Вася completed a game rolled under version 1, and then the rules changed
        var s = TwoGames().Roll("Вася").Start("Вася").Complete("Вася");
        s.WithRuleset(HoursPerDie(1));

        // When he rolls the next game and completes it
        s.Roll("Вася");
        var snapshot = s.Player("Вася").Offer!.Snapshot;
        s.Start("Вася").Complete("Вася", Difficulty.Normal);

        // Then the new roll is under version 2: 6 hours / 1 = 6 dice
        Assert.Equal(2, snapshot.RulesetVersion);
        Assert.Equal(1, snapshot.DiceCount.HoursPerDie);
        Assert.Equal(6, Assert.Single(s.LastEvents<CompletionRolled>()).Dice.Count);
    }

    [Fact]
    public void Roll_after_several_changes_takes_the_latest_version()
    {
        var s = TwoGames().WithRuleset(HoursPerDie(1)).WithRuleset(HoursPerDie(2));

        s.Roll("Вася");

        var snapshot = s.Player("Вася").Offer!.Snapshot;
        Assert.Equal(3, snapshot.RulesetVersion);
        Assert.Equal(2, snapshot.DiceCount.HoursPerDie);
    }

    [Fact]
    public void Rejected_change_does_not_affect_the_next_roll()
    {
        var s = TwoGames();
        s.Act(new ChangeRuleset(HoursPerDie(0)(s.Ruleset)));
        Assert.False(s.Last.IsAccepted);

        s.Roll("Вася");

        var snapshot = s.Player("Вася").Offer!.Snapshot;
        Assert.Equal(1, snapshot.RulesetVersion);
        Assert.Equal(3, snapshot.DiceCount.HoursPerDie);
    }
}
