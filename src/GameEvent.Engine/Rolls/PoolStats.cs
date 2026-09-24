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
    public static IReadOnlyList<CategoryStat> Categories(SeasonState state, IPoolView pool) =>
        throw new NotImplementedException("C5");

    /// <summary>
    /// Players of the season whose next roll would find no game (their exclusions included), ordered by name:
    /// the signal for the admin. Empty when the season is not running.
    /// </summary>
    public static IReadOnlyList<Guid> PlayersWithoutGames(SeasonState state, IPoolView pool) =>
        throw new NotImplementedException("C5");
}
