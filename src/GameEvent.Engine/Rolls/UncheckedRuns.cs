using GameEvent.Engine.Finish;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Rolls;

/// <summary>
/// A player's runs waiting for the admin's check (D-134): completed, the proof neither approved nor rejected — sent and
/// pending, or not sent at all. The same runs make the admin's proof queue. The frozen first is never held: a check
/// takes nothing from him (Q-3), so his runs wait for nothing.
/// </summary>
public static class UncheckedRuns
{
    /// <summary>Whether a run waits for the admin's check</summary>
    public static bool Waits(RunState run) =>
        run.Status == RunStatus.Completed && run.Proof?.Status is null or ProofStatus.Pending;

    public static int Count(SeasonState state, Guid playerId) =>
        state.Players.TryGetValue(playerId, out var player) && Finishes.IsFrozen(player)
            ? 0
            : state.Runs.Values.Count(run => run.PlayerId == playerId && Waits(run));
}
