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
/// proof behind it — it stops a cell before (D-409). The cell where it stops and the shop cells it passes trigger (D-303).
/// </summary>
internal sealed class MoveHandler : ActionHandler<MoveAction>
{
    protected override void Execute(MoveAction action, EffectRun run)
    {
        var steps = run.Number(action.Steps);
        var player = run.State.Players[run.ActionTarget(action)];
        if (steps == 0 || !Positions.Movable(player))
        {
            return;
        }

        var map = run.State.Map;
        IReadOnlyList<string> path = steps > 0
            ? Movement.Forward(map, player.CellId, steps)
            : Movement.Backward(map, player.Path, -steps, checkpoints: true);
        if (steps > 0 && path.Count > 0 && map.CellById(path[^1]).Type == CellType.Finish)
        {
            path = [.. path.Take(path.Count - 1)];
        }

        if (path.Count == 0)
        {
            return;
        }

        var moved = new PlayerMoved(player.PlayerId, player.CellId, path[^1], steps, [.. path], MoveReason.Item, RunId: null);
        run.Emit(moved);
        run.Emit(CellStops.After(run.State, moved, run.Context));
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
        if (!Positions.Movable(player))
        {
            return;
        }

        var destination = action.Cell == NearestShortcut ? Shortcut(map, player.CellId) : action.Cell;
        if (destination is null || destination == player.CellId || !map.HasCell(destination) || map.CellById(destination).Type == CellType.Finish)
        {
            return;
        }

        run.Emit(new PlayerMoved(player.PlayerId, player.CellId, destination, Steps: 0, [destination], MoveReason.ItemTeleport, RunId: null));
    }

    // The first teleport cell ahead along the default branches, and where it leads.
    private static string? Shortcut(MapGraph map, string from)
    {
        var ahead = Movement.Forward(map, from, map.Cells.Count);
        return ahead.Select(map.CellById).FirstOrDefault(c => c.Type == CellType.Teleport)?.To;
    }
}
