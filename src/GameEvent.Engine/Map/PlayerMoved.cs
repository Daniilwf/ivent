using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Map;

/// <summary>What moved the token. Rejecting a run takes back the cells of moves linked to it.</summary>
public enum MoveReason
{
    CompletionRoll,
    StartingCell,
    AdminAdjustment,
    DropPenalty,
    RunCorrection,
    ProofRejected,
}

/// <summary>
/// The player's token moved. <see cref="Path"/> lists the cells entered in order, ending at <see cref="To"/>.
/// <see cref="Steps"/> is how many steps were requested: positive forward, negative back, 0 for a transfer
/// (teleport, admin move, starting cell). Steps beyond the finish or past the start are lost (D-47, M2).
/// A move that entered no cell (blocked at the finish or the start) writes no event, so <see cref="Path"/> is never
/// empty. <see cref="RunId"/> links the move to a run when there is one.
/// </summary>
[EventType("player-moved")]
public sealed record PlayerMoved(
    Guid PlayerId,
    string From,
    string To,
    int Steps,
    EquatableArray<string> Path,
    MoveReason Reason,
    Guid? RunId) : IGameEvent;
