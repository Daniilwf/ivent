using GameEvent.Engine.Kernel;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Map;

/// <summary>
/// What a cell does when a move stops on it (SPEC «Движение», D-303): a teleport transfers the token (its destination
/// does not trigger), a points bonus gives points; other cells do nothing yet. Only the moves of the game stop on cells —
/// the player's dice and the drop penalty (later pushes of effects); transfers, corrections and rejects do not.
/// </summary>
internal static class CellStops
{
    /// <summary>Whether a move of <paramref name="reason"/> ends with a stop that triggers the cell.</summary>
    public static bool Triggers(MoveReason reason) => reason is MoveReason.CompletionRoll or MoveReason.DropPenalty;

    /// <summary>The cell's events for <paramref name="move"/>, already applied; nothing for a paused move or one that does not trigger.</summary>
    public static IEnumerable<IGameEvent> After(SeasonState state, PlayerMoved move)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(move);
        if (move.Paused || move.Steps == 0 || !Triggers(move.Reason))
        {
            return [];
        }

        var cell = state.Map.CellById(move.To);
        return cell switch
        {
            // Consequences of cells stay when a run is rejected (SPEC «Награда»): they are not linked to the run.
            { Type: CellType.Teleport, To: { } to } => [new PlayerMoved(move.PlayerId, cell.Id, to, Steps: 0, [to], MoveReason.Teleport, RunId: null)],
            { Type: CellType.PointsBonus, Amount: { } amount } when amount != 0 =>
                [new PointsChanged(move.PlayerId, amount, PointsReason.CellBonus, RunId: null)],
            _ => [],
        };
    }
}
