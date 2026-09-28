using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace GameEvent.Engine.Map;

/// <summary>How far each cell is from the finish: the leaderboard's «клетки до финиша» and the measure of a reject (D-321).</summary>
public static class MapDistances
{
    // A published map never changes, and a fold asks for the same one on every move: computed once per map object
    private static readonly ConditionalWeakTable<MapGraph, IReadOnlyDictionary<string, int>> s_cache = [];

    /// <summary>The fewest forward steps (along edges) from each cell to a finish cell; cells that cannot reach one are absent.</summary>
    public static IReadOnlyDictionary<string, int> ToFinish(MapGraph map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return s_cache.GetValue(map, Compute);
    }

    /// <summary>
    /// How many cells closer to the finish <paramref name="moved"/> brought the player: negative when further (a snake, a
    /// longer branch). A cell that cannot reach the finish counts by the cells walked.
    /// </summary>
    public static int Closer(MapGraph map, PlayerMoved moved)
    {
        ArgumentNullException.ThrowIfNull(moved);
        var distances = ToFinish(map);
        return distances.TryGetValue(moved.From, out var from) && distances.TryGetValue(moved.To, out var to)
            ? from - to
            : Math.Sign(moved.Steps) * moved.Path.Count;
    }

    private static IReadOnlyDictionary<string, int> Compute(MapGraph map)
    {
        // Breadth-first from the finish cells against the arrows.
        var incoming = map.Edges.ToLookup(e => e.To, e => e.From);
        var distances = new Dictionary<string, int>();
        var queue = new Queue<string>();
        foreach (var finish in map.Cells.Where(c => c.Type == CellType.Finish))
        {
            distances[finish.Id] = 0;
            queue.Enqueue(finish.Id);
        }

        while (queue.TryDequeue(out var cell))
        {
            foreach (var from in incoming[cell])
            {
                if (distances.TryAdd(from, distances[cell] + 1))
                {
                    queue.Enqueue(from);
                }
            }
        }

        return distances.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
