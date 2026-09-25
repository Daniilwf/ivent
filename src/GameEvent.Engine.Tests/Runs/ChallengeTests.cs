using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// The challenge of a game (W3; SPEC «Награда за прохождение»; D-13, D-14, D-96): a claimed challenge adds
/// <c>reward.challengeBonus.extraDice</c> dice of the same type as the difficulty die, fixed in the snapshot at the roll.
/// They are rolled after the dice by hours and stored apart (<see cref="CompletionRolled.ChallengeDice"/>); points and
/// steps are the sum of all dice. Test ruleset: hoursPerDie 3, normal d4, hard/extreme d6, easy d2, extraDice 1.
/// The claim is closed by <c>features.challenges</c> (D-96 (1)); these scenarios turn it on unless stated otherwise.
/// </summary>
public class ChallengeTests
{
    private static Scenario Playing(decimal? hours = 6, Func<Ruleset, Ruleset>? ruleset = null, bool challenges = true)
    {
        var s = Scenario.New();
        if (challenges)
        {
            s.WithRuleset(ChallengesOn);
        }

        if (ruleset is not null)
        {
            s.WithRuleset(ruleset);
        }

        s.WithCategory("Horror").WithGame("Silent Hill", hours, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);
        return s;
    }

    /// <summary>D-96 (1): claiming a challenge is closed by <c>features.challenges</c>, off by default.</summary>
    private static Ruleset ChallengesOn(Ruleset r) => r with { Features = r.Features with { Challenges = true } };

    private static Func<Ruleset, Ruleset> ExtraDice(int extraDice) =>
        r => r with { Reward = r.Reward with { ChallengeBonus = new ChallengeBonus { ExtraDice = extraDice } } };

    private static CompletionRolled DiceOf(Scenario s) => Assert.Single(s.LastEvents<CompletionRolled>());

    [Fact]
    public void Roll_snapshot_fixes_the_challenge_bonus()
    {
        var s = Scenario.New(ruleset: ExtraDice(2)(TestRuleset.Create()))
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася");

        Assert.Equal(2, s.Player("Вася").Offer!.Snapshot.ChallengeExtraDice);
    }

    [Fact]
    public void Claimed_challenge_adds_extra_dice_of_the_same_type_apart_from_the_dice_by_hours()
    {
        // Given a 6-hour game (2 dice) on normal (d4) and a challenge worth 1 extra die
        var s = Playing(6);
        var vasya = s.PlayerId("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;

        // When Вася completes it with the challenge: the dice by hours show 3 and 1, the challenge die 4
        s.NextRandom(3, 1, 4).Complete("Вася", Difficulty.Normal, challengeDone: true);

        // Then the challenge die is stored apart; points and steps are the sum of all three
        ScenarioAssert.Accepted(s);
        Assert.Equal(new CompletionRolled(runId, vasya, [new Die(4, 3), new Die(4, 1)], [new Die(4, 4)]), DiceOf(s));
        Assert.Equal(new PointsChanged(vasya, 8, PointsReason.CompletionRoll, runId), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(8, Assert.Single(s.LastEvents<PlayerMoved>()).Steps);
        Assert.True(Assert.Single(s.LastEvents<RunCompleted>()).ChallengeDone);

        var run = s.State.Runs[runId];
        Assert.Equal([new Die(4, 3), new Die(4, 1)], run.Dice);
        Assert.Equal([new Die(4, 4)], run.ChallengeDice);
        Assert.Equal(8, s.Player("Вася").Points);
        Assert.Equal("c8", s.Player("Вася").CellId);
    }

    [Theory]
    [InlineData(Difficulty.Easy, 2)]
    [InlineData(Difficulty.Normal, 4)]
    [InlineData(Difficulty.Hard, 6)]
    [InlineData(Difficulty.Extreme, 6)]
    public void Challenge_dice_follow_the_difficulty_die(Difficulty difficulty, int sides)
    {
        var s = Playing(6, ExtraDice(2));

        s.Complete("Вася", difficulty, challengeDone: true);

        ScenarioAssert.Accepted(s);
        var rolled = DiceOf(s);
        Assert.Equal(2, rolled.ChallengeDice.Count);
        Assert.All(rolled.ChallengeDice, d =>
        {
            Assert.Equal(sides, d.Sides);
            Assert.InRange(d.Value, 1, sides);
        });
        Assert.Equal(rolled.Dice.Sum(d => d.Value) + rolled.ChallengeDice.Sum(d => d.Value), s.Player("Вася").Points);
    }

    [Fact]
    public void Challenge_dice_come_after_the_dice_count_limit()
    {
        // D-13: the challenge adds after min/max. 100 hours → max 10 dice by hours, plus 1 for the challenge
        var s = Playing(100);

        s.Complete("Вася", Difficulty.Normal, challengeDone: true);

        ScenarioAssert.Accepted(s);
        Assert.Equal(10, DiceOf(s).Dice.Count);
        Assert.Single(DiceOf(s).ChallengeDice);
    }

    [Fact]
    public void Without_a_claim_there_are_no_challenge_dice()
    {
        var s = Playing(6);
        var runId = s.Player("Вася").ActiveRunId!.Value;

        s.NextRandom(2, 2).Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Empty(DiceOf(s).ChallengeDice);
        Assert.False(Assert.Single(s.LastEvents<RunCompleted>()).ChallengeDone);
        Assert.Empty(s.State.Runs[runId].ChallengeDice);
        Assert.Equal(4, s.Player("Вася").Points);
    }

    [Fact]
    public void Zero_extra_dice_give_nothing_for_a_claimed_challenge()
    {
        var s = Playing(6, ExtraDice(0));

        s.NextRandom(2, 2).Complete("Вася", Difficulty.Normal, challengeDone: true);

        ScenarioAssert.Accepted(s);
        Assert.Empty(DiceOf(s).ChallengeDice);
        Assert.Equal(2, DiceOf(s).Dice.Count);
        Assert.Equal(4, s.Player("Вася").Points);
        Assert.Equal(4, Assert.Single(s.LastEvents<PlayerMoved>()).Steps);
    }

    [Fact]
    public void Challenge_bonus_changed_after_the_roll_does_not_change_the_run()
    {
        // S1: the bonus is fixed at the roll (1); the admin raises it to 3 while Вася plays
        var s = Playing(6);
        s.WithRuleset(ExtraDice(3));

        s.Complete("Вася", Difficulty.Normal, challengeDone: true);

        ScenarioAssert.Accepted(s);
        Assert.Single(DiceOf(s).ChallengeDice);
    }

    [Fact]
    public void Challenge_bonus_changed_before_the_roll_applies_to_that_run()
    {
        var s = Playing(6, ExtraDice(3));

        s.Complete("Вася", Difficulty.Normal, challengeDone: true);

        ScenarioAssert.Accepted(s);
        Assert.Equal(3, DiceOf(s).ChallengeDice.Count);
    }

    [Fact]
    public void Challenge_with_an_estimate_adds_dice_the_same_way()
    {
        // No hours in the pool: 3-hour estimate → 1 die by hours, and 1 for the challenge
        var s = Playing(null);

        s.NextRandom(4, 2).Complete("Вася", Difficulty.Normal, estimatedHours: 3, hoursSource: "HLTB", challengeDone: true);

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(4, 4)], DiceOf(s).Dice);
        Assert.Equal([new Die(4, 2)], DiceOf(s).ChallengeDice);
        Assert.Equal(6, s.Player("Вася").Points);
    }
    [Fact]
    public void Challenges_are_off_by_default()
    {
        Assert.False(TestRuleset.Create().Features.Challenges);
        Assert.False(RulesetJson.Default().Features.Challenges);
    }

    [Fact]
    public void Claimed_challenge_with_the_feature_off_is_refused_without_events()
    {
        // D-96 (1): with features.challenges off the engine refuses ChallengeDone as feature.disabled
        var s = Playing(6, challenges: false);
        var runId = s.Player("Вася").ActiveRunId!.Value;

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Complete("Вася", Difficulty.Normal, challengeDone: true), RejectionCodes.FeatureDisabled);
        Assert.Equal(runId, s.Player("Вася").ActiveRunId);
        Assert.Equal(0, s.Player("Вася").Points);
    }

    [Fact]
    public void Feature_off_is_checked_before_the_hours()
    {
        // No hours in the pool and no estimate: the flag is reported, not the missing hours
        var s = Playing(null, challenges: false);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Complete("Вася", Difficulty.Normal, challengeDone: true), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void Completion_without_a_claim_works_with_the_feature_off()
    {
        var s = Playing(6, challenges: false);

        s.NextRandom(2, 3).Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Empty(DiceOf(s).ChallengeDice);
        Assert.Equal(5, s.Player("Вася").Points);
    }
}
