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

    internal static SeasonState Apply(SeasonState state, PlayerMoved e) =>
        state with { Players = state.Players.SetItem(e.PlayerId, state.Players[e.PlayerId] with { CellId = e.To }) };
}
