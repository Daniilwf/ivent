using GameEvent.Engine.Pool;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Map;

/// <summary>A zone whose roll filter leaves fewer available games than <c>map.minZoneGames</c>.</summary>
public sealed record ZoneGamesWarning(string ZoneId, int Available, int Wanted);

/// <summary>
/// The editor's warning (SPEC «Редактор»: у зоны в пуле не меньше 15 подходящих игр, иначе предупреждение; D-307): for
/// each zone with a roll filter, the games of the pool it leaves that are available in the season now (not deleted, not
/// completed, not busy). It never blocks a publication: an empty zone falls back to the ordinary roll.
/// </summary>
public static class ZoneWarnings
{
    public static IReadOnlyList<ZoneGamesWarning> For(MapGraph map, Ruleset rules, SeasonState state, IPoolView pool)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pool);
        if (rules.Map.MinZoneGames is not { } wanted)
        {
            return [];
        }

        var status = SeasonGameStatus.ForNobody(state);
        var available = pool.Games.Where(g => status.Of(g) == GameAvailability.Available).ToList();
        return [.. map.Zones
            .Where(z => z.RollFilter is not null)
            .Select(z => new ZoneGamesWarning(z.Id, available.Count(g => Rolling.Matches(z.RollFilter!, g)), wanted))
            .Where(w => w.Available < wanted)
            .OrderBy(w => w.ZoneId, StringComparer.Ordinal)];
    }
}
