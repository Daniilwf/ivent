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

/// <summary>
/// A predicate over pool games (SPEC «Уточнения»: filters are a predicate). Built by the engine, never stored.
/// <see cref="Tags"/> — the genres it imposes, if it filters by tags: a kept filter fixes them in the run's snapshot (D-325).
/// </summary>
public sealed record RollFilter(RollFilterPriority Priority, string Name, Func<Game, bool> Matches, Kernel.EquatableArray<string> Tags = default);

/// <summary>
/// Combines roll filters (G12, D-92): from the highest priority down, each filter narrows the games left. A lower filter
/// whose intersection leaves no available game is skipped: the more important one wins. The top filter itself is kept
/// even when it leaves nothing — then the roll is empty and the admin gets the signal — except a zone filter, which is
/// lifted on an empty pool (SPEC «Уточнения»: пустой пул). Filters of the same priority keep the given order.
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
        var narrowedYet = false;
        foreach (var filter in filters.OrderByDescending(f => f.Priority))
        {
            var narrowed = games.Where(filter.Matches).ToList();
            if (narrowed.Any(isAvailable))
            {
                games = narrowed;
                narrowedYet = true;
            }
            else if (!narrowedYet && filter.Priority != RollFilterPriority.Zone)
            {
                return narrowed;
            }
        }

        return games;
    }
}
