using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Scoring;

/// <summary>What changed a player's points. Points always equal the sum of deltas in non-undone events.</summary>
public enum PointsReason
{
    CompletionDice,
}

/// <summary>Points changed by <see cref="Delta"/>; <see cref="RunId"/> links the change to a run when there is one.</summary>
[EventType("points-changed")]
public sealed record PointsChanged(Guid PlayerId, int Delta, PointsReason Reason, Guid? RunId) : IGameEvent;
