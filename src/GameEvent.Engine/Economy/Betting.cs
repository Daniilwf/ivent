using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Economy;

/// <summary>
/// Bets (SPEC «Ставки», D-414): on another player completing their current run by a deadline of 1, 3 or 7 days, placed
/// within <c>bets.windowHoursAfterRoll</c> of the roll. The stake goes into the system's pledge; the win is
/// ⌊stake × multiplier⌋, the multiplier by the hours per day the game needs; lost stakes burn; a reject takes the win back.
/// </summary>
internal static class Betting
{
    public static Decision Decide(SeasonState state, PlaceBet command, EngineContext context)
    {
        if (ItemUse.Guard(state, command.PlayerId, context, f => f.Bets) is { } refused)
        {
            return refused;
        }

        var rules = state.Rules.Bets;
        var player = state.Players[command.PlayerId];
        if (command.OnPlayerId == command.PlayerId)
        {
            return Decision.Reject(RejectionCodes.BetOnSelf, "A bet is never on oneself.");
        }

        if (!state.Players.TryGetValue(command.OnPlayerId, out var on))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.OnPlayerId} is not in the season.");
        }

        if (Targets.IsFirst(state, on.PlayerId))
        {
            return Decision.Reject(RejectionCodes.BetOnFirst, "The first finisher plays out of the race: no bets on them.");
        }

        if (on.ActiveRunId is not { } runId)
        {
            return Decision.Reject(RejectionCodes.BetNoRun, "The player is not playing a game right now.");
        }

        var run = state.Runs[runId];
        var now = context.Clock.UtcNow;
        if (now - run.RolledAt > TimeSpan.FromHours(rules.WindowHoursAfterRoll))
        {
            return Decision.Reject(RejectionCodes.BetWindowClosed, $"Bets close {rules.WindowHoursAfterRoll} h after the roll.");
        }

        if (!rules.DeadlineOptionsDays.Contains(command.Days))
        {
            return Decision.Reject(RejectionCodes.BetInvalidDays, $"The deadline is one of {string.Join(", ", rules.DeadlineOptionsDays)} days.");
        }

        if (command.Stake < 1 || command.Stake > rules.MaxStake)
        {
            return Decision.Reject(RejectionCodes.BetInvalidStake, $"A stake is 1–{rules.MaxStake} coins.");
        }

        var open = player.Wallet.Bets.Where(b => b.Status == BetStatus.Open).ToList();
        if (open.Any(b => b.RunId == runId))
        {
            return Decision.Reject(RejectionCodes.BetAlreadyPlaced, "One bet per run.");
        }

        if (open.Count >= rules.MaxOpenBetsPerPlayer)
        {
            return Decision.Reject(RejectionCodes.BetTooMany, $"At most {rules.MaxOpenBetsPerPlayer} open bets.");
        }

        if (Shop.Unaffordable(player, command.Stake) is { } poor)
        {
            return poor;
        }

        var bet = new Bet(context.Ids.NewId(), on.PlayerId, runId, now, now.AddDays(command.Days), command.Stake, Multiplier(rules, run.Snapshot.Hours, command.Days));
        return Decision.Accept(
            new BetPlaced(player.PlayerId, bet),
            new CoinsChanged(player.PlayerId, -command.Stake, CoinsReason.BetStake, RunId: null));
    }

    /// <summary>
    /// The multiplier for a game of <paramref name="hours"/> within <paramref name="days"/>: the first step whose bound holds
    /// the hours per day; a game without hours takes the first step (D-414).
    /// </summary>
    public static decimal Multiplier(Rulesets.BetRules rules, decimal? hours, int days)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var perDay = (hours ?? 0) / days;
        return rules.PayoutByHoursPerDay.First(s => s.UpTo is not { } upTo || perDay <= upTo).Multiplier;
    }

    /// <summary>The bets settled by <paramref name="e"/> (the state already holds it), with their coins.</summary>
    public static IEnumerable<IGameEvent> Settle(SeasonState state, IGameEvent e)
    {
        switch (e)
        {
            case RunCompleted completed:
                foreach (var (bettor, bet) in OpenOn(state, completed.RunId))
                {
                    if (completed.CompletedAt > bet.Deadline)
                    {
                        yield return new BetSettled(bettor.PlayerId, bet.BetId, BetStatus.Lost, 0);
                        continue;
                    }

                    // The frozen first gets no coins (the freeze amendment); the bet still counts as won.
                    var payout = Finishes.IsFrozen(bettor) ? 0 : (int)Math.Floor(bet.Stake * bet.Multiplier);
                    yield return new BetSettled(bettor.PlayerId, bet.BetId, BetStatus.Won, payout);
                    if (payout != 0)
                    {
                        yield return new CoinsChanged(bettor.PlayerId, payout, CoinsReason.BetPayout, RunId: null);
                    }
                }

                break;
            case RunDropped dropped:
                foreach (var lost in Lose(state, dropped.RunId))
                {
                    yield return lost;
                }

                break;
            case RunTechRerolled rerolled:
                foreach (var lost in Lose(state, rerolled.RunId))
                {
                    yield return lost;
                }

                break;
            case Proofs.ProofRejected rejected:
                foreach (var bettor in state.Players.Values)
                {
                    foreach (var bet in bettor.Wallet.Bets.Where(b => b.RunId == rejected.RunId && b.Status == BetStatus.Won))
                    {
                        yield return new BetSettled(bettor.PlayerId, bet.BetId, BetStatus.Revoked, bet.Payout);
                        if (bet.Payout != 0)
                        {
                            yield return new CoinsChanged(bettor.PlayerId, -bet.Payout, CoinsReason.BetPayoutRevoked, RunId: null);
                        }
                    }
                }

                break;
            case SeasonStatusChanged { To: SeasonStatus.Closing }:
                // After the deadline no run is completed any more.
                foreach (var bettor in state.Players.Values)
                {
                    foreach (var bet in bettor.Wallet.Bets.Where(b => b.Status == BetStatus.Open))
                    {
                        yield return new BetSettled(bettor.PlayerId, bet.BetId, BetStatus.Lost, 0);
                    }
                }

                break;
            default:
                break;
        }
    }

    private static IEnumerable<IGameEvent> Lose(SeasonState state, Guid runId) =>
        OpenOn(state, runId).Select(x => (IGameEvent)new BetSettled(x.Bettor.PlayerId, x.Bet.BetId, BetStatus.Lost, 0));

    private static IEnumerable<(SeasonPlayer Bettor, Bet Bet)> OpenOn(SeasonState state, Guid runId) =>
        state.Players.Values.SelectMany(p => p.Wallet.Bets.Where(b => b.RunId == runId && b.Status == BetStatus.Open).Select(b => (p, b)));

    public static SeasonState Apply(SeasonState state, BetPlaced e) =>
        Inventories.Update(state, e.PlayerId, w => w with { Bets = [.. w.Bets, e.Bet] });

    public static SeasonState Apply(SeasonState state, BetSettled e) =>
        Inventories.Update(state, e.PlayerId, w => w with
        {
            Bets = [.. w.Bets.Select(b => b.BetId == e.BetId ? b with { Status = e.Status, Payout = e.Payout } : b)],
        });
}
