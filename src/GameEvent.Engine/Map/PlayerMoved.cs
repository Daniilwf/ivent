using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Map;

/// <summary>What moved the token. Rejecting a run takes back the cells of moves linked to it.</summary>
public enum MoveReason
{
    CompletionRoll,
}

/// <summary>
/// The player's token moved. <see cref="Path"/> lists the cells entered in order, ending at <see cref="To"/>.
/// <see cref="Steps"/> is how many steps were requested; steps beyond the finish burn (D-47).
/// <see cref="RunId"/> links the move to a run when there is one.
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
