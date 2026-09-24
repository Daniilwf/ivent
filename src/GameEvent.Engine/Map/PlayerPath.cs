using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Map;

/// <summary>A run of cells walked edge by edge, oldest first.</summary>
public sealed record PathSegment(EquatableArray<string> Cells);

/// <summary>
/// A player's own path (SPEC «Движение»: stored in segments). A transfer (teleport, admin move, starting cell)
/// starts a new segment. Moving back retraces the last segment's walked edges; when it runs out, the move follows
/// primary backward edges and the last segment restarts at the cell reached.
/// </summary>
public sealed record PlayerPath(EquatableArray<PathSegment> Segments)
{
    /// <summary>The path of a player placed on <paramref name="cellId"/> with no walked edges yet.</summary>
    public static PlayerPath At(string cellId) => new([new PathSegment([cellId])]);

    /// <summary>The cell the player stands on.</summary>
    public string Current => Segments[^1].Cells[^1];

    /// <summary>The path after <paramref name="moved"/>: forward extends, back retraces, a transfer starts anew.</summary>
    public PlayerPath After(PlayerMoved moved)
    {
        ArgumentNullException.ThrowIfNull(moved);

        if (moved.Steps == 0)
        {
            return new([.. Segments, new PathSegment([moved.To])]);
        }

        var cells = Segments[^1].Cells.ToList();
        if (moved.Steps > 0)
        {
            cells.AddRange(moved.Path);
        }
        else
        {
            foreach (var cell in moved.Path)
            {
                if (cells.Count >= 2 && cells[^2] == cell)
                {
                    cells.RemoveAt(cells.Count - 1);
                }
                else
                {
                    cells = [cell];
                }
            }
        }

        return new([.. Segments.Take(Segments.Count - 1), new PathSegment([.. cells])]);
    }
}
