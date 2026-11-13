using GameEvent.Engine.Interactions;

namespace GameEvent.Engine.Runs;

/// <summary>
/// What a completed run gives each of its players (D-521): a run played alone — all of it to its player; a coop run split by
/// the shares written at its completion, the roller ⌈total × share⌉, the partner the rest.
/// </summary>
internal static class RunShares
{
    /// <summary>The players a completed run gave something to: its player, and the partner of a split coop run.</summary>
    public static IReadOnlyList<Guid> Players(RunState run) =>
        run.Coop is { Shares: not null } coop ? [run.PlayerId, coop.PlayerId] : [run.PlayerId];

    /// <summary><paramref name="playerId"/>'s points and steps of the run's total (after every correction).</summary>
    public static int Points(RunState run, Guid playerId) => Of(run, playerId, CompletionRoll.Total(run), s => s.PointsShare);

    /// <summary><paramref name="playerId"/>'s part of the run's <paramref name="coins"/>.</summary>
    public static int Coins(RunState run, Guid playerId, int coins) => Of(run, playerId, coins, s => s.CoinsShare);

    private static int Of(RunState run, Guid playerId, int total, Func<CoopShareRules, decimal> share)
    {
        if (run.Coop is not { Shares: { } shares } coop)
        {
            return playerId == run.PlayerId ? total : 0;
        }

        var (roller, partner) = CoopShares.Split(total, share(shares));
        // The partner first: his view of the run (RunState.Side) carries his id as the run's player
        return playerId == coop.PlayerId ? partner : playerId == run.PlayerId ? roller : 0;
    }
}
