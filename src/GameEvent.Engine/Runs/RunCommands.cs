using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Runs;

/// <summary>Start playing the offered game: Rolling → Playing. Creates the run with the roll-time snapshot.</summary>
public sealed record StartRun(Guid PlayerId) : ICommand;

/// <summary>
/// Complete the active run: Playing → Idle. Dice are rolled by the server in the same command,
/// then points are added and the token moves by the same sum (D-12).
/// <see cref="EstimatedHours"/> is required only when the snapshot has no hours, and then <see cref="HoursSource"/>
/// (a link or where the estimate comes from) too. <see cref="ChallengeDone"/> claims the game's challenge: extra dice,
/// checked with the proof (D-13). <see cref="Review"/> is optional (SPEC «Отзыв»).
/// </summary>
public sealed record CompleteRun(
    Guid PlayerId,
    Difficulty Difficulty,
    decimal? EstimatedHours = null,
    string? HoursSource = null,
    bool ChallengeDone = false,
    RunReview? Review = null) : ICommand;

/// <summary>The player reviews their own completed run, or changes the review (D-96).</summary>
public sealed record ReviewRun(Guid PlayerId, Guid RunId, RunReview Review) : ICommand;

/// <summary>The run started. <see cref="RolledAt"/> is kept: the tech reroll window counts from the roll (D-04).</summary>
[EventType("run-started")]
public sealed record RunStarted(Guid RunId, Guid PlayerId, Guid GameId, RunSnapshot Snapshot, DateTimeOffset RolledAt, DateTimeOffset StartedAt) : IGameEvent;

[EventType("run-completed")]
public sealed record RunCompleted(
    Guid RunId,
    Guid PlayerId,
    Difficulty Difficulty,
    decimal Hours,
    DateTimeOffset CompletedAt,
    string? HoursSource = null,
    bool ChallengeDone = false) : IGameEvent;

/// <summary>
/// Dice for a completed run, each die separately: <see cref="Dice"/> by the hours, <see cref="ChallengeDice"/> the
/// challenge bonus apart, so an hours correction touches only the first (D-14).
/// </summary>
[EventType("completion-rolled")]
public sealed record CompletionRolled(Guid RunId, Guid PlayerId, EquatableArray<Die> Dice, EquatableArray<Die> ChallengeDice = default) : IGameEvent;

/// <summary>The run's review was written or changed.</summary>
[EventType("run-reviewed")]
public sealed record RunReviewed(Guid RunId, Guid PlayerId, int Rating, string? Text, DateTimeOffset ReviewedAt) : IGameEvent;
