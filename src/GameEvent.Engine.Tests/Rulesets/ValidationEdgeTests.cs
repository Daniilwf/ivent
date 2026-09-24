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
        {
            "length filter not implemented",
            r => r with { Roll = r.Roll with { LastDaysLengthFilter = r.Roll.LastDaysLengthFilter with { Enabled = true } } },
            "roll.lastDaysLengthFilter.enabled"
        },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_value_is_reported_on_its_field(string what, Func<Ruleset, Ruleset> change, string path)
    {
        var errors = RulesetValidator.Validate(change(TestRuleset.Create()));

        Assert.True(errors.Any(e => e.Path == path), $"{what}: expected an error on {path}, got [{string.Join(", ", errors.Select(e => e.Path))}]");
    }

    [Fact]
    public void Ceilings_leave_real_seasons_alone()
    {
        var generous = TestRuleset.Create() with { Map = new MapRules { LinearLength = 10_000 } };

        Assert.Empty(RulesetValidator.Validate(generous));
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
