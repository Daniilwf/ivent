using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Economy;

/// <summary>A deadline a bet may take and the multiplier it gets for this run's game.</summary>
public sealed record BetOption(int Days, decimal Multiplier);

/// <summary>
/// A run the player may bet on now: another player's current game, rolled within <c>bets.windowHoursAfterRoll</c>
/// (<c>ClosesAt</c> — when the window ends), with the multiplier of each deadline (D-447).
/// </summary>
public sealed record BetRun(Guid PlayerId, Guid RunId, Guid GameId, decimal? Hours, DateTimeOffset RolledAt, DateTimeOffset ClosesAt, IReadOnlyList<BetOption> Options);

/// <summary>
/// What the bets screen offers (D-447): why no bet is placed now (the engine's code: the mechanic off, the deadline, the
/// frozen first, the limit of open bets, debt or no coins), the open bets against their limit, the largest stake the
/// player can place, and the runs a bet is accepted on.
/// </summary>
public sealed record BetPlan(string? Refusal, int OpenBets, int MaxOpenBets, int MaxStake, IReadOnlyList<BetRun> Runs);

public static class BetPlans
{
    /// <summary>The bets of <paramref name="playerId"/> at <paramref name="now"/>; null for a player who is not in the season.</summary>
    public static BetPlan? Of(SeasonState state, DateTimeOffset now, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Players.TryGetValue(playerId, out var player))
        {
            return null;
        }

        var rules = state.Rules.Bets;
        var open = player.Wallet.Bets.Where(b => b.Status == BetStatus.Open).ToList();
        var refusal = ItemUse.Guard(state, playerId, now, f => f.Bets)?.Rejection?.Code
            ?? (open.Count >= rules.MaxOpenBetsPerPlayer ? Kernel.RejectionCodes.BetTooMany : null)
            ?? Shop.Unaffordable(player, 1)?.Rejection?.Code;

        var window = TimeSpan.FromHours(rules.WindowHoursAfterRoll);
        var runs = refusal is not null
            ? []
            : state.Players.Values
                // A bet is on a run's roller, never on its coop partner or by him (D-520)
                .Where(p => p.PlayerId != playerId && !Targets.IsFirst(state, p.PlayerId) && p.ActiveRunId is { } active && state.Runs[active].PlayerId == p.PlayerId)
                .Select(p => state.Runs[p.ActiveRunId!.Value])
                .Where(run => run.PartnerId != playerId)
                .Where(run => now - run.RolledAt <= window && open.All(b => b.RunId != run.RunId))
                .OrderBy(run => run.RolledAt)
                .ThenBy(run => run.RunId)
                .Select(run => new BetRun(
                    run.PlayerId,
                    run.RunId,
                    run.GameId,
                    run.Snapshot.Hours,
                    run.RolledAt,
                    run.RolledAt + window,
                    [.. rules.DeadlineOptionsDays.Select(days => new BetOption(days, Betting.Multiplier(rules, run.Snapshot.Hours, days)))]))
                .ToList();

        return new BetPlan(refusal, open.Count, rules.MaxOpenBetsPerPlayer, Math.Max(0, Math.Min(rules.MaxStake, player.Coins)), runs);
    }
}
