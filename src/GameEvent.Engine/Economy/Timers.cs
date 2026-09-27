using GameEvent.Engine.Content;
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
    /// <summary>The earliest moment something of <paramref name="player"/> is due; null when nothing waits.</summary>
    public static DateTimeOffset? Next(SeasonPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var wallet = player.Wallet;
        IEnumerable<DateTimeOffset?> moments =
        [
            wallet.Shop?.ExpiresAt,
            .. wallet.Inventory.Select(o => o.ExpiresAt),
            .. wallet.Bets.Where(b => b.Status == BetStatus.Open).Select(b => (DateTimeOffset?)b.Deadline),
        ];
        return moments.Where(m => m is not null).Min();
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

        var now = context.Clock.UtcNow;
        var events = new List<IGameEvent>();
        foreach (var player in state.Players.Values)
        {
            var wallet = player.Wallet;
            if (wallet.Shop is { } offer && offer.ExpiresAt <= now)
            {
                events.Add(new ShopOfferExpired(player.PlayerId));
            }

            events.AddRange(wallet.Inventory
                .Where(o => o.ExpiresAt <= now)
                .Select(o => new ObjectRemoved(player.PlayerId, o.InstanceId, o.ObjectId, ObjectRemoval.Expired)));
            events.AddRange(wallet.Bets
                .Where(b => b.Status == BetStatus.Open && b.Deadline <= now)
                .Select(b => new BetSettled(player.PlayerId, b.BetId, BetStatus.Lost, 0)));
        }

        return events.Count == 0
            ? Decision.Reject(RejectionCodes.TimersNothingDue, "Nothing is due yet.")
            : Decision.Accept(events);
    }
}

/// <summary>
/// What follows a command's own events without being an effect (D-413): bets are decided, the shop price starts over,
/// objects living for some runs count one down. Not limited by the effect chain: a settlement must never be cut.
/// Writes nothing while the season has none of it (a mechanic off writes no events).
/// </summary>
internal static class Settlements
{
    public static IReadOnlyList<IGameEvent> After(SeasonState state, IReadOnlyList<IGameEvent> events)
    {
        var settled = new List<IGameEvent>();
        foreach (var e in events)
        {
            foreach (var next in Betting.Settle(state, e).Concat(RunEnded(state, e)))
            {
                settled.Add(next);
                state = SeasonEngine.Apply(state, next);
            }
        }

        return settled;
    }

    // A run of the player ended: the shop price starts over (economy.shop.resetOn), objects of «runs» count one down.
    private static IEnumerable<IGameEvent> RunEnded(SeasonState state, IGameEvent e)
    {
        var (playerId, reset) = e switch
        {
            Runs.RunCompleted c => (c.PlayerId, (Rulesets.ShopPriceReset?)Rulesets.ShopPriceReset.RunCompleted),
            Runs.RunDropped d => (d.PlayerId, Rulesets.ShopPriceReset.RunDropped),
            _ => (Guid.Empty, null),
        };
        if (reset is null)
        {
            yield break;
        }

        var wallet = state.Players[playerId].Wallet;
        if (wallet.ShopRolls > 0 && state.Rules.Economy.Shop.ResetOn.Contains(reset.Value))
        {
            yield return new ShopPriceRestarted(playerId);
        }

        foreach (var item in wallet.Inventory.Where(o => o.RunsLeft is not null && o.Kind != ObjectKind.Item))
        {
            yield return item.RunsLeft <= 1
                ? new ObjectRemoved(playerId, item.InstanceId, item.ObjectId, ObjectRemoval.Expired)
                : new ObjectChanged(playerId, item with { RunsLeft = item.RunsLeft - 1 });
        }
    }
}
