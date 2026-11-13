using GameEvent.Engine.Content;
using GameEvent.Engine.Map;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Effects.Actions;

/// <summary>
/// Whether a push or transfer may move a player (SPEC «Цели»): a finisher's position is fixed, and a
/// move paused at a fork is a step already begun (D-305, «эффекты от других … никогда к уже начатому»).
/// </summary>
internal static class Positions
{
    public static bool Movable(SeasonPlayer player) =>
        player.Finish is null && player.Choice?.Kind != Turns.ChoiceKind.Branch;
}

/// <summary>
/// <c>move</c>: a push of <c>steps</c> cells, forward along the default branches or back along the walked path, never past
/// a checkpoint or the start (D-306). A push does not reach the finish — the finish is the player's own move with a
/// proof behind it — it stops a cell before (D-409, D-322: <see cref="Movement.Push"/>). The cell where it stops and the shop cells it passes trigger (D-303).
/// </summary>
internal sealed class MoveHandler : ActionHandler<MoveAction>
{
    /// <summary>The answers of <see cref="MoveDirection.Chosen"/>; the interface names them in its own words.</summary>
    public const string Forward = "forward";
    public const string Back = "back";

    protected override void Execute(MoveAction action, EffectRun run)
    {
        // The way is asked before the steps are rolled: an event that waits for it rolls once, after the answer (D-511)
        var way = action.Direction == MoveDirection.Chosen ? run.NextChoice([Forward, Back]) : Forward;
        if (way is null)
        {
            return;
        }

        var player = run.State.Players[run.ActionTarget(action)];
        if (run.PositionLocked)
        {
            // After the deadline the token stays and the steps are not rolled: the skip is recorded (Н-13, S5.7-14)
            Skip(run, player);
            return;
        }

        var steps = run.Number(action.Steps);
        switch (action.Direction)
        {
            case MoveDirection.Chosen:
                steps = way == Back ? -steps : steps;
                break;
            case MoveDirection.Away:
                steps *= Away(run.State, run.State.Players[run.UserId].CellId, player.CellId);
                break;
            default:
                break;
        }

        if (steps == 0 || !Positions.Movable(player))
        {
            return;
        }

        var map = run.State.Map;
        // Only a run's own move reaches the finish: a push stops on the cell before it (D-322)
        var path = steps > 0
            ? Movement.Push(map, player.CellId, steps)
            : Movement.Backward(map, player.Path, -steps, checkpoints: true);
        if (path.Count == 0)
        {
            return;
        }

        var moved = new PlayerMoved(player.PlayerId, player.CellId, path[^1], steps, [.. path], MoveReason.Item, RunId: null);
        run.Emit(moved);
        run.Emit(CellStops.After(run.State, moved, run.Context));
    }

    // 1 when the target is closer to the finish than the user (ahead), -1 when farther, 0 when neither (D-477).
    private static int Away(SeasonState state, string userCell, string targetCell)
    {
        var distances = MapDistances.ToFinish(state.Map);
        return distances.TryGetValue(userCell, out var user) && distances.TryGetValue(targetCell, out var target)
            ? Math.Sign(user - target)
            : 0;
    }

    /// <summary>The record of a move a continuation skips after the deadline (Н-13): only for a player it could move.</summary>
    public static void Skip(EffectRun run, SeasonPlayer player)
    {
        if (Positions.Movable(player))
        {
            run.Emit(new EffectMoveSkipped(player.PlayerId, run.ObjectId));
        }
    }
}

/// <summary>
/// <c>teleport</c>: a transfer to a cell, or to where the nearest shortcut ahead leads (<c>nearestShortcut</c>). A transfer
/// does not trigger its destination (SPEC «Цепочки») and never lands on the finish (D-409).
/// </summary>
internal sealed class TeleportHandler : ActionHandler<TeleportAction>
{
    public const string NearestShortcut = "nearestShortcut";

    protected override void Execute(TeleportAction action, EffectRun run)
    {
        var player = run.State.Players[run.ActionTarget(action)];
        var map = run.State.Map;
        if (run.PositionLocked)
        {
            MoveHandler.Skip(run, player);
            return;
        }

        if (!Positions.Movable(player))
        {
            return;
        }

        var destination = action.Cell == NearestShortcut ? Shortcut(map, player.CellId) : action.Cell;
        if (destination is null || destination == player.CellId || !map.HasCell(destination) || map.CellById(destination).Type == CellType.Finish
            || BehindCheckpoint(map, player, destination))
        {
            return;
        }

        run.Emit(new PlayerMoved(player.PlayerId, player.CellId, destination, Steps: 0, [destination], MoveReason.ItemTeleport, RunId: null));
    }

    // A transfer never lands behind the checkpoint the player last reached (invariant 7, D-306, AU4-6): the checkpoint a push
    // back would stop on, and the destination farther from the finish than it.
    private static bool BehindCheckpoint(MapGraph map, SeasonPlayer player, string destination)
    {
        var back = Movement.Backward(map, player.Path, map.Cells.Count, checkpoints: true);
        var guard = map.CellById(player.CellId).Type == CellType.Checkpoint ? player.CellId : back.Count > 0 ? back[^1] : null;
        if (guard is null || map.CellById(guard).Type != CellType.Checkpoint)
        {
            return false;
        }

        var distances = MapDistances.ToFinish(map);
        return distances.TryGetValue(guard, out var fromGuard) && distances.TryGetValue(destination, out var fromDestination) && fromDestination > fromGuard;
    }

    // The first teleport cell ahead along the default branches, and where it leads.
    private static string? Shortcut(MapGraph map, string from)
    {
        var ahead = Movement.Forward(map, from, map.Cells.Count);
        return ahead.Select(map.CellById).FirstOrDefault(c => c.Type == CellType.Teleport)?.To;
    }
}
