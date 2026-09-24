using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Rulesets;

/// <summary>A problem in a ruleset: the JSON path of the field and what is wrong, in English for logs.</summary>
public sealed record RulesetError(string Path, string Message);

/// <summary>
/// Semantic checks a JSON schema cannot express (min ≤ max, dice with at least two sides, …) and mechanics this
/// build cannot play yet (D-22, D-53). An invalid ruleset is never stored (C2). All errors are reported at once.
/// </summary>
public static class RulesetValidator
{
    /// <summary>The configuration document format this build reads (the JSON field <c>version</c>).</summary>
    public const int CurrentFormat = 1;

    // Safety ceilings, not balance: a typo like 6000000 must not make the queue build a gigantic map or roll
    // millions of dice. Balance numbers stay in the ruleset (D-86).
    private const int MaxMapLength = 10_000;
    private const int MaxDice = 1_000;
    private const int MaxSides = 1_000;

    public static IReadOnlyList<RulesetError> Validate(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        var errors = new List<RulesetError>();
        void Error(string path, string message) => errors.Add(new RulesetError(path, message));

        if (ruleset.Version != CurrentFormat)
        {
            Error("version", $"format {ruleset.Version} is not supported; this build reads format {CurrentFormat}");
        }

        var dice = ruleset.Reward.DiceCount;
        if (dice.HoursPerDie <= 0)
        {
            Error("reward.diceCount.hoursPerDie", "must be greater than 0");
        }

        if (dice.Min < 0)
        {
            Error("reward.diceCount.min", "must not be negative");
        }

        if (dice.Min > dice.Max)
        {
            Error("reward.diceCount.min", $"must not exceed max ({dice.Max})");
        }

        AtMost("reward.diceCount.max", dice.Max, MaxDice);

        var dies = ruleset.Reward.DieByDifficulty;
        foreach (var (name, die) in new[] { ("easy", dies.Easy), ("normal", dies.Normal), ("hard", dies.Hard), ("extreme", dies.Extreme) })
        {
            Sides($"reward.dieByDifficulty.{name}.sides", die.Sides);
        }

        Sides("drop.penaltyDice.sides", ruleset.Drop.PenaltyDice.Sides);
        NotNegative("drop.penaltyDice.count", ruleset.Drop.PenaltyDice.Count);
        AtMost("drop.penaltyDice.count", ruleset.Drop.PenaltyDice.Count, MaxDice);
        NotNegative("reward.challengeBonus.extraDice", ruleset.Reward.ChallengeBonus.ExtraDice);
        AtMost("reward.challengeBonus.extraDice", ruleset.Reward.ChallengeBonus.ExtraDice, MaxDice);

        for (var i = 0; i < ruleset.Finish.BonusByOrder.Count; i++)
        {
            NotNegative($"finish.bonusByOrder[{i}]", ruleset.Finish.BonusByOrder[i]);
        }

        NotNegative("finish.bonusAfterList", ruleset.Finish.BonusAfterList);

        var weights = ruleset.Economy.RarityWeights;
        NotNegative("economy.rarityWeights.common", weights.Common);
        NotNegative("economy.rarityWeights.epic", weights.Epic);
        NotNegative("economy.rarityWeights.legendary", weights.Legendary);

        if (ruleset.Roll.ChoiceCount < 1)
        {
            Error("roll.choiceCount", "must be at least 1");
        }

        if (ruleset.Season.MaxActiveRunsPerPlayer < 1)
        {
            Error("season.maxActiveRunsPerPlayer", "must be at least 1");
        }

        if (ruleset.Map.LinearLength < 1)
        {
            Error("map.linearLength", "must be at least 1");
        }

        AtMost("map.linearLength", ruleset.Map.LinearLength, MaxMapLength);
        NotNegative("season.inactiveHintDays", ruleset.Season.InactiveHintDays);

        if (ruleset.Effects.MaxChainDepth < 1)
        {
            Error("effects.maxChainDepth", "must be at least 1");
        }

        if (ruleset.Effects.MaxEventsPerCommand < 1)
        {
            Error("effects.maxEventsPerCommand", "must be at least 1");
        }

        NotNegative("roll.freeRerollsPerRoll", ruleset.Roll.FreeRerollsPerRoll);
        NotNegative("roll.techRerollWindowHours", ruleset.Roll.TechRerollWindowHours);
        NotNegative("roll.minPlayMinutesBeforeDrop", ruleset.Roll.MinPlayMinutesBeforeDrop);

        if (ruleset.Reward.Coins.PerHour < 0)
        {
            Error("reward.coins.perHour", "must not be negative");
        }

        NotNegative("reward.coins.min", ruleset.Reward.Coins.Min);

        var reroll = ruleset.Roll.RerollCost;
        switch (reroll.Kind)
        {
            case RerollCostKind.Coins when reroll.Amount is null:
                Error("roll.rerollCost.amount", "is required when the reroll costs coins");
                break;
            case RerollCostKind.Coins when reroll.Amount < 0:
                Error("roll.rerollCost.amount", "must not be negative");
                break;
            case RerollCostKind.BadEvent when reroll.Amount is not null:
                Error("roll.rerollCost.amount", "must be absent when the reroll costs a bad event");
                break;
            default:
                break;
        }

        var share = ruleset.Reward.Coop.PointsShare;
        if (share is <= 0 or > 1)
        {
            Error("reward.coop.pointsShare", "must be greater than 0 and at most 1");
        }

        var tiebreakers = ruleset.Ranking.Tiebreakers;
        if (tiebreakers.Distinct().Count() != tiebreakers.Count)
        {
            Error("ranking.tiebreakers", "must not repeat");
        }

        var reactions = ruleset.Social.Reactions;
        if (reactions.Count == 0 || reactions.Any(string.IsNullOrWhiteSpace) || reactions.Distinct(StringComparer.Ordinal).Count() != reactions.Count)
        {
            Error("social.reactions", "must be a non-empty list without repeats");
        }

        PayoutSteps(ruleset.Bets.PayoutByHoursPerDay);

        var steps = ruleset.Roll.LastDaysLengthFilter.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].MaxHours <= 0)
            {
                Error($"roll.lastDaysLengthFilter.steps[{i}].maxHours", "must be greater than 0");
            }

            NotNegative($"roll.lastDaysLengthFilter.steps[{i}].daysBeforeDeadline", steps[i].DaysBeforeDeadline);
        }

        errors.AddRange(RulesetSupport.Unsupported(ruleset)
            .Where(unsupported => !errors.Any(e => e.Path == unsupported.Path)));
        return errors;

        void Sides(string path, int sides)
        {
            if (sides < 2)
            {
                Error(path, "a die needs at least 2 sides");
            }

            AtMost(path, sides, MaxSides);
        }

        void AtMost(string path, int value, int ceiling)
        {
            if (value > ceiling)
            {
                Error(path, $"must be at most {ceiling}");
            }
        }

        void NotNegative(string path, int value)
        {
            if (value < 0)
            {
                Error(path, "must not be negative");
            }
        }

        // Ascending «up to» bounds, the last step open-ended (upTo: null) and only the last.
        void PayoutSteps(EquatableArray<PayoutStep> payout)
        {
            var bounded = payout.Take(Math.Max(payout.Count - 1, 0)).ToList();
            var valid = payout.Count > 0
                && payout[^1].UpTo is null
                && bounded.All(s => s.UpTo is > 0)
                && bounded.Zip(bounded.Skip(1)).All(p => p.First.UpTo < p.Second.UpTo)
                && payout.All(s => s.Multiplier > 0);
            if (!valid)
            {
                Error("bets.payoutByHoursPerDay", "steps must ascend by upTo, end with an open-ended step (upTo: null) and pay a positive multiplier");
            }
        }
    }

    /// <summary>The rejection for an invalid ruleset, or null when it is valid.</summary>
    internal static Decision? Check(Ruleset ruleset)
    {
        var errors = Validate(ruleset);
        return errors.Count == 0
            ? null
            : Decision.Reject(RejectionCodes.RulesetInvalid, string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
    }
}
