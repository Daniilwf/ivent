using System.Text.Json.Serialization;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.GlobalEvents;

/// <summary>What a global goal counts (D-518): the HowLongToBeat hours of the completed games, or the completions.</summary>
public enum GoalMetric
{
    Hours,
    Runs,
}

/// <summary>Where a global event is: running until its end, or ended (D-517).</summary>
public enum GlobalEventStatus
{
    Active,
    Ended,
}

/// <summary>What ended a global event (D-517): its term by the timer, the admin early, or the season's deadline (D-516).</summary>
public enum GlobalEventEndReason
{
    Timer,
    Admin,
    Deadline,
}

/// <summary>Why a global goal's progress changed (D-518, D-546 (6)).</summary>
public enum GlobalProgressReason
{
    /// <summary>A run was completed in the event's window.</summary>
    Completed,

    /// <summary>The run's proof was rejected: its contribution is gone.</summary>
    Rejected,

    /// <summary>The admin corrected the run's hours: an hours goal follows by the difference.</summary>
    HoursCorrected,
}

/// <summary>
/// The goal of a global event (D-517): <see cref="Target"/> of <see cref="Metric"/> together; <see cref="Reward"/> — the
/// effect everyone gets when it is reached, settled for everyone (D-537); none — a progress bar only.
/// </summary>
public sealed record GlobalEventGoal
{
    public required GoalMetric Metric { get; init; }

    public required int Target { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectSpec? Reward { get; init; }
}

/// <summary>
/// The admin launches a global event (D-517): the event <see cref="ObjectId"/> of the season's content for
/// <see cref="DurationHours"/>, with a goal or without.
/// </summary>
public sealed record LaunchGlobalEvent(string ObjectId, int DurationHours, GlobalEventGoal? Goal = null) : ICommand;

/// <summary>The admin ends a running global event before its term (D-517).</summary>
public sealed record EndGlobalEvent(Guid GlobalEventId) : ICommand;

/// <summary>
/// A global event was launched (D-517): the snapshot of its definition, its term, its goal; <see cref="Kind"/> — the kind of
/// the deck that holds it (D-706), written only when one does. Its effect on everyone follows as a settlement of its own
/// (D-537), or as a poll first when it asks a vote (EV23, D-753).
/// </summary>
[EventType("global-event-launched")]
public sealed record GlobalEventLaunched(
    Guid GlobalEventId, string ObjectId, ObjectDefinition Definition, DateTimeOffset StartedAt, DateTimeOffset EndsAt) : IGameEvent
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GlobalEventGoal? Goal { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventKind? Kind { get; init; }
}

/// <summary>A global event ended (D-517): its banner goes, completions no longer count; a goal not reached gives nothing.</summary>
[EventType("global-event-ended")]
public sealed record GlobalEventEnded(Guid GlobalEventId, GlobalEventEndReason Reason) : IGameEvent;

/// <summary>
/// The progress of a global goal changed by <see cref="Delta"/> to <see cref="Total"/> (D-518): the run
/// <see cref="RunId"/> was completed in the window, rejected, or its hours were corrected.
/// </summary>
[EventType("global-progress-changed")]
public sealed record GlobalProgressChanged(Guid GlobalEventId, Guid RunId, GlobalProgressReason Reason, decimal Delta, decimal Total) : IGameEvent;

/// <summary>
/// A global goal was reached by the run <see cref="RunId"/> (D-518): once per event; its reward, if any, is queued for
/// everyone in the same command and stays whatever happens to the progress later.
/// </summary>
[EventType("global-goal-reached")]
public sealed record GlobalGoalReached(Guid GlobalEventId, Guid RunId) : IGameEvent;

/// <summary>What one run brought to a global goal (an hours goal: its hours; a goal of runs: 1).</summary>
public sealed record GlobalContribution(Guid RunId, decimal Amount);

/// <summary>
/// A global event of the season (D-517, D-518): its snapshot and term, its goal with the contributions in force and whether
/// it was reached; <see cref="PollId"/> — the poll its effect waits for (EV23), written only when set.
/// </summary>
public sealed record GlobalEventState(
    Guid GlobalEventId,
    string ObjectId,
    ObjectDefinition Definition,
    DateTimeOffset StartedAt,
    DateTimeOffset EndsAt,
    GlobalEventStatus Status,
    EquatableArray<GlobalContribution> Contributions,
    bool Reached)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GlobalEventGoal? Goal { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventKind? Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GlobalEventEndReason? EndReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PollId { get; init; }

    /// <summary>The goal's progress: the sum of the contributions in force.</summary>
    [JsonIgnore]
    public decimal Progress => Contributions.Sum(c => c.Amount);

    /// <summary>The window is <c>[StartedAt, EndsAt)</c> by the moment a run was completed (D-518).</summary>
    public bool InWindow(DateTimeOffset moment) => moment >= StartedAt && moment < EndsAt;

    /// <summary>Running at <paramref name="now"/>: not ended and its term not up yet (the timer may not have fired).</summary>
    public bool RunsAt(DateTimeOffset now) => Status == GlobalEventStatus.Active && now < EndsAt;
}
