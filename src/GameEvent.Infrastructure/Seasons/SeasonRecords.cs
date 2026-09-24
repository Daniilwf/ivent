using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Infrastructure.Seasons;

// Read projection of season state, updated in the same transaction as the events of each command.
// The log is the source of truth: these rows always equal a fold of it (integrity check, L4).
// Everything that is filtered or sorted is a column; snapshots and dice are JSON.

/// <summary>A manual effect waiting to be resolved (SPEC «Модель данных»: PendingManualEffect).</summary>
public sealed class PendingManualEffectRecord
{
    public Guid Id { get; set; }

    public Guid SeasonId { get; set; }

    public Guid PlayerId { get; set; }

    public Engine.Rulesets.EventKind DrawEvent { get; set; }

    public Engine.Effects.ManualEffectSource Source { get; set; }

    public Guid? RunId { get; set; }
}

/// <summary>A game excluded for one player of the season (SPEC «Модель данных»: PlayerGameExclusion).</summary>
public sealed class PlayerGameExclusionRecord
{
    public Guid PlayerId { get; set; }

    public Guid GameId { get; set; }

    public Engine.Rolls.ExclusionReason Reason { get; set; }
}

public sealed class SeasonRecord
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public SeasonStatus Status { get; set; }

    public DateTimeOffset? Deadline { get; set; }

    public int RulesetVersion { get; set; }

    /// <summary>The rules in force, projected from the log (D-82); the history is the log's ruleset events.</summary>
    public required string RulesetJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One version of a season's rules (SPEC «Модель данных»: Ruleset — version, config, date, who changed it).
/// A projection of season-created and ruleset-changed events (D-82): the log stays the source of truth.
/// </summary>
public sealed class RulesetRecord
{
    public Guid SeasonId { get; set; }

    public int Version { get; set; }

    public required string Json { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who changed the rules; null for the version created with the season by the system.</summary>
    public Guid? AuthorId { get; set; }
}

public sealed class SeasonPlayerRecord
{
    /// <summary>Participation id (the engine's PlayerId), not the user id.</summary>
    public Guid Id { get; set; }

    public Guid SeasonId { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    public required string CellId { get; set; }

    public int Points { get; set; }

    public int Coins { get; set; }

    /// <summary>Other resources as a JSON object; never filtered or sorted in SQL (invariant 9).</summary>
    public required string ResourcesJson { get; set; }

    public bool IsInactive { get; set; }

    /// <summary>The player's path in segments, as JSON (<see cref="Engine.Map.PlayerPath"/>); needed to move back.</summary>
    public required string PathJson { get; set; }

    public TurnPhase Phase { get; set; }

    /// <summary>The offered game waiting for «Начать», as JSON (<see cref="Engine.Rolls.RollOffer"/>).</summary>
    public string? OfferJson { get; set; }

    /// <summary>The pending choice, as JSON (<see cref="Engine.Turns.PendingChoice"/>); one per player at most.</summary>
    public string? ChoiceJson { get; set; }

    public int RerollsThisRoll { get; set; }

    public Guid? ActiveRunId { get; set; }
}

public sealed class RunRecord
{
    public Guid Id { get; set; }

    public Guid SeasonId { get; set; }

    public Guid PlayerId { get; set; }

    public Guid GameId { get; set; }

    public RunStatus Status { get; set; }

    public required string SnapshotJson { get; set; }

    public DateTimeOffset RolledAt { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public Difficulty? Difficulty { get; set; }

    public decimal? Hours { get; set; }

    public required string DiceJson { get; set; }

    /// <summary>The challenge bonus dice, apart from the dice by hours (D-14, D-96).</summary>
    public required string ChallengeDiceJson { get; set; }

    /// <summary>Where the player's hours estimate comes from, when the pool had no hours.</summary>
    public string? HoursSource { get; set; }
}

/// <summary>A review of a completed run (SPEC «Модель данных»: Review): one per run, the latest wins.</summary>
public sealed class ReviewRecord
{
    public Guid RunId { get; set; }

    public Guid SeasonId { get; set; }

    public Guid PlayerId { get; set; }

    public Guid GameId { get; set; }

    public int Rating { get; set; }

    public string? Text { get; set; }
}
