using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Runs;

/// <summary>Start playing the offered game: Rolling → Playing. Creates the run with the roll-time snapshot.</summary>
public sealed record StartRun(Guid PlayerId) : ICommand;

/// <summary>
/// Complete the active run: Playing → Idle. Dice are rolled by the server in the same command,
/// then points are added and the token moves by the same sum (D-12).
/// <see cref="EstimatedHours"/> is required only when the snapshot has no hours.
/// </summary>
public sealed record CompleteRun(Guid PlayerId, Difficulty Difficulty, decimal? EstimatedHours = null) : ICommand;

[EventType("run-started")]
public sealed record RunStarted(Guid RunId, Guid PlayerId, Guid GameId, RunSnapshot Snapshot, DateTimeOffset StartedAt) : IGameEvent;

[EventType("run-completed")]
public sealed record RunCompleted(Guid RunId, Guid PlayerId, Difficulty Difficulty, decimal Hours, DateTimeOffset CompletedAt) : IGameEvent;

/// <summary>Dice for a completed run, each die separately.</summary>
[EventType("completion-dice-rolled")]
public sealed record CompletionDiceRolled(Guid RunId, Guid PlayerId, EquatableArray<Die> Dice) : IGameEvent;
