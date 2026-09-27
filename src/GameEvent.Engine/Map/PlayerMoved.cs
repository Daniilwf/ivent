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

    /// <summary>A stop on a teleport cell transferred the token (D-303).</summary>
    Teleport,
}

/// <summary>
/// The player's token moved. <see cref="Path"/> lists the cells entered in order, ending at <see cref="To"/>.
/// <see cref="Steps"/> is how many steps were requested: positive forward, negative back, 0 for a transfer
/// (teleport, admin move, starting cell). Steps beyond the finish or past the start are lost (D-47, M2).
/// A move that entered no cell (blocked at the finish or the start) writes no event, so <see cref="Path"/> is never
/// empty. <see cref="RunId"/> links the move to a run when there is one. <see cref="Paused"/>: the player's own move
/// reached a fork with steps left and waits for the branch (D-304): its last cell is passed, not stopped on; the rest
/// of the steps follow in the move after the choice, whose <see cref="Steps"/> are the steps left. A paused move keeps
/// the whole throw in <see cref="Steps"/>, so its burned steps are not <c>Steps − Path.Count</c>. Written only when set, so moves of stage 1 read and write as before.
/// </summary>
[EventType("player-moved")]
public sealed record PlayerMoved(
    Guid PlayerId,
    string From,
    string To,
    int Steps,
    EquatableArray<string> Path,
    MoveReason Reason,
    Guid? RunId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool Paused = false) : IGameEvent;

/// <summary>
/// The player's own move stopped at fork <see cref="CellId"/> with <see cref="Steps"/> left: they choose one of
/// <see cref="Options"/> (the cells the branches lead to, the default first) with <see cref="Turns.MakeChoice"/> (D-304).
/// </summary>
[EventType("branch-choice-requested")]
public sealed record BranchChoiceRequested(
    Guid PlayerId,
    Guid ChoiceId,
    string CellId,
    EquatableArray<string> Options,
    int Steps,
    MoveReason Reason,
    Guid? RunId) : IGameEvent;
