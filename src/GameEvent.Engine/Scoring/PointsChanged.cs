using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Scoring;

/// <summary>What changed a player's points. Points always equal the sum of deltas in non-undone events.</summary>
public enum PointsReason
{
    CompletionRoll,
}

/// <summary>Points changed by <see cref="Delta"/>; <see cref="RunId"/> links the change to a run when there is one.</summary>
[EventType("points-changed")]
public sealed record PointsChanged(Guid PlayerId, int Delta, PointsReason Reason, Guid? RunId) : IGameEvent;

internal static class PointsLedger
{
    public static SeasonState Apply(SeasonState state, PointsChanged e)
    {
        var player = state.Players[e.PlayerId];
        return state with { Players = state.Players.SetItem(e.PlayerId, player with { Points = player.Points + e.Delta }) };
    }
}
