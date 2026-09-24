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

    private SeasonGameStatus(SeasonState state)
    {
        foreach (var run in state.Runs.Values)
        {
            switch (run.Status)
            {
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
        }
    }

    /// <summary>Game statuses as seen by <paramref name="playerId"/>. Personal exclusions arrive in tasks C5–C6.</summary>
    public static SeasonGameStatus For(SeasonState state, Guid playerId)
    {
        _ = playerId;
        return new SeasonGameStatus(state);
    }

    public GameAvailability Of(Game game, out RollMiss? miss)
    {
        miss = null;
        if (game.IsDeleted)
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
