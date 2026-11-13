using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rulesets;

/// <summary>
/// Validator branches found untested by the C1 audit, safety ceilings and the document format (D-86),
/// and concurrent edits of the rules (optimistic version check).
/// </summary>
public class ValidationEdgeTests
{
    public static TheoryData<string, Func<Ruleset, Ruleset>, string> Invalid() => new()
    {
        { "negative common weight", r => r with { Economy = r.Economy with { RarityWeights = r.Economy.RarityWeights with { Common = -1 } } }, "economy.rarityWeights.common" },
        { "negative legendary weight", r => r with { Economy = r.Economy with { RarityWeights = r.Economy.RarityWeights with { Legendary = -1 } } }, "economy.rarityWeights.legendary" },
        { "negative dice min", r => r with { Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { Min = -1 } } }, "reward.diceCount.min" },
        { "negative penalty dice", r => r with { Drop = r.Drop with { PenaltyDice = r.Drop.PenaltyDice with { Count = -1 } } }, "drop.penaltyDice.count" },
        { "negative challenge dice", r => r with { Reward = r.Reward with { ChallengeBonus = new ChallengeBonus { ExtraDice = -1 } } }, "reward.challengeBonus.extraDice" },
        { "negative free rerolls", r => r with { Roll = r.Roll with { FreeRerollsPerRoll = -1 } }, "roll.freeRerollsPerRoll" },
        { "negative tech reroll window", r => r with { Roll = r.Roll with { TechRerollWindowHours = -1 } }, "roll.techRerollWindowHours" },
        { "negative minutes before drop", r => r with { Roll = r.Roll with { MinPlayMinutesBeforeDrop = -1 } }, "roll.minPlayMinutesBeforeDrop" },
        { "negative minimum coins", r => r with { Reward = r.Reward with { Coins = r.Reward.Coins with { Min = -1 } } }, "reward.coins.min" },
        { "no active runs", r => r with { Season = r.Season with { MaxActiveRunsPerPlayer = 0 } }, "season.maxActiveRunsPerPlayer" },
        { "negative inactive hint", r => r with { Season = r.Season with { InactiveHintDays = -5 } }, "season.inactiveHintDays" },
        { "no chain depth", r => r with { Effects = r.Effects with { MaxChainDepth = 0 } }, "effects.maxChainDepth" },
        { "no events per command", r => r with { Effects = r.Effects with { MaxEventsPerCommand = 0 } }, "effects.maxEventsPerCommand" },
        { "chain deeper than the ceiling", r => r with { Effects = r.Effects with { MaxChainDepth = Limits.MaxEffectDepth + 1 } }, "effects.maxChainDepth" },
        { "more events than the ceiling", r => r with { Effects = r.Effects with { MaxEventsPerCommand = Limits.MaxEventsPerCommand + 1 } }, "effects.maxEventsPerCommand" },
        {
            "negative days before deadline",
            r => r with { Roll = r.Roll with { LastDaysLengthFilter = r.Roll.LastDaysLengthFilter with { Steps = [new LengthFilterStep { DaysBeforeDeadline = -1, MaxHours = 5 }] } } },
            "roll.lastDaysLengthFilter.steps[0].daysBeforeDeadline"
        },
        { "blank reaction", r => r with { Social = r.Social with { Reactions = ["🔥", " "] } }, "social.reactions" },
        {
            "zero payout multiplier",
            r => r with { Bets = r.Bets with { PayoutByHoursPerDay = [new PayoutStep { UpTo = null, Multiplier = 0 }] } },
            "bets.payoutByHoursPerDay"
        },
        {
            "zero payout bound",
            r => r with { Bets = r.Bets with { PayoutByHoursPerDay = [new PayoutStep { UpTo = 0, Multiplier = 1 }, new PayoutStep { UpTo = null, Multiplier = 2 }] } },
            "bets.payoutByHoursPerDay"
        },
        { "unknown document format", r => r with { Version = 99 }, "version" },
        { "gigantic map", r => r with { Map = r.Map with { LinearLength = int.MaxValue } }, "map.linearLength" },
        { "millions of dice", r => r with { Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { Max = 5_000_000 } } }, "reward.diceCount.max" },
        { "gigantic die", r => r with { Drop = r.Drop with { PenaltyDice = r.Drop.PenaltyDice with { Sides = 1_000_000 } } }, "drop.penaltyDice.sides" },
        // D-96 (3): a typo in the coin reward must not break every completion
        { "millions of coins per hour", r => r with { Reward = r.Reward with { Coins = r.Reward.Coins with { PerHour = 1_000_001 } } }, "reward.coins.perHour" },
        { "millions of minimum coins", r => r with { Reward = r.Reward with { Coins = r.Reward.Coins with { Min = 1_000_001 } } }, "reward.coins.min" },
        // Found by mutation testing (D-282)
        { "blank wish reroll tag", r => r with { Roll = r.Roll with { WishRerollTags = ["Horror", " "] } }, "roll.wishRerollTags" },
        { "no unchecked runs", r => r with { Season = r.Season with { MaxUncheckedRuns = 0 } }, "season.maxUncheckedRuns" },
        { "negative zone games", r => r with { Map = r.Map with { MinZoneGames = -1 } }, "map.minZoneGames" },
        { "too many challenge dice", r => r with { Reward = r.Reward with { ChallengeBonus = new ChallengeBonus { ExtraDice = 1_001 } } }, "reward.challengeBonus.extraDice" },
        {
            "length filter not implemented",
            r => r with { Roll = r.Roll with { LastDaysLengthFilter = r.Roll.LastDaysLengthFilter with { Enabled = true } } },
            "roll.lastDaysLengthFilter.enabled"
        },
        // Review of 5.13 and 5.14 (D-578, D-579): the bounds of the weekly challenge's window and the suggestion box's limit
        { "a weekly challenge of no days", r => r with { WeeklyChallenge = r.WeeklyChallenge with { DurationDays = 0 } }, "weeklyChallenge.durationDays" },
        { "a weekly challenge over a year", r => r with { WeeklyChallenge = r.WeeklyChallenge with { DurationDays = 367 } }, "weeklyChallenge.durationDays" },
        { "no suggestions a day", r => r with { Suggestions = new SuggestionRules { PerDay = 0 } }, "suggestions.perDay" },
        { "too many suggestions a day", r => r with { Suggestions = new SuggestionRules { PerDay = 101 } }, "suggestions.perDay" },
        // Task 5.6 (EV12): the screenshots of an IRL proof, at most what a proof of a run holds
        { "events without the irl proof rules", IrlProof(null!), "events.irlProof" },
        { "negative irl proof files", IrlProof(new IrlProofRules { MaxFiles = -1 }), "events.irlProof.maxFiles" },
        { "more irl proof files than a proof holds", IrlProof(new IrlProofRules { MaxFiles = Limits.MaxProofFiles + 1 }), "events.irlProof.maxFiles" },
    };

    /// <summary>Events on (with items) and <paramref name="rules"/> as <c>events.irlProof</c>.</summary>
    private static Func<Ruleset, Ruleset> IrlProof(IrlProofRules rules) =>
        r =>
        {
            var withEvents = r.WithEvents();
            return withEvents with { Events = withEvents.Events! with { IrlProof = rules } };
        };

    [Theory]
    [InlineData(0)]
    [InlineData(Limits.MaxProofFiles)]
    public void The_irl_proof_files_at_their_bounds_are_accepted(int maxFiles)
    {
        Assert.Empty(RulesetValidator.Validate(IrlProof(new IrlProofRules { MaxFiles = maxFiles })(TestRuleset.Create())));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(366, 100)]
    public void The_weekly_challenge_window_and_the_suggestion_limit_at_their_bounds_are_accepted(int days, int perDay)
    {
        var r = TestRuleset.Create();
        var atBounds = r with
        {
            Features = r.Features with { WeeklyChallenge = true, Suggestions = true },
            WeeklyChallenge = r.WeeklyChallenge with { DurationDays = days },
            Suggestions = new SuggestionRules { PerDay = perDay },
        };

        Assert.Empty(RulesetValidator.Validate(atBounds));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_value_is_reported_on_its_field(string what, Func<Ruleset, Ruleset> change, string path)
    {
        var errors = RulesetValidator.Validate(change(TestRuleset.Create()));

        Assert.True(errors.Any(e => e.Path == path), $"{what}: expected an error on {path}, got [{string.Join(", ", errors.Select(e => e.Path))}]");
        Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message), $"{what}: the error at {e.Path} has no message"));
    }

    [Fact]
    public void Dice_min_above_max_names_the_max()
    {
        var r = TestRuleset.Create();

        var error = Assert.Single(RulesetValidator.Validate(r with { Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { Min = 9, Max = 3 } } }));

        Assert.Equal(new RulesetError("reward.diceCount.min", "must not exceed max (3)"), error);
    }

    [Fact]
    public void A_missing_ruleset_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => RulesetValidator.Validate(null!));
    }

    [Fact]
    public void Ceilings_leave_real_seasons_alone()
    {
        var generous = TestRuleset.Create() with { Map = new MapRules { LinearLength = 10_000 } };

        Assert.Empty(RulesetValidator.Validate(generous));
    }

    [Fact]
    public void Coin_reward_at_its_ceilings_is_accepted()
    {
        // D-96 (3): 1 000 000 is the ceiling itself for both reward.coins.perHour and reward.coins.min
        var r = TestRuleset.Create();
        var atCeiling = r with { Reward = r.Reward with { Coins = new CoinReward { PerHour = 1_000_000, Min = 1_000_000 } } };

        Assert.Empty(RulesetValidator.Validate(atCeiling));
    }

    [Fact]
    public void Coin_reward_over_its_ceilings_is_refused_by_the_engine()
    {
        var s = Scenario.New().WithPlayers("Вася");
        var typo = s.Ruleset with { Reward = s.Ruleset.Reward with { Coins = s.Ruleset.Reward.Coins with { PerHour = 1_000_001 } } };

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRuleset(typo)), RejectionCodes.RulesetInvalid);
    }

    [Fact]
    public void Change_edited_from_an_older_version_is_refused()
    {
        var s = Scenario.New().WithPlayers("Вася");
        var edited = s.Ruleset with { Reward = s.Ruleset.Reward with { DiceCount = s.Ruleset.Reward.DiceCount with { Max = 12 } } };
        s.Act(new ChangeRuleset(edited, ExpectedVersion: 1));
        Assert.True(s.Last.IsAccepted);

        // A second admin window still shows version 1 and saves its own edit
        var stale = s.Ruleset with { Reward = s.Ruleset.Reward with { DiceCount = s.Ruleset.Reward.DiceCount with { Max = 8 } } };

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRuleset(stale, ExpectedVersion: 1)), RejectionCodes.RulesetVersionConflict);
        Assert.Equal(12, s.Ruleset.Reward.DiceCount.Max);
    }

    [Fact]
    public void Change_edited_from_the_current_version_goes_through()
    {
        var s = Scenario.New().WithPlayers("Вася");
        var edited = s.Ruleset with { Reward = s.Ruleset.Reward with { DiceCount = s.Ruleset.Reward.DiceCount with { Max = 12 } } };

        s.Act(new ChangeRuleset(edited, ExpectedVersion: 1));

        Assert.True(s.Last.IsAccepted);
        Assert.Equal(2, s.State.RulesetVersion);
    }
}
