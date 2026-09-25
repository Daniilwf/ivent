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
    public static IReadOnlyList<string> Backward(MapGraph map, PlayerPath path, int steps)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(path);

        var entered = new List<string>();
        var walked = path.Segments[^1].Cells.ToList();
        var current = map.CellById(path.Current);
        for (var i = 0; i < steps && current.Type != CellType.Start; i++)
        {
            string? previous;
            if (walked.Count >= 2)
            {
                walked.RemoveAt(walked.Count - 1);
                previous = walked[^1];
            }
            else
            {
                previous = PrimaryBackward(map, current.Id)?.From;
            }

            if (previous is null)
            {
                break;
            }

            current = map.CellById(previous);
            entered.Add(current.Id);
        }

        return entered;
    }

    // The editor requires a primary edge on cells with several inputs; a single input needs no mark.
    private static Edge? PrimaryBackward(MapGraph map, string cellId)
    {
        var incoming = map.Edges.Where(e => e.To == cellId).ToList();
        return incoming.FirstOrDefault(e => e.IsPrimaryBackward) ?? (incoming.Count == 1 ? incoming[0] : null);
    }

    /// <summary>
    /// The trigger points of a move, in path order: for each entered cell a <see cref="CellVisitKind.MoveStep"/>,
    /// then <see cref="CellVisitKind.Pass"/>, or <see cref="CellVisitKind.Stop"/> for the last cell. A transfer
    /// (<c>Steps == 0</c>) has none, and a move that entered no cell (blocked at the finish or the start) stops nowhere.
    /// </summary>
    public static IReadOnlyList<CellVisit> Visits(PlayerMoved moved)
    {
        ArgumentNullException.ThrowIfNull(moved);

        if (moved.Steps == 0)
        {
            return [];
        }

        var visits = new List<CellVisit>(moved.Path.Count * 2);
        for (var i = 0; i < moved.Path.Count; i++)
        {
            visits.Add(new CellVisit(moved.Path[i], CellVisitKind.MoveStep));
            visits.Add(new CellVisit(moved.Path[i], i == moved.Path.Count - 1 ? CellVisitKind.Stop : CellVisitKind.Pass));
        }

        return visits;
    }

    internal static SeasonState Apply(SeasonState state, PlayerMoved e)
    {
        var player = state.Players[e.PlayerId];
        state = state with { Players = state.Players.SetItem(e.PlayerId, player with { CellId = e.To, Path = player.Path.After(e) }) };

        // A run whose move lands on the finish is marked: its proof goes on top of the queue (SPEC «Уточнения»).
        return e.RunId is { } runId && e.Steps > 0 && state.Map.CellById(e.To).Type == CellType.Finish
            ? state with { Runs = state.Runs.SetItem(runId, state.Runs[runId] with { ReachedFinish = true }) }
            : state;
    }
}
