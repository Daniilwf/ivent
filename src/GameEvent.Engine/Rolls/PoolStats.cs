using GameEvent.Engine.Pool;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Rolls;

/// <summary>
/// How many games of a category can be rolled right now (G11: small categories burn out, the admin watches this):
/// not deleted, not completed in the season, not played or reserved by anyone. Personal exclusions are not subtracted.
/// </summary>
public sealed record CategoryStat(string Category, int Weight, int Available);

/// <summary>Pool health for the admin panel (SPEC «Уточнения»: empty pool → a signal in the admin panel).</summary>
public static class PoolStats
{
    /// <summary>Every category of the pool, ordered by name, with its available games.</summary>
    public static IReadOnlyList<CategoryStat> Categories(SeasonState state, IPoolView pool)
    {
        ArgumentNullException.ThrowIfNull(pool);

        // Nobody's personal exclusions: the status as a player with none sees it.
        var status = SeasonGameStatus.For(state, Guid.Empty);
        var available = pool.Games.Where(g => status.Of(g) == GameAvailability.Available).ToList();
        return [.. pool.Categories
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new CategoryStat(c.Name, c.Weight, available.Count(g => Rolling.InCategory(g, c))))];
    }

    /// <summary>
    /// Players of the season whose next roll would find no game (their exclusions included), ordered by name:
    /// the signal for the admin. Empty when the season is not running.
    /// </summary>
    public static IReadOnlyList<Guid> PlayersWithoutGames(SeasonState state, IPoolView pool)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pool);

        if (state.Status != SeasonStatus.Active)
        {
            return [];
        }

        // The same test as the wheel: some weighted category has a game this player could get.
        return [.. state.Players.Values
            .Where(p =>
            {
                var status = SeasonGameStatus.For(state, p.PlayerId);
                return !pool.Categories.Any(c => c.Weight > 0
                    && pool.Games.Any(g => Rolling.InCategory(g, c) && status.Of(g) == GameAvailability.Available));
            })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.PlayerId)];
    }
}
