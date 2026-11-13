using GameEvent.Engine.Content;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Economy;

/// <summary>
/// Timers of the economy (SPEC «Таймеры и перезапуск сервера», D-404): the moments are in the state — the shop offer's end,
/// an effect's hours, a bet's deadline — and so in the log and the projection; the scheduler sends <see cref="FireTimers"/>
/// through the one queue when the earliest has come, and the engine decides by its own clock.
/// </summary>
public static class Timers
{
    /// <summary>
    /// The earliest moment something of <paramref name="player"/> is due; null when nothing waits. What a mechanic switched
    /// off left waiting is not due until it is back (D-420).
    /// </summary>
    public static DateTimeOffset? Next(SeasonPlayer player, Rulesets.Features features)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(features);
        var wallet = player.Wallet;
        IEnumerable<DateTimeOffset?> moments =
        [
            features.Shop ? wallet.Shop?.ExpiresAt : null,
            .. features.Items && !Finishes.IsFrozen(player) ? wallet.Inventory.Select(o => o.ExpiresAt) : [],
            .. features.Bets ? wallet.Bets.Where(b => b.Status == BetStatus.Open).Select(b => (DateTimeOffset?)b.Deadline) : [],
            Effects.Suspensions.Due(player, features),
        ];
        return moments.Where(m => m is not null).Min();
    }

    /// <summary>
    /// The earliest moment something of <paramref name="player"/> in <paramref name="state"/> is due: his own timers and the
    /// pending invitations he sent (D-523); null when nothing waits.
    /// </summary>
    public static DateTimeOffset? Next(SeasonState state, SeasonPlayer player)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(player);
        var invitations = state.Interactions.Values
            .Where(i => i.Status == Interactions.InteractionStatus.Pending && i.FromPlayerId == player.PlayerId
                && Interactions.InteractionRules.IsOn(state.Rules.Features, i.Kind))
            .Select(i => (DateTimeOffset?)i.ExpiresAt)
            .Min();
        return new[] { Next(player, state.Rules.Features), invitations }.Where(m => m is not null).Min();
    }

    internal static Decision Decide(SeasonState state, EngineContext context)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (state.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: no timers run.");
        }

        // A mechanic switched off leaves no event: what it left waiting stays until it is back (D-420)
        var now = context.Clock.UtcNow;
        var features = state.Rules.Features;
        var events = new List<IGameEvent>();
        foreach (var player in state.Players.Values)
        {
            var wallet = player.Wallet;
            if (features.Shop && wallet.Shop is { } offer && offer.ExpiresAt <= now)
            {
                events.Add(new ShopOfferExpired(player.PlayerId));
            }

            // The frozen first's inventory is frozen too (D-407, D-422)
            events.AddRange(wallet.Inventory
                .Where(o => features.Items && o.ExpiresAt <= now && !Finishes.IsFrozen(player))
                .Select(o => new ObjectRemoved(player.PlayerId, o.InstanceId, o.ObjectId, ObjectRemoval.Expired)));
            events.AddRange(wallet.Bets
                .Where(b => features.Bets && b.Status == BetStatus.Open && b.Deadline <= now)
                .Select(b => new BetSettled(player.PlayerId, b.BetId, BetStatus.Lost, 0)));
        }

        // An event's question past its answer timeout is answered by the timer (D-560)
        events.AddRange(Effects.Suspensions.Timeouts(events.Aggregate(state, SeasonEngine.Apply), context));

        // An invitation past its time burns (D-523)
        events.AddRange(Interactions.Coop.Timeouts(state, now));

        return events.Count == 0
            ? Decision.Reject(RejectionCodes.TimersNothingDue, "Nothing is due yet.")
            : Decision.Accept(events);
    }
}

/// <summary>
/// What follows a command's own events without being an effect (D-413): bets are decided, weekly challenges are completed
/// or taken back (D-526), the shop price starts over,
/// objects living for some runs count one down. Not limited by the effect chain: a settlement must never be cut.
/// Writes nothing while the season has none of it, nor for a mechanic switched off (D-420).
/// </summary>
internal static class Settlements
{
    public static IReadOnlyList<IGameEvent> After(SeasonState state, IReadOnlyList<IGameEvent> events, EngineContext context) =>
        Each(state, events, (s, e) => (s.Rules.Features.Bets ? Betting.Settle(s, e) : [])
            .Concat(s.Rules.Features.WeeklyChallenge ? Challenges.WeeklyChallenges.Settle(s, context.Pool, e) : [])
            .Concat(s.Rules.Features.Coop ? Interactions.Coop.Settle(s, e, context.Clock.UtcNow) : [])
            .Concat(s.Rules.Features.Shop ? ShopReset(s, e) : []));

    /// <summary>
    /// After the effects fired: an object living for some runs counts one down when a run of its owner ends, so it still
    /// fires on its last run (SPEC «на N игр»).
    /// </summary>
    public static IReadOnlyList<IGameEvent> Lifetimes(SeasonState state, IReadOnlyList<IGameEvent> events) =>
        state.Rules.Features.Items ? Each(state, events, RunsLived) : [];

    private static List<IGameEvent> Each(SeasonState state, IReadOnlyList<IGameEvent> events, Func<SeasonState, IGameEvent, IEnumerable<IGameEvent>> follow)
    {
        var settled = new List<IGameEvent>();
        foreach (var e in events)
        {
            foreach (var next in follow(state, e))
            {
                settled.Add(next);
                state = SeasonEngine.Apply(state, next);
            }
        }

        return settled;
    }

    private static (Guid PlayerId, Rulesets.ShopPriceReset Reset)? Ended(IGameEvent e) =>
        e switch
        {
            Runs.RunCompleted c => (c.PlayerId, Rulesets.ShopPriceReset.RunCompleted),
            Runs.RunDropped d => (d.PlayerId, Rulesets.ShopPriceReset.RunDropped),
            _ => null,
        };

    // A run of the player ended: the shop price starts over (economy.shop.resetOn).
    private static IEnumerable<IGameEvent> ShopReset(SeasonState state, IGameEvent e)
    {
        if (Ended(e) is { } ended && state.Players[ended.PlayerId].Wallet.ShopRolls > 0 && state.Rules.Economy.Shop.ResetOn.Contains(ended.Reset))
        {
            yield return new ShopPriceRestarted(ended.PlayerId);
        }
    }

    private static IEnumerable<IGameEvent> RunsLived(SeasonState state, IGameEvent e)
    {
        if (Ended(e) is not { } ended || Finishes.IsFrozen(state.Players[ended.PlayerId]))
        {
            yield break;
        }

        foreach (var item in state.Players[ended.PlayerId].Wallet.Inventory.Where(o => o.RunsLeft is not null && o.Kind != ObjectKind.Item))
        {
            yield return item.RunsLeft <= 1
                ? new ObjectRemoved(ended.PlayerId, item.InstanceId, item.ObjectId, ObjectRemoval.Expired)
                : new ObjectChanged(ended.PlayerId, item with { RunsLeft = item.RunsLeft - 1 });
        }
    }
}
