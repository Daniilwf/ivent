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
    RunCorrection,
    ProofRejected,
    FinishBonus,
    FinishBonusRevoked,
}

/// <summary>What changed a player's coins. Coins always equal the sum of deltas in non-undone events.</summary>
public enum CoinsReason
{
    StartingBalance,
    AdminAdjustment,
    Reroll,
    CompletionReward,
    RunCorrection,
    ProofRejected,
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
    // Every non-zero change is numbered in the season; the player keeps the number of his last one (D-100).
    public static SeasonState Apply(SeasonState state, PointsChanged e)
    {
        var tick = e.Delta == 0 ? state.PointsChanges : state.PointsChanges + 1;
        return Update(state with { PointsChanges = tick }, e.PlayerId, p => p with
        {
            Points = p.Points + e.Delta,
            PointsTick = e.Delta == 0 ? p.PointsTick : tick,

            // The finish bonus a player holds now (Q-4: at most one).
            Finish = e.Reason is PointsReason.FinishBonus or PointsReason.FinishBonusRevoked && p.Finish is { } finish
                ? finish with { Bonus = finish.Bonus + e.Delta }
                : p.Finish,
        });
    }

    public static SeasonState Apply(SeasonState state, CoinsChanged e) =>
        Update(state, e.PlayerId, p => p with { Coins = p.Coins + e.Delta });

    public static SeasonState Apply(SeasonState state, ResourceChanged e) =>
        Update(state, e.PlayerId, p => p with { Resources = p.Resources.Add(e.Resource, e.Delta) });

    private static SeasonState Update(SeasonState state, Guid playerId, Func<SeasonPlayer, SeasonPlayer> change) =>
        state with { Players = state.Players.SetItem(playerId, change(state.Players[playerId])) };
}
