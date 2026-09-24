using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Map;

/// <summary>Movement along the map graph.</summary>
public static class Movement
{
    /// <summary>
    /// Cells entered, in order, on <paramref name="steps"/> forward steps from <paramref name="from"/>
    /// along default forward edges. The finish is a stop cell: extra steps burn.
    /// </summary>
    public static IReadOnlyList<string> Forward(MapGraph map, string from, int steps)
    {
        ArgumentNullException.ThrowIfNull(map);

        var path = new List<string>();
        var current = map.CellById(from);
        for (var i = 0; i < steps && current.Type != CellType.Finish; i++)
        {
            var edge = map.Edges.FirstOrDefault(e => e.From == current.Id && e.IsDefaultForward);
            if (edge is null)
            {
                break;
            }

            current = map.CellById(edge.To);
            path.Add(current.Id);
        }

        return path;
    }

    /// <summary>
    /// Cells entered, in order, on <paramref name="steps"/> steps back from where <paramref name="path"/> stands:
    /// first back along the walked edges of the last segment, then along primary backward edges (the edge a cell
    /// is entered by when history runs out). Never past the start: missing steps are lost (M2, RR3).
    /// </summary>
    public static IReadOnlyList<string> Backward(MapGraph map, PlayerPath path, int steps) =>
        throw new NotImplementedException("C3");

    /// <summary>
    /// The trigger points of a move, in path order: for each entered cell a <see cref="CellVisitKind.MoveStep"/>,
    /// then <see cref="CellVisitKind.Pass"/>, or <see cref="CellVisitKind.Stop"/> for the last cell. A transfer
    /// (<c>Steps == 0</c>) has none, and a move that entered no cell (blocked at the finish or the start) stops nowhere.
    /// </summary>
    public static IReadOnlyList<CellVisit> Visits(PlayerMoved moved) =>
        throw new NotImplementedException("C3");

    internal static SeasonState Apply(SeasonState state, PlayerMoved e)
    {
        var player = state.Players[e.PlayerId];
        return state with { Players = state.Players.SetItem(e.PlayerId, player with { CellId = e.To, Path = player.Path.After(e) }) };
    }
}
