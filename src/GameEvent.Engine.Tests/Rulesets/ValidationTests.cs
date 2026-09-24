using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rulesets;

/// <summary>
/// C2: semantic checks a JSON schema cannot express. Every error carries the camelCase JSON path of the field
/// (array elements with an index, e.g. <c>finish.bonusByOrder[1]</c>), and all errors are reported at once.
/// E3 / D-22 / D-53: a mechanic this build cannot play yet is an error on its field.
/// </summary>
public class ValidationTests
{
    private static IReadOnlyList<RulesetError> Errors(Func<Ruleset, Ruleset> change) =>
        RulesetValidator.Validate(change(TestRuleset.Create()));

    private static string Show(IReadOnlyList<RulesetError> errors) =>
        errors.Count == 0 ? "(no errors)" : string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"));

    /// <summary>There is an error on one of <paramref name="paths"/> (an array field also matches its elements), with a message.</summary>
    private static void AssertErrorAt(IReadOnlyList<RulesetError> errors, params string[] paths)
    {
        var match = errors.FirstOrDefault(e => paths.Any(p => e.Path == p || e.Path.StartsWith(p + "[", StringComparison.Ordinal)));
        Assert.True(match is not null, $"Expected an error at {string.Join(" or ", paths)}, got: {Show(errors)}");
        Assert.False(string.IsNullOrWhiteSpace(match.Message), $"The error at {match.Path} has no message.");
    }

    private static Func<Ruleset, Ruleset> DiceCount(Func<DiceCountRule, DiceCountRule> change) =>
        r => r with { Reward = r.Reward with { DiceCount = change(r.Reward.DiceCount) } };

    private static Func<Ruleset, Ruleset> Dies(Func<DieByDifficulty, DieByDifficulty> change) =>
        r => r with { Reward = r.Reward with { DieByDifficulty = change(r.Reward.DieByDifficulty) } };

    private static Func<Ruleset, Ruleset> RerollCost(RerollCostKind kind, int? amount) =>
        r => r with { Roll = r.Roll with { RerollCost = new RerollCost { Kind = kind, Amount = amount } } };

    private static Func<Ruleset, Ruleset> PointsShare(decimal share) =>
        r => r with { Reward = r.Reward with { Coop = r.Reward.Coop with { PointsShare = share } } };

    private static Func<Ruleset, Ruleset> Payout(params PayoutStep[] steps) =>
        r => r with { Bets = r.Bets with { PayoutByHoursPerDay = [.. steps] } };

    private static Func<Ruleset, Ruleset> LengthSteps(params LengthFilterStep[] steps) =>
        r => r with
        {
            Roll = r.Roll with { LastDaysLengthFilter = r.Roll.LastDaysLengthFilter with { Steps = [.. steps] } },
        };

    private static Func<Ruleset, Ruleset> Reactions(params string[] reactions) =>
        r => r with { Social = r.Social with { Reactions = [.. reactions] } };

    private static Func<Ruleset, Ruleset> Flags(Func<Features, Features> change) =>
        r => r with { Features = change(r.Features) };

    private static PayoutStep Step(decimal? upTo, decimal multiplier) => new() { UpTo = upTo, Multiplier = multiplier };

    private static LengthFilterStep LengthStep(int days, decimal maxHours) => new() { DaysBeforeDeadline = days, MaxHours = maxHours };

    // ---- valid rulesets ----

    [Fact]
    public void Default_ruleset_is_valid()
    {
        var errors = RulesetValidator.Validate(RulesetJson.Default());

        Assert.True(errors.Count == 0, Show(errors));
    }

    [Fact]
    public void Pinned_test_ruleset_is_valid()
    {
        var errors = RulesetValidator.Validate(TestRuleset.Create());

        Assert.True(errors.Count == 0, Show(errors));
    }

    public static TheoryData<string, Func<Ruleset, Ruleset>> ValidEdges() => new()
    {
        { "dice min equals max", DiceCount(d => d with { Min = 3, Max = 3 }) },
        { "fractional hours per die", DiceCount(d => d with { HoursPerDie = 0.5m }) },
        {
            "two-sided dice everywhere",
            Dies(d => d with
            {
                Easy = new DieRule { Sides = 2 },
                Normal = new DieRule { Sides = 2 },
                Hard = new DieRule { Sides = 2 },
                Extreme = new DieRule { Sides = 2 },
            })
        },
        { "two-sided penalty die", r => r with { Drop = r.Drop with { PenaltyDice = r.Drop.PenaltyDice with { Sides = 2 } } } },
        { "zero finish bonuses", r => r with { Finish = r.Finish with { BonusByOrder = [0, 0], BonusAfterList = 0 } } },
        { "no finish bonus list", r => r with { Finish = r.Finish with { BonusByOrder = [] } } },
        {
            "a zero rarity weight",
            r => r with { Economy = r.Economy with { RarityWeights = r.Economy.RarityWeights with { Legendary = 0 } } }
        },
        { "one-step map", r => r with { Map = r.Map with { LinearLength = 1 } } },
        { "no coins per hour", r => r with { Reward = r.Reward with { Coins = r.Reward.Coins with { PerHour = 0 } } } },
        { "free paid reroll", RerollCost(RerollCostKind.Coins, 0) },
        { "bad event reroll without amount", RerollCost(RerollCostKind.BadEvent, null) },
        { "coop share of one", PointsShare(1m) },
        { "small coop share", PointsShare(0.01m) },
        { "one tiebreaker", r => r with { Ranking = r.Ranking with { Tiebreakers = [Tiebreaker.EarliestFinalScore] } } },
        { "one reaction", Reactions("🔥") },
        { "one open-ended payout step", Payout(Step(null, 1.5m)) },
        { "ascending payout steps", Payout(Step(0.5m, 1m), Step(1m, 1.5m), Step(null, 2m)) },
        { "fractional length limit", LengthSteps(LengthStep(3, 0.5m)) },
        { "no length steps", LengthSteps() },
    };

    [Theory]
    [MemberData(nameof(ValidEdges))]
    public void Ruleset_on_the_edge_is_valid(string what, Func<Ruleset, Ruleset> change)
    {
        var errors = Errors(change);

        Assert.True(errors.Count == 0, $"{what}: {Show(errors)}");
    }

    // ---- invalid rulesets ----

    public static TheoryData<string, Func<Ruleset, Ruleset>, string[]> InvalidRulesets() => new()
    {
        { "dice min above max", DiceCount(d => d with { Min = 5, Max = 4 }), ["reward.diceCount.min", "reward.diceCount.max"] },
        { "zero hours per die", DiceCount(d => d with { HoursPerDie = 0 }), ["reward.diceCount.hoursPerDie"] },
        { "negative hours per die", DiceCount(d => d with { HoursPerDie = -3 }), ["reward.diceCount.hoursPerDie"] },
        { "one-sided easy die", Dies(d => d with { Easy = new DieRule { Sides = 1 } }), ["reward.dieByDifficulty.easy.sides"] },
        { "zero-sided normal die", Dies(d => d with { Normal = new DieRule { Sides = 0 } }), ["reward.dieByDifficulty.normal.sides"] },
        { "one-sided hard die", Dies(d => d with { Hard = new DieRule { Sides = 1 } }), ["reward.dieByDifficulty.hard.sides"] },
        {
            "negative-sided extreme die",
            Dies(d => d with { Extreme = d.Extreme with { Sides = -6 } }),
            ["reward.dieByDifficulty.extreme.sides"]
        },
        {
            "one-sided penalty die",
            r => r with { Drop = r.Drop with { PenaltyDice = r.Drop.PenaltyDice with { Sides = 1 } } },
            ["drop.penaltyDice.sides"]
        },
        { "negative finish bonus by order", r => r with { Finish = r.Finish with { BonusByOrder = [10, -1, 6] } }, ["finish.bonusByOrder[1]"] },
        { "negative finish bonus after list", r => r with { Finish = r.Finish with { BonusAfterList = -1 } }, ["finish.bonusAfterList"] },
        {
            "negative rarity weight",
            r => r with { Economy = r.Economy with { RarityWeights = r.Economy.RarityWeights with { Epic = -1 } } },
            ["economy.rarityWeights.epic"]
        },
        { "zero games to choose from", r => r with { Roll = r.Roll with { ChoiceCount = 0 } }, ["roll.choiceCount"] },
        { "empty map", r => r with { Map = r.Map with { LinearLength = 0 } }, ["map.linearLength"] },
        { "negative map", r => r with { Map = r.Map with { LinearLength = -5 } }, ["map.linearLength"] },
        {
            "negative coins per hour",
            r => r with { Reward = r.Reward with { Coins = r.Reward.Coins with { PerHour = -0.5m } } },
            ["reward.coins.perHour"]
        },
        { "coins reroll without amount", RerollCost(RerollCostKind.Coins, null), ["roll.rerollCost.amount"] },
        { "coins reroll with negative amount", RerollCost(RerollCostKind.Coins, -1), ["roll.rerollCost.amount"] },
        { "bad event reroll with amount", RerollCost(RerollCostKind.BadEvent, 5), ["roll.rerollCost.amount"] },
        { "zero coop share", PointsShare(0m), ["reward.coop.pointsShare"] },
        { "negative coop share", PointsShare(-0.5m), ["reward.coop.pointsShare"] },
        { "coop share above one", PointsShare(1.01m), ["reward.coop.pointsShare"] },
        {
            "repeated tiebreaker",
            r => r with { Ranking = r.Ranking with { Tiebreakers = [Tiebreaker.CompletedRuns, Tiebreaker.CompletedRuns] } },
            ["ranking.tiebreakers"]
        },
        { "no reactions", Reactions(), ["social.reactions"] },
        { "repeated reaction", Reactions("🔥", "😂", "🔥"), ["social.reactions"] },
        { "payout steps not ascending", Payout(Step(3m, 2m), Step(1m, 1.2m), Step(null, 3m)), ["bets.payoutByHoursPerDay"] },
        { "equal payout steps", Payout(Step(1m, 1.2m), Step(1m, 2m), Step(null, 3m)), ["bets.payoutByHoursPerDay"] },
        { "payout without open-ended last step", Payout(Step(1m, 1.2m), Step(3m, 2m)), ["bets.payoutByHoursPerDay"] },
        { "open-ended payout step not last", Payout(Step(1m, 1.2m), Step(null, 3m), Step(5m, 4m)), ["bets.payoutByHoursPerDay"] },
        { "no payout steps", Payout(), ["bets.payoutByHoursPerDay"] },
        {
            "zero length limit",
            LengthSteps(LengthStep(5, 10), LengthStep(2, 0)),
            ["roll.lastDaysLengthFilter.steps[1].maxHours"]
        },
        { "negative length limit", LengthSteps(LengthStep(5, -1)), ["roll.lastDaysLengthFilter.steps[0].maxHours"] },
    };

    [Theory]
    [MemberData(nameof(InvalidRulesets))]
    public void Invalid_ruleset_reports_the_field(string what, Func<Ruleset, Ruleset> change, string[] paths)
    {
        _ = what;

        AssertErrorAt(Errors(change), paths);
    }

    [Fact]
    public void All_errors_are_reported_at_once()
    {
        var errors = Errors(r => r with
        {
            Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { HoursPerDie = 0 } },
            Map = r.Map with { LinearLength = 0 },
            Social = r.Social with { Reactions = [] },
            Features = r.Features with { Shop = true },
        });

        AssertErrorAt(errors, "reward.diceCount.hoursPerDie");
        AssertErrorAt(errors, "map.linearLength");
        AssertErrorAt(errors, "social.reactions");
        AssertErrorAt(errors, "features.shop");
    }

    [Fact]
    public void Valid_fields_next_to_an_invalid_one_are_not_reported()
    {
        var errors = Errors(r => r with { Map = r.Map with { LinearLength = 0 } });

        Assert.All(errors, e => Assert.Equal("map.linearLength", e.Path));
    }

    // ---- mechanics this build cannot play yet (D-22, D-53, E3) ----

    public static TheoryData<string, Func<Ruleset, Ruleset>, string> NotImplementedYet() => new()
    {
        { "graph map", Flags(f => f with { MapMode = MapMode.Graph }), "features.mapMode" },
        { "choice of two games", r => r with { Roll = r.Roll with { ChoiceCount = 2 } }, "roll.choiceCount" },
        { "two active runs", r => r with { Season = r.Season with { MaxActiveRunsPerPlayer = 2 } }, "season.maxActiveRunsPerPlayer" },
        { "shop", Flags(f => f with { Shop = true }), "features.shop" },
        { "items", Flags(f => f with { Items = true }), "features.items" },
        { "events", Flags(f => f with { Events = true }), "features.events" },
        { "bets", Flags(f => f with { Bets = true }), "features.bets" },
        { "polls", Flags(f => f with { Polls = true }), "features.polls" },
        { "achievements", Flags(f => f with { Achievements = true }), "features.achievements" },
        { "weekly challenge", Flags(f => f with { WeeklyChallenge = true }), "features.weeklyChallenge" },
        { "partner board", Flags(f => f with { PartnerBoard = true }), "features.partnerBoard" },
        { "reactions", Flags(f => f with { Reactions = true }), "features.reactions" },
        { "comments", Flags(f => f with { Comments = true }), "features.comments" },
        { "gallery", Flags(f => f with { Gallery = true }), "features.gallery" },
    };

    [Theory]
    [MemberData(nameof(NotImplementedYet))]
    public void Mechanic_not_implemented_in_this_build_is_an_error_on_its_field(string what, Func<Ruleset, Ruleset> change, string path)
    {
        _ = what;
        var errors = Errors(change);

        var error = Assert.Single(errors);
        Assert.Equal(path, error.Path);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Linear_map_with_every_flag_off_is_playable()
    {
        var errors = Errors(Flags(f => f with { MapMode = MapMode.Linear }));

        Assert.True(errors.Count == 0, Show(errors));
    }
}
