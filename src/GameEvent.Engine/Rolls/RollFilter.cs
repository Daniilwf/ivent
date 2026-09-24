using GameEvent.Engine.Pool;

namespace GameEvent.Engine.Rolls;

/// <summary>Where a roll filter comes from; a higher value wins (SPEC «Пул игр и ролл»: effect, then zone, then normal).</summary>
public enum RollFilterPriority
{
    /// <summary>The ordinary roll, for example the length limit of the last days (<c>roll.lastDaysLengthFilter</c>).</summary>
    Normal = 1,

    /// <summary>The map zone the player stands in (stage 2).</summary>
    Zone = 2,

    /// <summary>A special roll or an effect (stage 4).</summary>
    Effect = 3,
}

/// <summary>A predicate over pool games (SPEC «Уточнения»: filters are a predicate). Built by the engine, never stored.</summary>
public sealed record RollFilter(RollFilterPriority Priority, string Name, Func<Game, bool> Matches);

/// <summary>
/// Combines roll filters (G12): from the highest priority down, each filter narrows the games left; a filter that would
/// leave no available game is dropped, so the more important one wins and an empty intersection never empties the roll.
/// Filters of the same priority are applied in the given order.
/// </summary>
public static class RollFilters
{
    /// <summary>
    /// Games of <paramref name="candidates"/> that pass the kept filters. <paramref name="isAvailable"/> says which games
    /// count when deciding whether a filter leaves anything (a game that would only be a miss does not).
    /// </summary>
    public static IReadOnlyList<Game> Apply(IReadOnlyList<Game> candidates, Func<Game, bool> isAvailable, IEnumerable<RollFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(isAvailable);
        ArgumentNullException.ThrowIfNull(filters);

        // OrderByDescending is stable: filters of one priority keep the given order.
        var games = candidates;
        foreach (var filter in filters.OrderByDescending(f => f.Priority))
        {
            var narrowed = games.Where(filter.Matches).ToList();
            if (narrowed.Any(isAvailable))
            {
                games = narrowed;
            }
        }

        return games;
    }
}
