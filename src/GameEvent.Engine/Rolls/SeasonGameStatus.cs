using GameEvent.Engine.Pool;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Rolls;

/// <summary>How a game looks to a player who is rolling.</summary>
internal enum GameAvailability
{
    /// <summary>Can be offered.</summary>
    Available,

    /// <summary>Unavailable for a season reason; landing on it is a logged miss.</summary>
    Miss,

    /// <summary>Never rolled for this player and not shown (deleted, own drop, «Уже проходил»).</summary>
    Hidden,
}

/// <summary>
/// Season status of games, computed from runs and offers (SPEC «Модель данных»: no separate table).
/// The wheel and the draw use this one predicate, so a category on the wheel always has a game to draw.
/// </summary>
internal sealed class SeasonGameStatus
{
    private readonly Dictionary<Guid, RollMiss> _misses = [];
    private readonly HashSet<Guid> _excluded = [];

    private SeasonGameStatus(SeasonState state, Guid playerId)
    {
        // The player's own exclusions are hidden from them, never shown as a miss (D-05).
        if (state.Players.TryGetValue(playerId, out var me))
        {
            _excluded.UnionWith(me.Exclusions.Select(x => x.GameId));
        }

        foreach (var run in state.Runs.Values)
        {
            switch (run.Status)
            {
                case RunStatus.Completed when run.FreeMode && run.PlayerId != playerId:
                    // The first's games in free mode do not complete the game for the others (D-16); for him they do.
                    break;
                case RunStatus.Completed:
                    // «Уже прошёл» wins over «Сейчас играет».
                    _misses[run.GameId] = new RollMiss(run.GameId, RollMissReason.CompletedInSeason, run.PlayerId);
                    break;
                case RunStatus.Playing:
                    _misses.TryAdd(run.GameId, new RollMiss(run.GameId, RollMissReason.BeingPlayed, run.PlayerId));
                    break;
                case RunStatus.Dropped:
                case RunStatus.TechRerolled:
                default:
                    break;
            }
        }

        foreach (var player in state.Players.Values)
        {
            if (player.Offer is { } offer)
            {
                _misses.TryAdd(offer.GameId, new RollMiss(offer.GameId, RollMissReason.BeingPlayed, player.PlayerId));
            }

            // Every option of a pending choice is reserved until the pick (D-06).
            foreach (var option in player.Choice?.Options ?? [])
            {
                if (option.Game is { } game)
                {
                    _misses.TryAdd(game.GameId, new RollMiss(game.GameId, RollMissReason.BeingPlayed, player.PlayerId));
                }
            }
        }
    }

    /// <summary>Game statuses as seen by <paramref name="playerId"/>; an unknown id sees no personal exclusions.</summary>
    public static SeasonGameStatus For(SeasonState state, Guid playerId) => new(state, playerId);

    /// <summary>Game statuses without anyone's personal exclusions (category counts for the admin).</summary>
    public static SeasonGameStatus ForNobody(SeasonState state) => new(state, Guid.Empty);

    /// <summary>Every game taken in the season: completed (winning over played) or played, offered or among pending options.</summary>
    public IEnumerable<RollMiss> Taken => _misses.Values;

    public GameAvailability Of(Game game, out RollMiss? miss)
    {
        miss = null;
        if (game.IsDeleted || _excluded.Contains(game.Id))
        {
            return GameAvailability.Hidden;
        }

        if (_misses.TryGetValue(game.Id, out var found))
        {
            miss = found;
            return GameAvailability.Miss;
        }

        return GameAvailability.Available;
    }

    public GameAvailability Of(Game game) => Of(game, out _);
}
