using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Decks;

/// <summary>
/// The paid reroll for a bad event (<c>roll.rerollCost.kind = badEvent</c>, SPEC «Реролл»; D-504): the bad event is the
/// price, so it is drawn before the new roll, and what it does to a roll goes to that roll, as an item used «перед роллом»
/// (D-546 (3), an exception to D-405): the special rolls it gave fire now, and its roll changes join the ones of the roll in
/// progress while older ones keep waiting. Nothing else to roll is told first, before anything is paid (D-93). With
/// <c>features.events</c> off the price is a manual effect, as before (D-10).
/// </summary>
internal static class PaidRerollEvents
{
    /// <summary>
    /// The paid reroll on <paramref name="state"/>, which already holds <paramref name="head"/>: the reroll and the refit of
    /// the roll in progress, if any (D-583).
    /// </summary>
    public static Decision Decide(SeasonState state, IReadOnlyList<IGameEvent> head, IReadOnlySet<Guid> givenUp, EngineContext context)
    {
        var playerId = ((GameRerolled)head[0]).PlayerId;
        if (!state.Rules.Features.Events)
        {
            // As before stage 5, the roll first, then the manual price (D-10): the same ids in the same order
            return Rolling.Draw(state, playerId, context, Rolling.Filters(state, playerId), givenUp) is { } plain
                ? Decision.Accept([.. head, .. EventDraws.Draw(state, context, playerId, EventDraws.NameOf(EventKind.Bad), EventSource.RerollCost), plain])
                : Decision.Reject(RejectionCodes.NoAvailableGames, "No game is left besides the ones given up.");
        }

        if (!Rolling.CanRoll(state, playerId, context.Pool, Rolling.Filters(state, playerId), givenUp))
        {
            return Decision.Reject(RejectionCodes.NoAvailableGames, "No game is left besides the ones given up.");
        }

        var waiting = state.Players[playerId].Wallet.NextRoll.Count;
        var drawn = EventDraws.Draw(state, context, playerId, EventDraws.NameOf(EventKind.Bad), EventSource.RerollCost);
        var after = drawn.Aggregate(state, SeasonEngine.Apply);
        var (prepared, ready) = IntoThisRoll(after, playerId, drawn, waiting, givenUp, context);

        // The event's filter may leave nothing to roll; the filters give way as at any roll, so this is a safeguard
        return Rolling.Draw(ready, playerId, context, Rolling.Filters(ready, playerId), givenUp) is { } roll
            ? Decision.Accept([.. head, .. drawn, .. prepared, roll])
            : Decision.Reject(RejectionCodes.NoAvailableGames, "No game is left besides the ones given up.");
    }

    // The special rolls the event gave fire at once; the roll changes made since the draw join the roll in progress
    private static (IReadOnlyList<IGameEvent> Events, SeasonState State) IntoThisRoll(
        SeasonState state, Guid playerId, IReadOnlyList<IGameEvent> drawn, int waiting, IReadOnlySet<Guid> givenUp, EngineContext context)
    {
        var events = new List<IGameEvent>();
        var given = drawn.OfType<ObjectGiven>().Where(g => g.PlayerId == playerId).Select(g => g.Item.InstanceId).ToHashSet();
        foreach (var special in Firing.Subscribed(state, playerId, Trigger.BeforeRoll, ObjectKind.SpecialRoll).Where(o => given.Contains(o.InstanceId)))
        {
            if (state.Players[playerId].Wallet.Find(special.InstanceId) is { } held
                && Firing.Fire(state, context, playerId, held, Trigger.BeforeRoll) is { } fired)
            {
                events.AddRange(fired.Events);
                state = fired.State;
            }
        }

        var wallet = state.Players[playerId].Wallet;
        if (wallet.NextRoll.Count > waiting)
        {
            // In the order of kinds; a contradicting condition and a filter that fits no game left wait for the next roll as at
            // any roll (D-506, D-583, D-586)
            var applied = RollPreparation.Refit(state, playerId, context, givenUp, [.. wallet.NextRoll.Take(waiting)], [.. wallet.NextRoll.Skip(waiting)])!;
            events.Add(applied);
            state = SeasonEngine.Apply(state, applied);
        }

        return (events, state);
    }
}
