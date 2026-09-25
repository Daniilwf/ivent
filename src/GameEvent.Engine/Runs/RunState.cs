using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Runs;

public enum RunStatus
{
    Playing,
    Completed,
    Dropped,
    TechRerolled,
}

public enum Difficulty
{
    Easy,
    Normal,
    Hard,
    Extreme,
}

/// <summary>One die of a roll, stored separately so hour corrections can add or remove dice.</summary>
public sealed record Die(int Sides, int Value);

/// <summary>
/// Rules fixed for a run at roll time (SPEC «Снапшот»). <see cref="Hours"/> is the game length from the pool;
/// null means the player must give an estimate when completing. <see cref="TechRerollWindowHours"/> is the tech reroll
/// window after the roll (D-94); <see cref="ChallengeExtraDice"/> and <see cref="Coins"/> are the challenge bonus and
/// the coin reward for completing (D-96).
/// </summary>
public sealed record RunSnapshot(
    int RulesetVersion,
    decimal? Hours,
    DiceCountRule DiceCount,
    DieByDifficulty DieByDifficulty,
    int TechRerollWindowHours,
    int ChallengeExtraDice = 0,
    CoinReward? Coins = null);

/// <summary>A review of a completed run (SPEC «Отзыв»): a rating 1–10 and an optional text.</summary>
public sealed record RunReview(int Rating, string? Text);

/// <summary>An attempt to complete one rolled game.</summary>
public sealed record RunState(
    Guid RunId,
    Guid PlayerId,
    Guid GameId,
    RunStatus Status,
    RunSnapshot Snapshot,
    DateTimeOffset RolledAt,
    DateTimeOffset StartedAt,
    Difficulty? Difficulty,
    decimal? Hours,
    EquatableArray<Die> Dice,
    EquatableArray<Die> ChallengeDice = default,
    string? HoursSource = null,
    RunReview? Review = null);
