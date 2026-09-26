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
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pool);

        var status = SeasonGameStatus.ForNobody(state);
        var available = pool.Games.Where(g => status.Of(g) == GameAvailability.Available).ToList();
        return [.. pool.Categories
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new CategoryStat(c.Name, c.Weight, available.Count(g => Rolling.InCategory(g, c))))];
    }

    /// <summary>
    /// The games nobody can roll now for a season reason, ordered by game id (H6: the pool page shows «Уже прошёл Вася» and
    /// «Сейчас играет Вася» by the same predicate the wheel uses). An offered game and every option of a pending choice
    /// count as being played; a free-mode run of the first does not complete the game (D-16). Deleted games and personal
    /// exclusions are not the season's status and are not listed.
    /// </summary>
    public static IReadOnlyList<RollMiss> Taken(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return [.. SeasonGameStatus.ForNobody(state).Taken.OrderBy(m => m.GameId)];
    }

    /// <summary>
    /// Idle, active players of a running season whose roll would find no game now (their exclusions and the filters
    /// included), ordered by name: the signal for the admin (D-92). A player playing or holding an offer has a game.
    /// </summary>
    public static IReadOnlyList<Guid> PlayersWithoutGames(SeasonState state, IPoolView pool)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pool);

        if (state.Status != SeasonStatus.Active)
        {
            return [];
        }

        var filters = Rolling.Filters(state);
        return [.. state.Players.Values
            .Where(p => p is { Phase: TurnPhase.Idle, IsInactive: false } && !Rolling.CanRoll(state, p.PlayerId, pool, filters))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.PlayerId)];
    }
}
