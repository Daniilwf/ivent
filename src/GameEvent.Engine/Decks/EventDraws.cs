using System.Text.Json.Serialization;
using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Decks;

/// <summary>
/// Where a drawn event came from (GLOSSARY «Источник ивента», panel D-484 (2), D-548). The Бабл stops a bad event of
/// <see cref="Cell"/>, <see cref="Item"/>, <see cref="Difficulty"/> and <see cref="Incoming"/>, never the price of a reroll
/// (<see cref="RerollCost"/>) or the penalty of a drop (<see cref="DropPenalty"/>).
/// </summary>
public enum EventSource
{
    /// <summary>A stop on an event cell (D-502).</summary>
    Cell,

    /// <summary>The <c>drawEvent</c> of an item or an effect, on its user (D-504).</summary>
    Item,

    /// <summary>A completion of a difficulty with <c>grantEvent</c> (D-504).</summary>
    Difficulty,

    /// <summary>The <c>drawEvent</c> of another player's item, drawn onto the target (D-504, D-548).</summary>
    Incoming,

    /// <summary>A paid reroll with <c>roll.rerollCost.kind = badEvent</c>: the price, drawn before the new roll (D-504).</summary>
    RerollCost,

    /// <summary>The mandatory bad event of a drop (<c>drop.mandatoryEvent = bad</c>): the penalty, after its move (D-504).</summary>
    DropPenalty,
}

/// <summary>The names a deck is referred to by beyond the ids of the content (CONTENT.md, клетка <c>deck: zone</c>).</summary>
public static class DeckRef
{
    /// <summary>The deck of the zone of the cell the player stands on, else <c>events.defaultDeck</c> (D-502).</summary>
    public const string Zone = "zone";
}

/// <summary>
/// An event was drawn (D-500): <see cref="ObjectId"/> out of deck <see cref="Deck"/>, of the deck's kind, by
/// <see cref="PlayerId"/>; the events of its effect on the player follow in the same command. <see cref="FromPlayerId"/> is
/// the other player whose item drew it onto him, <see cref="Hostile"/> tells that item was hostile (D-504, D-548); both are
/// written only when set, so the draws of 5.1 read and write as they were.
/// </summary>
[EventType("event-drawn")]
public sealed record EventDrawn(Guid PlayerId, string Deck, string ObjectId, EventKind Kind, EventSource Source) : IGameEvent
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? FromPlayerId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hostile { get; init; }

    /// <summary>
    /// The run whose difficulty granted the event (<see cref="EventSource.Difficulty"/> only): a run draws its difficulty's
    /// event once, whatever the admin later does to its difficulty (D-557). Written only when set.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RunId { get; init; }
}

/// <summary>
/// The bad event <see cref="EventId"/> just drawn by <see cref="PlayerId"/> was stopped by his object <see cref="InstanceId"/>
/// (<see cref="ObjectId"/>, an effect with <c>intercept: badEvent</c>, the Бабл; D-484, panel (3)): it does not happen, none of
/// its actions follow, and a use of the interceptor is spent. Never for <see cref="EventSource.RerollCost"/> or
/// <see cref="EventSource.DropPenalty"/>.
/// </summary>
[EventType("bad-event-intercepted")]
public sealed record BadEventIntercepted(Guid PlayerId, Guid InstanceId, string ObjectId, EventSource Source, string EventId) : IGameEvent;

/// <summary>
/// Deck <see cref="Deck"/> had nothing to draw — it is not in the content, or none of its events is live (D-501): the
/// event is not lost, a manual effect «draw an event of kind <see cref="Kind"/>» follows.
/// </summary>
[EventType("event-draw-missed")]
public sealed record EventDrawMissed(Guid PlayerId, string Deck, EventKind Kind, EventSource Source) : IGameEvent;

/// <summary>
/// Drawing an event from a deck and playing it at once on the one who drew it (SPEC «Ивенты», D-500…D-502, D-510). With
/// <c>features.events</c> off the event is drawn by hand: a manual effect «draw a good/bad event» (D-10).
/// </summary>
internal static class EventDraws
{
    /// <summary>The kind of a standard deck: the wheels named like the kinds of events (D-501).</summary>
    public static EventKind? KindOf(string deck) => deck switch
    {
        "good" => EventKind.Good,
        "bad" => EventKind.Bad,
        "special" => EventKind.Special,
        "risky" => EventKind.Risky,
        _ => null,
    };

    /// <summary>The id of the standard deck of <paramref name="kind"/>.</summary>
    public static string NameOf(EventKind kind) => kind switch
    {
        EventKind.Good => "good",
        EventKind.Bad => "bad",
        EventKind.Special => "special",
        _ => "risky",
    };

    /// <summary>
    /// The deck <paramref name="deck"/> names for <paramref name="playerId"/> and its kind: <c>zone</c> is the deck of the
    /// zone of the player's cell, else the default deck (D-502); a standard name is that deck; any other — the deck of the
    /// content. The kind is null when nothing tells it (a deck missing from the content under a name that is not a kind).
    /// </summary>
    public static (string Deck, EventKind? Kind) Resolve(SeasonState state, Guid playerId, string deck)
    {
        var wheels = state.Catalog.Wheels;
        if (deck == DeckRef.Zone)
        {
            // A zone naming a deck draws from it, even one missing from the content: a miss, not another deck (D-501)
            if (state.Map.ZoneOf(state.Players[playerId].CellId)?.Deck is { } zoneDeck)
            {
                deck = zoneDeck;
            }
            else
            {
                return state.Rules.Events?.DefaultDeck is { } fallback ? (NameOf(fallback), fallback) : (deck, null);
            }
        }

        return wheels.TryGetValue(deck, out var wheel) && wheel.Deck is { } kind ? (deck, kind) : (deck, KindOf(deck));
    }

    /// <summary>
    /// The events of <paramref name="playerId"/> drawing from <paramref name="deck"/> (D-500, D-501): the drawn event and its
    /// effect, or its interception by a Бабл (D-484), or its text left to manual resolution (<c>manual: true</c>, D-410); or
    /// a miss and a manual effect «draw an event» that remembers <paramref name="source"/> (D-550). The frozen first draws
    /// nothing (D-510); the provisional first draws only by his own run: completion, drop, reroll (D-553). An event that
    /// asks waits for the answer (D-511), whatever its source: <paramref name="level"/> is the chain level its effect runs
    /// at, where its continuation goes on. With <c>features.events</c> off the event is drawn by hand, as before stage 5
    /// (D-10, D-504).
    /// </summary>
    public static List<IGameEvent> Draw(
        SeasonState state,
        EngineContext context,
        Guid playerId,
        string deck,
        EventSource source,
        Guid? runId = null,
        Guid? fromPlayerId = null,
        bool hostile = false,
        int level = 0)
    {
        var (deckId, kind) = Resolve(state, playerId, deck);
        if (!state.Rules.Features.Events)
        {
            // Drawn by hand, as before stage 5 (D-10)
            return kind is { } byHand
                ? [new ManualEffectCreated(context.Ids.NewId(), playerId, byHand, ManualSource(source, events: false), runId)]
                : [];
        }

        if (DrawsNothing(state, playerId, source))
        {
            return [];
        }

        // An unknown kind cannot happen with a checked pack and ruleset; the default deck's kind keeps the event anyway
        var deckKind = kind ?? state.Rules.Events?.DefaultDeck ?? EventKind.Risky;
        var catalog = state.Catalog;
        var entries = catalog.Wheels.TryGetValue(deckId, out var wheel) && wheel.Deck is not null
            ? wheel.Entries.Where(e => catalog.Live(e.ObjectId) is { Kind: ObjectKind.Event }).ToList()
            : [];
        if (entries.Count == 0)
        {
            return
            [
                new EventDrawMissed(playerId, deckId, deckKind, source),
                new ManualEffectCreated(context.Ids.NewId(), playerId, deckKind, ManualSource(source, events: true), runId),
            ];
        }

        var entry = Weighted.Pick(entries, context.Random);
        var definition = catalog.Live(entry.ObjectId)!;
        var drawn = new EventDrawn(playerId, deckId, definition.Id, deckKind, source)
        {
            FromPlayerId = fromPlayerId,
            Hostile = hostile,
            RunId = source == EventSource.Difficulty ? runId : null,
        };
        var after = SeasonEngine.Apply(state, drawn);

        // A bad event by chance meets the Бабл; a price or a penalty never does (D-484 (1), (3))
        if (deckKind == EventKind.Bad && Interceptable(source) && Interception.Find(after, playerId, Intercept.BadEvent) is { } bubble)
        {
            var stopped = new BadEventIntercepted(playerId, bubble.InstanceId, bubble.ObjectId, source, definition.Id);
            return Inventories.AfterFiring(after, playerId, bubble) is { } spent ? [drawn, stopped, spent] : [drawn, stopped];
        }

        List<IGameEvent> events = [drawn];
        if (definition is { Manual: false, Effect: { Target.Selector: TargetSelector.All } shared } && ForAllSettlements.Settles(shared)
            && !Targets.IsFirst(after, playerId))
        {
            events.AddRange(OnEveryone(after, context, playerId, definition, shared, deckKind, source, level));
        }
        else if (definition.Effect is not null)
        {
            // Played at once on the drawer; what it gives him comes from the system, not a player (D-544). A question
            // suspends the effect until the answer, from any source (D-511); the provisional first's event reaches him
            // alone (D-557 (4)), see EffectRun.Play
            var run = new EffectRun(after, context, playerId, definition.Id, hostile: false, parameters: null)
            {
                Asks = new Asking(deckKind, level) { Source = source },
            };
            run.Play(definition);
            events.AddRange(run.Events);
        }

        // A text without automation waits for the player or the admin; the turn goes on (D-410, SPEC «IRL-ивенты»). It
        // remembers a drop, a reroll or a difficulty it came from, so a buy-out never takes it (panel D-508, D-557)
        if (definition.Manual || definition.Effect is null)
        {
            var from = ManualSource(source, events: true);
            events.Add(new ManualEffectCreated(context.Ids.NewId(), playerId, DrawEvent: null, from, from == ManualEffectSource.Event ? null : runId)
            {
                ObjectId = definition.Id,
            });
        }

        return events;
    }

    /// <summary>
    /// A drawn event on everyone (D-757, the owner's decision of 2026-11-14, D-703): its actions on «self» play on the drawer
    /// in his command; the rest goes to its targets — chosen now by the selector, as inside the command — in a settlement for
    /// everyone of its own, in batches of players with their cells' reactions, so moving everyone fits the 50 events.
    /// </summary>
    private static List<IGameEvent> OnEveryone(
        SeasonState state, EngineContext context, Guid playerId, ObjectDefinition definition, EffectSpec effect, EventKind kind, EventSource source, int level)
    {
        var events = new List<IGameEvent>();
        var own = effect.Actions.Where(a => a.Target == ActionTarget.Self).ToList();
        if (own.Count > 0)
        {
            var run = new EffectRun(state, context, playerId, definition.Id, hostile: false, parameters: null)
            {
                Asks = new Asking(kind, level) { Source = source },
            };
            run.Run(effect with { Actions = [.. own], Outcomes = null }, [playerId]);
            events.AddRange(run.Events);
            state = run.State;
        }

        var rest = effect with { Actions = [.. effect.Actions.Where(a => a.Target != ActionTarget.Self)] };
        if (rest.Actions.Count > 0 || rest.Outcomes is not null)
        {
            var targets = Targets.ForTriggered(state, context, definition with { Effect = rest }, playerId);
            events.Add(ForAllSettlements.Queue(state, context, ForAllSource.DrawnEvent, playerId, definition.Id, rest, kind: kind, players: targets));
        }

        return events;
    }

    /// <summary>A difficulty's event marks its run, which never draws it again (D-557).</summary>
    public static SeasonState Apply(SeasonState state, EventDrawn e) =>
        e is { Source: EventSource.Difficulty, RunId: { } runId } && state.Runs.TryGetValue(runId, out var run)
            ? state with { Runs = state.Runs.SetItem(runId, run with { DifficultyEventDrawn = true }) }
            : state;

    /// <summary>
    /// The difficulty's event of <paramref name="runId"/> once its throw, move, finish and stop are done (D-553 (2)): with
    /// events on, a difficulty with <c>grantEvent</c>, not drawn yet for this run, and no branch of the run's walk pending.
    /// </summary>
    public static List<IGameEvent> ForDifficulty(SeasonState state, EngineContext context, Guid runId)
    {
        var run = state.Runs[runId];
        // A run without dice by its unmet condition gets none of the difficulty's reward (D-592)
        if (!state.Rules.Features.Events || run.Difficulty is not { } difficulty || run.DifficultyEventDrawn || Runs.RunConditions.WithoutDice(run))
        {
            return [];
        }

        // A walk paused at a fork has not stopped yet: the event follows the branch (D-546 (7))
        if (state.Players[run.PlayerId].Choice is { Kind: Turns.ChoiceKind.Branch, Move.RunId: { } paused } && paused == runId)
        {
            return [];
        }

        return Runs.CompletionRoll.DieFor(difficulty, run.Snapshot.DieByDifficulty).GrantEvent is { } kind
            ? Draw(state, context, run.PlayerId, NameOf(kind), EventSource.Difficulty, runId)
            : [];
    }

    /// <summary>Whether a bad event of <paramref name="source"/> may be stopped (D-484 (1)): not the price of a reroll, not the penalty of a drop.</summary>
    public static bool Interceptable(EventSource source) => source is not (EventSource.RerollCost or EventSource.DropPenalty);

    // D-510, D-553: the frozen first is out; the provisional first draws only by his own run
    private static bool DrawsNothing(SeasonState state, Guid playerId, EventSource source) =>
        Targets.IsFirst(state, playerId) ? !PlaysForTheFirst(state.Players[playerId], source) : Finish.Finishes.IsFrozen(state.Players[playerId]);

    /// <summary>
    /// Whether the first finisher <paramref name="player"/> still plays an event of <paramref name="source"/> (D-553 (3)):
    /// only while provisional and only by his own run — completion, drop, reroll; a question of it goes on after his
    /// answer too (D-511). A question with no source written is one of 5.7, closed by the answer only (D-567 (8)).
    /// </summary>
    public static bool PlaysForTheFirst(SeasonPlayer player, EventSource? source) =>
        !Finish.Finishes.IsFrozen(player) && source is EventSource.Difficulty or EventSource.DropPenalty or EventSource.RerollCost;

    // D-550: the pending effect remembers where the event came from, so a buy-out never takes the one of a drop or a reroll (panel D-508)
    private static ManualEffectSource ManualSource(EventSource source, bool events) => source switch
    {
        EventSource.DropPenalty => ManualEffectSource.Drop,
        EventSource.RerollCost => ManualEffectSource.PaidReroll,
        EventSource.Difficulty => ManualEffectSource.Difficulty,
        _ => events ? ManualEffectSource.Event : ManualEffectSource.Item,
    };
}

/// <summary>
/// An event cell draws when a move stops on it (SPEC «Движение»: any stop, a push of another included, never a teleport's
/// destination; D-502). It reacts in the effect chain, so a loop «event cell → push → event cell» is cut by its levels (D-500).
/// </summary>
internal sealed class EventCells : ITriggerHandler, IFiringTriggerHandler
{
    public static EventCells Instance { get; } = new();

    public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) => Drawn(state, trigger, context, level: 0);

    /// <summary>A drawn event and what it plays are one firing: cut whole or written whole (D-488); it runs at the chain's level.</summary>
    public IEnumerable<IReadOnlyList<IGameEvent>> ReactInFirings(SeasonState state, IGameEvent trigger, EngineContext context, int depth, int eventsBefore) =>
        Drawn(state, trigger, context, depth) is { Count: > 0 } drawn ? [drawn] : [];

    private static List<IGameEvent> Drawn(SeasonState state, IGameEvent trigger, EngineContext context, int level) =>
        state.Ruleset is not null && state.Rules.Features.Events
            && trigger is PlayerMoved { Paused: false } move && move.Steps != 0 && CellStops.Triggers(move.Reason)
            && move.Reason is not (MoveReason.DropPenalty or MoveReason.CompletionRoll)
            ? Stop(state, context, move, level)
            : [];

    /// <summary>
    /// The draw of the event cell <paramref name="move"/> stopped on, if it is one. The stop of a drop's penalty move draws
    /// inside the drop, before the drop's own bad event (D-504), and the stop of a run's own move inside its command, before
    /// the difficulty's event (D-553 (2), D-557), so the chain leaves those moves alone.
    /// </summary>
    public static List<IGameEvent> Stop(SeasonState state, EngineContext context, PlayerMoved move, int level) =>
        state.Rules.Features.Events && !move.Paused && move.Steps != 0
            && state.Map.CellById(move.To) is { Type: CellType.Event, Deck: { } deck }
            ? EventDraws.Draw(state, context, move.PlayerId, deck, EventSource.Cell, level: level)
            : [];

    /// <summary>
    /// The stop of the command's own move (a run's, a drop's penalty) drawn inside the command: a reaction to the move, so
    /// its event runs at level 1 and what it writes reacts from level 2, as the same loop started by an item — with or
    /// without a question (D-500, D-559 (1), D-567 (4)).
    /// </summary>
    public static List<IGameEvent> StopOfOwnMove(SeasonState state, EngineContext context, PlayerMoved move)
    {
        var drawn = Stop(state, context, move, level: 1);
        context.Levels?.Mark(drawn, 1);
        return drawn;
    }
}
