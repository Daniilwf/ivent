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
    private const int MaxChoiceCount = 20;
    private const int MaxCoins = 1_000_000;
    private const int MaxLotsPerRoll = 10;
    private const int MaxLotLifetimeMinutes = 7 * 24 * 60;
    private const int MaxInventoryLimit = 100;
    private const int MaxBetDeadlineDays = 365;
    private const int MaxWeight = 1_000_000;
    private const decimal MaxPayoutMultiplier = 100;
    private const int MaxReflectMultiplier = 10;
    private const int MaxWeeklyChallengeDays = 366;
    private const int MaxSuggestionsPerDay = 100;

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
        if (ruleset.Drop.ConsecutiveExtraDice is { } extra)
        {
            NotNegative("drop.consecutiveExtraDice", extra);
            AtMost("drop.consecutiveExtraDice", extra, MaxDice);
        }

        if (ruleset.Roll.WishRerollTags.Any(string.IsNullOrWhiteSpace))
        {
            Error("roll.wishRerollTags", "a tag must not be blank");
        }

        NotNegative("reward.challengeBonus.extraDice", ruleset.Reward.ChallengeBonus.ExtraDice);
        AtMost("reward.challengeBonus.extraDice", ruleset.Reward.ChallengeBonus.ExtraDice, MaxDice);

        for (var i = 0; i < ruleset.Finish.BonusByOrder.Count; i++)
        {
            NotNegative($"finish.bonusByOrder[{i}]", ruleset.Finish.BonusByOrder[i]);
        }

        NotNegative("finish.bonusAfterList", ruleset.Finish.BonusAfterList);

        var economy = ruleset.Economy;
        var weights = economy.RarityWeights;
        foreach (var (name, weight) in new[] { ("common", weights.Common), ("epic", weights.Epic), ("legendary", weights.Legendary) })
        {
            Within($"economy.rarityWeights.{name}", weight, 0, MaxWeight);
        }

        // NB-SEC-1: a negative step made a shop roll pay coins, a huge lifetime threw in AddMinutes (D-651)
        Within("economy.inventoryLimit", economy.InventoryLimit, 0, MaxInventoryLimit);
        Within("economy.shop.rollCost", economy.Shop.RollCost, 0, MaxCoins);
        Within("economy.shop.rerollCostStep", economy.Shop.RerollCostStep, 0, MaxCoins);
        Within("economy.shop.lotsPerRoll", economy.Shop.LotsPerRoll, 1, MaxLotsPerRoll);
        Within("economy.shop.lotLifetimeMinutes", economy.Shop.LotLifetimeMinutes, 1, MaxLotLifetimeMinutes);

        if (ruleset.Roll.ChoiceCount < 1)
        {
            Error("roll.choiceCount", "must be at least 1");
        }

        AtMost("roll.choiceCount", ruleset.Roll.ChoiceCount, MaxChoiceCount);

        if (ruleset.Season.MaxActiveRunsPerPlayer < 1)
        {
            Error("season.maxActiveRunsPerPlayer", "must be at least 1");
        }

        if (ruleset.Season.MaxUncheckedRuns is < 1)
        {
            Error("season.maxUncheckedRuns", "must be at least 1, or null for no limit");
        }

        if (ruleset.Map.LinearLength < 1)
        {
            Error("map.linearLength", "must be at least 1");
        }

        AtMost("map.linearLength", ruleset.Map.LinearLength, MaxMapLength);
        if (ruleset.Map.MinZoneGames is < 0)
        {
            Error("map.minZoneGames", "must not be negative, or null for no warning");
        }

        NotNegative("season.inactiveHintDays", ruleset.Season.InactiveHintDays);

        if (ruleset.Effects.MaxChainDepth < 1)
        {
            Error("effects.maxChainDepth", "must be at least 1");
        }

        // The protective ceilings of SPEC «Лимит цепочки»: a season may cut its chains earlier, never later (D-418, AU4-7)
        AtMost("effects.maxChainDepth", ruleset.Effects.MaxChainDepth, Limits.MaxEffectDepth);
        AtMost("effects.maxEventsPerCommand", ruleset.Effects.MaxEventsPerCommand, Limits.MaxEventsPerCommand);

        if (ruleset.Effects.Reflect is { Multiplier: < 1 })
        {
            Error("effects.reflect.multiplier", "must be at least 1");
        }

        if (ruleset.Effects.Reflect is { } reflect)
        {
            AtMost("effects.reflect.multiplier", reflect.Multiplier, MaxReflectMultiplier);
        }

        if (ruleset.Features.Events && ruleset.Events is null)
        {
            Error("events", "is required when features.events is on");
        }

        if (ruleset.Features.WeeklyChallenge && ruleset.WeeklyChallenge.DurationDays is null)
        {
            Error("weeklyChallenge.durationDays", "is required when features.weeklyChallenge is on");
        }

        // A window is a few days; the bound keeps the window's end a valid moment (D-574)
        if (ruleset.WeeklyChallenge.DurationDays is < 1 or > MaxWeeklyChallengeDays)
        {
            Error("weeklyChallenge.durationDays", $"must be 1–{MaxWeeklyChallengeDays}");
        }

        if (ruleset.Features.Suggestions && ruleset.Suggestions is null)
        {
            Error("suggestions", "is required when features.suggestions is on");
        }

        if (ruleset.Suggestions is { PerDay: < 1 or > MaxSuggestionsPerDay })
        {
            Error("suggestions.perDay", $"must be 1–{MaxSuggestionsPerDay}");
        }

        // Special and risky events are drawn from decks: without events they could not even be played by hand (D-530)
        var difficulties = ruleset.Reward.DieByDifficulty;
        foreach (var (name, rule) in new[] { ("easy", difficulties.Easy), ("normal", difficulties.Normal), ("hard", difficulties.Hard), ("extreme", difficulties.Extreme) })
        {
            if (!ruleset.Features.Events && rule.GrantEvent is EventKind.Special or EventKind.Risky)
            {
                Error($"reward.dieByDifficulty.{name}.grantEvent", "special and risky events need features.events");
            }
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

        if (ruleset.Reward.Coins.PerHour > MaxCoins)
        {
            Error("reward.coins.perHour", $"must not exceed {MaxCoins}");
        }

        NotNegative("reward.coins.min", ruleset.Reward.Coins.Min);
        AtMost("reward.coins.min", ruleset.Reward.Coins.Min, MaxCoins);

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

        var bets = ruleset.Bets;
        PayoutSteps(bets.PayoutByHoursPerDay);
        Within("bets.maxStake", bets.MaxStake, 1, MaxCoins);
        NotNegative("bets.windowHoursAfterRoll", bets.WindowHoursAfterRoll);
        NotNegative("bets.maxOpenBetsPerPlayer", bets.MaxOpenBetsPerPlayer);
        var days = bets.DeadlineOptionsDays;
        if (days.Count == 0 || days.Distinct().Count() != days.Count)
        {
            Error("bets.deadlineOptionsDays", "must be a non-empty list without repeats");
        }

        for (var i = 0; i < days.Count; i++)
        {
            Within($"bets.deadlineOptionsDays[{i}]", days[i], 1, MaxBetDeadlineDays);
        }

        for (var i = 0; i < bets.PayoutByHoursPerDay.Count; i++)
        {
            if (bets.PayoutByHoursPerDay[i].Multiplier > MaxPayoutMultiplier)
            {
                Error($"bets.payoutByHoursPerDay[{i}].multiplier", $"must be at most {MaxPayoutMultiplier}");
            }
        }

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

        void Within(string path, int value, int floor, int ceiling)
        {
            if (value < floor || value > ceiling)
            {
                Error(path, $"must be {floor}–{ceiling}");
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
