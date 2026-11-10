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
    public static bool Triggers(MoveReason reason) => reason is MoveReason.CompletionRoll or MoveReason.DropPenalty or MoveReason.Item;

    /// <summary>
    /// The cell's events for <paramref name="move"/>, already applied; nothing for a move that does not trigger. A shop cell
    /// grants its object on a stop and on a pass (SPEC «Магазин | при остановке и проходе», D-403); the other cells act on
    /// a stop only, so a paused move triggers only the shops it passed.
    /// </summary>
    public static IEnumerable<IGameEvent> After(SeasonState state, PlayerMoved move, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(move);
        if (move.Steps == 0 || !Triggers(move.Reason))
        {
            return [];
        }

        var shops = ShopGrants(state, move, context);
        if (move.Paused)
        {
            return shops;
        }

        var cell = state.Map.CellById(move.To);
        return [.. shops, .. Stop(state, move, cell)];
    }

    /// <summary>
    /// The coupons of the shop cells <paramref name="move"/> passed forward or stopped on (D-403): a pass back — a drop's
    /// penalty, a push — gives nothing, so drops do not farm coupons.
    /// </summary>
    public static List<IGameEvent> ShopGrants(SeasonState state, PlayerMoved move, EngineContext context)
    {
        if (move.Steps == 0 || !Triggers(move.Reason) || !state.Rules.Features.Shop || Effects.Targets.IsFirst(state, move.PlayerId))
        {
            return [];
        }

        var events = new List<IGameEvent>();
        foreach (var visit in Movement.Visits(move).Where(v => v.Kind == CellVisitKind.Stop || (v.Kind == CellVisitKind.Pass && move.Steps > 0)))
        {
            if (state.Map.CellById(visit.CellId) is { Type: CellType.Shop, Grants: { } grants } && state.Catalog.Live(grants) is { } definition)
            {
                var given = Inventory.Inventories.Give(state, context, move.PlayerId, definition, null, Inventory.ObjectSource.Cell, fromPlayerId: null);
                events.Add(given);
                state = SeasonEngine.Apply(state, given);
            }
        }

        return events;
    }

    private static IEnumerable<IGameEvent> Stop(SeasonState state, PlayerMoved move, Cell cell)
    {
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
