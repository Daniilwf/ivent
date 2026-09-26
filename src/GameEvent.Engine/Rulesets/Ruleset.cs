using System.Text.Json.Serialization;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Rulesets;

// Season rules: every game number the engine uses comes from here; there are no balance constants in code.
// Field meanings: docs/RULESET.md. The JSON schema docs/ruleset.schema.json is generated from these types.
// Fields added after the first release must be optional with a default (D-50), so older versions still read.

public sealed record Ruleset
{
    /// <summary>Format version of the configuration document (not the season's ruleset version).</summary>
    public required int Version { get; init; }

    public required Features Features { get; init; }

    public required SeasonRules Season { get; init; }

    public required RollRules Roll { get; init; }

    public required RewardRules Reward { get; init; }

    public required DropRules Drop { get; init; }

    public required FinishRules Finish { get; init; }

    public required RankingRules Ranking { get; init; }

    public required MapRules Map { get; init; }

    public required EconomyRules Economy { get; init; }

    public required EffectRules Effects { get; init; }

    public required InteractionRules Interactions { get; init; }

    public required BetRules Bets { get; init; }

    public required SocialRules Social { get; init; }

    public required NominationRules Nominations { get; init; }

    public required WeeklyChallengeRules WeeklyChallenge { get; init; }
}

public enum MapMode
{
    Linear,
    Graph,
}

/// <summary>Feature flags: a disabled mechanic is invisible in the interface and refused by the engine.</summary>
public sealed record Features
{
    public required MapMode MapMode { get; init; }

    public required bool Shop { get; init; }

    public required bool Items { get; init; }

    public required bool Events { get; init; }

    public required bool Bets { get; init; }

    public required bool Polls { get; init; }

    public required bool Achievements { get; init; }

    public required bool WeeklyChallenge { get; init; }

    public required bool PartnerBoard { get; init; }

    public required bool Reactions { get; init; }

    public required bool Comments { get; init; }

    public required bool Gallery { get; init; }

    /// <summary>
    /// Claiming a game's challenge on completion (D-96). Off until games carry a challenge note and the proof checks it;
    /// optional, so older configs without it still read.
    /// </summary>
    public bool Challenges { get; init; }
}

public sealed record SeasonRules
{
    public required string Timezone { get; init; }

    public required int MaxActiveRunsPerPlayer { get; init; }

    public required int InactiveHintDays { get; init; }

    /// How many of a player's runs may wait for the admin's check before a new roll is refused (D-134): completed, not
    /// rejected, the proof not approved (pending or not sent). Null: no limit — so a season logged before this rule
    /// reads without it and keeps its old behaviour. Not written when null: a season without the limit logs its ruleset
    /// exactly as before the field existed.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxUncheckedRuns { get; init; }
}

public enum RerollCostKind
{
    Coins,
    BadEvent,
}

/// <summary>Price of a paid reroll: <see cref="Amount"/> coins, or a bad event (then no amount).</summary>
public sealed record RerollCost
{
    public required RerollCostKind Kind { get; init; }

    public int? Amount { get; init; }
}

public enum EmptyPoolFallback
{
    DropZoneFilter,
}

public sealed record LengthFilterStep
{
    public required int DaysBeforeDeadline { get; init; }

    public required decimal MaxHours { get; init; }
}

public sealed record LastDaysLengthFilter
{
    public required bool Enabled { get; init; }

    public required EquatableArray<LengthFilterStep> Steps { get; init; }
}

public sealed record RollRules
{
    public required int ChoiceCount { get; init; }

    public required int FreeRerollsPerRoll { get; init; }

    public required RerollCost RerollCost { get; init; }

    public required int TechRerollWindowHours { get; init; }

    public required int MinPlayMinutesBeforeDrop { get; init; }

    public required EmptyPoolFallback EmptyPoolFallback { get; init; }

    public required LastDaysLengthFilter LastDaysLengthFilter { get; init; }
}

public enum Rounding
{
    Nearest,
    Floor,
    Ceil,
}

public sealed record DiceCountRule
{
    public required decimal HoursPerDie { get; init; }

    public required Rounding Rounding { get; init; }

    public required int Min { get; init; }

    public required int Max { get; init; }
}

public enum EventKind
{
    Good,
    Bad,
}

public sealed record DieRule
{
    public required int Sides { get; init; }

    public EventKind? GrantEvent { get; init; }
}

public sealed record DieByDifficulty
{
    public required DieRule Easy { get; init; }

    public required DieRule Normal { get; init; }

    public required DieRule Hard { get; init; }

    public required DieRule Extreme { get; init; }
}

public sealed record ChallengeBonus
{
    public required int ExtraDice { get; init; }
}

public enum UnmetConditionPolicy
{
    NoDiceKeepCoins,
    CountAsDrop,
    Ignore,
}

public sealed record CoinReward
{
    public required decimal PerHour { get; init; }

    public required int Min { get; init; }
}

public enum CoopRoundUpFor
{
    Roller,
}

public sealed record CoopRules
{
    public required decimal PointsShare { get; init; }

    public required CoopRoundUpFor RoundUpFor { get; init; }
}

public sealed record RewardRules
{
    public required DiceCountRule DiceCount { get; init; }

    public required DieByDifficulty DieByDifficulty { get; init; }

    public required ChallengeBonus ChallengeBonus { get; init; }

    public required UnmetConditionPolicy UnmetConditionPolicy { get; init; }

    public required CoinReward Coins { get; init; }

    public required CoopRules Coop { get; init; }
}

public sealed record PenaltyDice
{
    public required int Count { get; init; }

    public required int Sides { get; init; }
}

public enum MandatoryEvent
{
    Bad,
    None,
}

public sealed record DropRules
{
    public required PenaltyDice PenaltyDice { get; init; }

    public required bool AffectsPoints { get; init; }

    public required bool AffectsPosition { get; init; }

    public required MandatoryEvent MandatoryEvent { get; init; }
}

public sealed record FinishRules
{
    public required bool RequireApprovalForFirst { get; init; }

    public required EquatableArray<int> BonusByOrder { get; init; }

    public required int BonusAfterList { get; init; }
}

public enum Tiebreaker
{
    CompletedRuns,
    EarliestFinalScore,
}

public sealed record RankingRules
{
    public required EquatableArray<Tiebreaker> Tiebreakers { get; init; }
}

public sealed record MapRules
{
    public required int LinearLength { get; init; }
}

public sealed record RarityWeights
{
    public required int Common { get; init; }

    public required int Epic { get; init; }

    public required int Legendary { get; init; }
}

public enum ShopPriceReset
{
    RunCompleted,
    RunDropped,
}

public sealed record ShopRules
{
    public required int LotsPerRoll { get; init; }

    public required int LotLifetimeMinutes { get; init; }

    public required int RollCost { get; init; }

    public required int RerollCostStep { get; init; }

    public required EquatableArray<ShopPriceReset> ResetOn { get; init; }
}

public sealed record EconomyRules
{
    public required bool AllowNegativeCoins { get; init; }

    public required int InventoryLimit { get; init; }

    public required RarityWeights RarityWeights { get; init; }

    public required ShopRules Shop { get; init; }
}

public sealed record HostileCap
{
    public required bool Enabled { get; init; }

    public required int MaxActive { get; init; }
}

public sealed record EffectRules
{
    public required int MaxChainDepth { get; init; }

    public required int MaxEventsPerCommand { get; init; }

    public required HostileCap HostileCap { get; init; }

    public required bool AttacksOnlyOnHigherPoints { get; init; }
}

public sealed record InteractionRules
{
    public required int InviteTtlHours { get; init; }
}

/// <summary>Payout multiplier while the needed hours per day stay up to <see cref="UpTo"/>; null is «no upper bound».</summary>
public sealed record PayoutStep
{
    public required decimal? UpTo { get; init; }

    public required decimal Multiplier { get; init; }
}

public sealed record BetRules
{
    public required int MaxStake { get; init; }

    public required int MaxOpenBetsPerPlayer { get; init; }

    public required int WindowHoursAfterRoll { get; init; }

    public required EquatableArray<int> DeadlineOptionsDays { get; init; }

    public required EquatableArray<PayoutStep> PayoutByHoursPerDay { get; init; }
}

public sealed record SocialRules
{
    public required EquatableArray<string> Reactions { get; init; }

    public required int GalleryUploadsPerDay { get; init; }

    public required int CommentsPerDay { get; init; }

    public required bool SpectatorsCanVote { get; init; }
}

public enum AutoNomination
{
    MostHours,
    MostRuns,
    UnluckiestDice,
    MostDrops,
    MostAttacks,
}

public enum VotedNomination
{
    BestReview,
    GameOfSeason,
    FunniestMoment,
}

public sealed record NominationRules
{
    public required EquatableArray<AutoNomination> Auto { get; init; }

    public required EquatableArray<VotedNomination> Voted { get; init; }
}

public sealed record WeeklyChallengeRules
{
    public required int DefaultRewardCoins { get; init; }
}
