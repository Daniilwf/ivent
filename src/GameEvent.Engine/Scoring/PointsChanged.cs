using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Scoring;

/// <summary>What changed a player's points. Points always equal the sum of deltas in non-undone events.</summary>
public enum PointsReason
{
    CompletionRoll,
    StartingBalance,
    AdminAdjustment,
    DropPenalty,
}

/// <summary>What changed a player's coins. Coins always equal the sum of deltas in non-undone events.</summary>
public enum CoinsReason
{
    StartingBalance,
    AdminAdjustment,
    Reroll,
}

/// <summary>What changed another resource of a player.</summary>
public enum ResourceReason
{
    AdminAdjustment,
    Reroll,
}

/// <summary>Points changed by <see cref="Delta"/>; <see cref="RunId"/> links the change to a run when there is one.</summary>
[EventType("points-changed")]
public sealed record PointsChanged(Guid PlayerId, int Delta, PointsReason Reason, Guid? RunId) : IGameEvent;

[EventType("coins-changed")]
public sealed record CoinsChanged(Guid PlayerId, int Delta, CoinsReason Reason, Guid? RunId) : IGameEvent;

[EventType("resource-changed")]
public sealed record ResourceChanged(Guid PlayerId, string Resource, int Delta, ResourceReason Reason) : IGameEvent;

internal static class PointsLedger
{
    public static SeasonState Apply(SeasonState state, PointsChanged e) =>
        Update(state, e.PlayerId, p => p with { Points = p.Points + e.Delta });

    public static SeasonState Apply(SeasonState state, CoinsChanged e) =>
        Update(state, e.PlayerId, p => p with { Coins = p.Coins + e.Delta });

    public static SeasonState Apply(SeasonState state, ResourceChanged e) =>
        Update(state, e.PlayerId, p => p with { Resources = p.Resources.Add(e.Resource, e.Delta) });

    private static SeasonState Update(SeasonState state, Guid playerId, Func<SeasonPlayer, SeasonPlayer> change) =>
        state with { Players = state.Players.SetItem(playerId, change(state.Players[playerId])) };
}
