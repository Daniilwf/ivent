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
/// Rules fixed for a run at roll time. <see cref="Hours"/> is the game length from the pool;
/// null means the player must give an estimate when completing.
/// </summary>
public sealed record RunSnapshot(int RulesetVersion, decimal? Hours, DiceCountRule DiceCount, DieByDifficulty DieByDifficulty);

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
    EquatableArray<Die> Dice);
