using System.Text.Json.Serialization;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Effects;

/// <summary>What caused a settlement for everyone (D-537): the mechanic that queued it.</summary>
public enum ForAllSource
{
    /// <summary>The reward of a global event's goal, reached by someone's completion (D-517, D-518; task 5.9).</summary>
    GoalReward,

    /// <summary>The result of a poll whose effect targets everyone (D-512; task 5.8).</summary>
    PollResult,

    /// <summary>The effect of a global event at its launch (D-517; task 5.9).</summary>
    GlobalEventLaunch,
}

/// <summary>
/// A settlement for everyone was queued by the command that caused it (D-537): the effect <see cref="Effect"/> (a snapshot,
/// labelled <see cref="ObjectId"/>) of <see cref="Source"/> <see cref="Reference"/> (the poll, the global event) goes to
/// <see cref="Players"/> — the targets fixed now, in id order — in <see cref="SettleForAll"/> commands of its own.
/// <see cref="Parameters"/> are the references the effect reads (a poll's <c>$result</c>), written only when set.
/// </summary>
[EventType("for-all-queued")]
public sealed record ForAllQueued(Guid SettlementId, ForAllSource Source, Guid Reference, string ObjectId, EffectSpec Effect, EquatableArray<Guid> Players) : IGameEvent
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContentParamDictionary? Parameters { get; init; }
}

/// <summary>
/// One batch of a settlement for everyone (D-537), the first event of its <see cref="SettleForAll"/>: <see cref="Players"/>
/// got the effect in this command, in this order; <see cref="Skipped"/> were no longer reachable when their turn came (the
/// first finisher, a later finisher for an effect beyond points and coins, a run of the effect refused); <see cref="Done"/> —
/// nobody remains.
/// </summary>
[EventType("for-all-settled")]
public sealed record ForAllSettled(Guid SettlementId, int Batch, EquatableArray<Guid> Players, EquatableArray<Guid> Skipped, bool Done) : IGameEvent;

/// <summary>
/// The system settles the next batch of a pending settlement for everyone (D-537): sent by the queue right after the cause
/// and before any later command of the season, never by a player or the admin.
/// </summary>
public sealed record SettleForAll(Guid SettlementId) : ICommand;

/// <summary>
/// A settlement for everyone waiting for its batches (D-537). <see cref="Remaining"/> — the players not reached yet, in
/// order; <see cref="Batches"/> — batches written so far; <see cref="Order"/> — its place in the queue of the season;
/// <see cref="Halted"/> — an undo of one of its batches stopped it for good: it waits for the undo of its cause.
/// </summary>
public sealed record ForAllSettlement(
    Guid SettlementId,
    ForAllSource Source,
    Guid Reference,
    string ObjectId,
    EffectSpec Effect,
    ContentParamDictionary? Parameters,
    EquatableArray<Guid> Remaining,
    int Batches,
    int Order,
    bool Halted);

/// <summary>
/// Settlements for everyone (D-537, invariants 2, 3, 8): a reward of a goal, a poll's result on everyone or a global
/// event's launch effect is not run inside the command that caused it, where its events would add to the cause's and pass
/// the 50 of a command. The cause writes <see cref="ForAllQueued"/>; <see cref="SettleForAll"/> commands then settle it in
/// batches of whole players — a player's effect and its chain reactions go into one batch or wait for the next — each
/// within <c>effects.maxEventsPerCommand</c>. Undoing the cause waits for its batches (invariant 12); undoing a batch halts
/// the settlement (D-701).
/// </summary>
public static class ForAllSettlements
{
    /// <summary>
    /// The event that queues <paramref name="effect"/> for everyone, for a cause to write among its own events. The targets
    /// are fixed now (D-701): every player but the first finisher, a later finisher only for points and coins (D-518).
    /// </summary>
    public static ForAllQueued Queue(
        SeasonState state, EngineContext context, ForAllSource source, Guid reference, string objectId, EffectSpec effect, ContentParamDictionary? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(effect);
        return new ForAllQueued(
            context.Ids.NewId(), source, reference, objectId, effect, [.. state.Players.Keys.Where(id => Reachable(state, id, effect))])
        {
            Parameters = parameters,
        };
    }

    /// <summary>The oldest settlement waiting and not halted, while the season runs (active or closing); null when none.</summary>
    public static Guid? Next(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Status is SeasonStatus.Active or SeasonStatus.Closing
            ? state.ForAll.Values.Where(s => !s.Halted).OrderBy(s => s.Order).Select(s => (Guid?)s.SettlementId).FirstOrDefault()
            : null;
    }

    // The first finisher is out of the game (SPEC); a later finisher is reached by points and coins only (SPEC «Цели», D-518)
    private static bool Reachable(SeasonState state, Guid playerId, EffectSpec effect) =>
        state.Players.TryGetValue(playerId, out var player)
        && !Targets.IsFirst(state, playerId)
        && (player.Finish is null || Targets.PointsAndCoinsOnly(effect));

    internal static Decision Decide(SeasonState state, SettleForAll command, EngineContext context)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (state.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: nothing is settled any more.");
        }

        if (!state.ForAll.TryGetValue(command.SettlementId, out var settlement))
        {
            return Decision.Reject(RejectionCodes.ForAllUnknown, $"No settlement {command.SettlementId} waits.");
        }

        if (settlement.Halted)
        {
            return Decision.Reject(RejectionCodes.ForAllHalted, $"Settlement {command.SettlementId} was stopped by an undo of its batch: undo its cause.");
        }

        // The batch fills with whole players while the command stays within its events (invariant 8); a player who alone
        // does not fit goes alone, his chain cut as usual
        var limit = ChainLimits.Of(state).Events;
        var locked = state.Status != SeasonStatus.Active || SeasonSetup.IsPastDeadline(state, context.Clock.UtcNow);
        var current = state;
        var events = new List<IGameEvent>();
        var players = new List<Guid>();
        var skipped = new List<Guid>();
        var count = 1;
        foreach (var playerId in settlement.Remaining)
        {
            if (!Reachable(current, playerId, settlement.Effect))
            {
                skipped.Add(playerId);
                continue;
            }

            var run = new EffectRun(current, context, playerId, settlement.ObjectId, hostile: false, settlement.Parameters, eventsBefore: count)
            {
                PositionLocked = locked,
            };
            run.Run(settlement.Effect, [playerId]);
            if (run.Refusal is not null)
            {
                skipped.Add(playerId);
                continue;
            }

            var (reactions, reacted) = EffectChain.Run(run.State, run.Events, context, eventsBefore: count);
            var size = run.Events.Count + reactions.Count;
            if (players.Count > 0 && count + size > limit)
            {
                break;
            }

            events.AddRange(run.Events);
            events.AddRange(reactions);
            players.Add(playerId);
            count += size;
            current = reacted;
        }

        var done = settlement.Remaining.Count == players.Count + skipped.Count;
        return Decision.Accept([new ForAllSettled(settlement.SettlementId, settlement.Batches + 1, [.. players], [.. skipped], done), .. events]);
    }

    internal static SeasonState Apply(SeasonState state, ForAllQueued e)
    {
        var order = state.ForAll.IsEmpty ? 1 : state.ForAll.Values.Max(s => s.Order) + 1;
        return state with
        {
            ForAll = state.ForAll.SetItem(
                e.SettlementId,
                new ForAllSettlement(e.SettlementId, e.Source, e.Reference, e.ObjectId, e.Effect, e.Parameters, e.Players, Batches: 0, order, Halted: false)),
        };
    }

    internal static SeasonState Apply(SeasonState state, ForAllSettled e)
    {
        if (!state.ForAll.TryGetValue(e.SettlementId, out var settlement))
        {
            return state;
        }

        if (e.Done)
        {
            return state with { ForAll = state.ForAll.Remove(e.SettlementId) };
        }

        var reached = e.Players.Concat(e.Skipped).ToHashSet();
        return state with
        {
            ForAll = state.ForAll.SetItem(
                e.SettlementId, settlement with { Remaining = [.. settlement.Remaining.Where(id => !reached.Contains(id))], Batches = e.Batch }),
        };
    }
}
