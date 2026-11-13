using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Rolls;

/// <summary>
/// Special rolls and roll changes (SPEC «Спецролл», D-405). A roll from Idle fires the player's first special roll (in the
/// order they came) and every <c>beforeRoll</c> effect, then fixes all waiting changes for this roll and its rerolls:
/// the special roll's first, then the others in the order they came; events' changes and special rolls before items, by
/// <c>events.applyOrder</c> (D-505, D-543). Changes that arrive later wait for the next roll —
/// effects of others never touch a roll already made. A player who finished not first rolls without the changes others
/// put on his game before the finish (D-425).
/// </summary>
internal static class RollPreparation
{
    public static (IReadOnlyList<IGameEvent> Events, SeasonState State) BeforeRoll(SeasonState state, Guid playerId, EngineContext context)
    {
        if (!state.Rules.Features.Items || Targets.IsFirst(state, playerId))
        {
            return ([], state);
        }

        var waiting = state.Players[playerId].Wallet.NextRoll;
        var events = new List<IGameEvent>();

        // The special rolls events gave by events.applyOrder first, then the others as they came (D-543)
        foreach (var special in EventOrder.Sort(state.Rules, Firing.Subscribed(state, playerId, Trigger.BeforeRoll, ObjectKind.SpecialRoll), o => o.EventKind))
        {
            if (Firing.Fire(state, context, playerId, special, Trigger.BeforeRoll) is { } fired)
            {
                events.AddRange(fired.Events);
                state = fired.State;
                break;
            }
        }

        var (effects, after) = FireEffects(state, playerId, context);
        events.AddRange(effects);
        state = after;

        var wallet = state.Players[playerId].Wallet;

        // Events' changes by events.applyOrder, then items'; within a kind the special roll's first, then as they came (D-505).
        // Events' changes of a roll left without a game (an admin's reset, «Уже проходил» with nothing left) never applied to
        // one: they wait on (D-583); items' are spent as in stage 4.
        IEnumerable<RollModifier> unused = state.Rules.Features.Events ? wallet.CurrentRoll.Where(m => m.EventKind is not null) : [];
        EquatableArray<RollModifier> pending = [.. EventOrder.Sort(state.Rules, [.. wallet.NextRoll.Skip(waiting.Count), .. unused, .. waiting], m => m.EventKind)];

        // Others' changes of the game put on him before he finished not first are taken off the roll and recorded by
        // what the roll applies (D-425); dice changes wait for his throw as before.
        EquatableArray<RollModifier> modifiers = Targets.IsLaterFinisher(state, playerId) ? [.. pending.Where(m => !m.Hostile)] : pending;
        if (pending.Count > 0 || wallet.CurrentRoll.Count > 0)
        {
            var (kept, lifted) = Fit(state, playerId, context, modifiers);
            var applied = new RollModifiersApplied(playerId, kept) { Waiting = lifted };
            events.Add(applied);
            state = SeasonEngine.Apply(state, applied);
        }

        return (events, state);
    }

    private static (IReadOnlyList<IGameEvent> Events, SeasonState State) FireEffects(SeasonState state, Guid playerId, EngineContext context)
    {
        var events = new List<IGameEvent>();
        foreach (var effect in Firing.Subscribed(state, playerId, Trigger.BeforeRoll, ObjectKind.Effect))
        {
            if (state.Players[playerId].Wallet.Find(effect.InstanceId) is { } held
                && Firing.Fire(state, context, playerId, held, Trigger.BeforeRoll) is { } fired)
            {
                events.AddRange(fired.Events);
                state = fired.State;
            }
        }

        return (events, state);
    }

    /// <summary>
    /// The changes of a roll, in order, split into those it applies and the events' changes that wait (SPEC «Накопление»,
    /// D-505, D-583): an event's filter that leaves no available game with the event filters kept before it does not apply
    /// to this roll and waits, whole, for the next one — the more important filter wins, and an event that fits no game waits
    /// for one that does (EV7). Item filters stay as in stage 4 (D-92). With events off nothing waits.
    /// <paramref name="givenUp"/> — the games a reroll gives up.
    /// </summary>
    public static (EquatableArray<RollModifier> Kept, EquatableArray<RollModifier> Lifted) Fit(
        SeasonState state, Guid playerId, EngineContext context, IReadOnlyList<RollModifier> ordered, IReadOnlySet<Guid>? givenUp = null)
    {
        var (kept, lifted) = Fit(state, playerId, context, ordered, m => m, givenUp);
        return ([.. kept], [.. lifted]);
    }

    private static (List<T> Kept, List<T> Lifted) Fit<T>(
        SeasonState state, Guid playerId, EngineContext context, IReadOnlyList<T> ordered, Func<T, RollModifier> modifierOf, IReadOnlySet<Guid>? givenUp)
    {
        if (!state.Rules.Features.Events || !ordered.Any(x => modifierOf(x) is { EventKind: not null, Filter: not null }))
        {
            return ([.. ordered], []);
        }

        var kept = new List<T>();
        var lifted = new List<T>();
        var games = Rolling.Fitting(state, playerId, context.Pool, givenUp);
        foreach (var item in ordered)
        {
            if (modifierOf(item) is { EventKind: not null, Filter: { } filter })
            {
                var narrowed = games.Where(g => Rolling.Matches(filter, g)).ToList();
                if (narrowed.Count == 0)
                {
                    lifted.Add(item);
                    continue;
                }

                games = narrowed;
            }

            kept.Add(item);
        }

        return (kept, lifted);
    }

    /// <summary>
    /// The roll in progress after a reroll or «Уже проходил» gave up games (D-583): its events' filters are fitted again
    /// against what is left, with the changes <paramref name="added"/> since (a paid reroll's bad event, D-546 (3)); a filter
    /// that fits nothing now waits for the next roll — first those of the roll in progress, then <paramref name="waiting"/>,
    /// then the added ones, as they came. Null when nothing changes (always with events off: the log stays as before).
    /// </summary>
    public static RollModifiersApplied? Refit(
        SeasonState state, Guid playerId, EngineContext context, IReadOnlySet<Guid> givenUp, IReadOnlyList<RollModifier> waiting, IReadOnlyList<RollModifier> added)
    {
        var current = state.Players[playerId].Wallet.CurrentRoll;
        var ordered = EventOrder.Sort(
                state.Rules,
                [.. current.Select(m => (Modifier: m, Added: false)), .. added.Select(m => (Modifier: m, Added: true))],
                x => x.Modifier.EventKind)
            .ToList();
        var (kept, lifted) = Fit(state, playerId, context, ordered, x => x.Modifier, givenUp);
        if (added.Count == 0 && lifted.Count == 0)
        {
            return null;
        }

        return new RollModifiersApplied(playerId, [.. kept.Select(x => x.Modifier)])
        {
            Waiting = [.. lifted.Where(x => !x.Added).Select(x => x.Modifier), .. waiting, .. lifted.Where(x => x.Added).Select(x => x.Modifier)],
        };
    }

    /// <summary>
    /// The filters of the roll in progress, in their order (priority «effect», D-92); their tags are an imposed genre,
    /// which refuses the wish reroll (D-325, D-422).
    /// </summary>
    public static IEnumerable<RollFilter> Filters(SeasonPlayer player) =>
        player.Wallet.CurrentRoll
            .Where(m => m.Filter is not null)
            .Select(FilterOf);

    private static RollFilter FilterOf(RollModifier m) =>
        new(RollFilterPriority.Effect, $"effect:{m.ObjectId}", game => Rolling.Matches(m.Filter!, game), m.Filter!.Tags ?? default);

    /// <summary>How many games the roll shows: the rules' count, or more when an item asks for a choice (CONTENT.md «Выбор из трёх»).</summary>
    public static int ChoiceCount(SeasonState state, Guid playerId) =>
        state.Rules.Features.Items
            ? Math.Max(state.Rules.Roll.ChoiceCount, state.Players[playerId].Wallet.CurrentRoll.Max(m => m.ChoiceCount) ?? 0)
            : state.Rules.Roll.ChoiceCount;
}
