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
    /// nothing (D-510); the provisional first draws only by his own run: completion, drop, reroll (D-553). With
    /// <c>features.events</c> off the event is drawn by hand, as before stage 5 (D-10, D-504).
    /// </summary>
    public static List<IGameEvent> Draw(
        SeasonState state,
        EngineContext context,
        Guid playerId,
        string deck,
        EventSource source,
        Guid? runId = null,
        Guid? fromPlayerId = null,
        bool hostile = false)
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
        var drawn = new EventDrawn(playerId, deckId, definition.Id, deckKind, source) { FromPlayerId = fromPlayerId, Hostile = hostile };
        var after = SeasonEngine.Apply(state, drawn);

        // A bad event by chance meets the Бабл; a price or a penalty never does (D-484 (1), (3))
        if (deckKind == EventKind.Bad && Interceptable(source) && Interception.Find(after, playerId, Intercept.BadEvent) is { } bubble)
        {
            var stopped = new BadEventIntercepted(playerId, bubble.InstanceId, bubble.ObjectId, source, definition.Id);
            return Inventories.AfterFiring(after, playerId, bubble) is { } spent ? [drawn, stopped, spent] : [drawn, stopped];
        }

        List<IGameEvent> events = [drawn];
        if (definition.Effect is { } effect)
        {
            // Played at once on the drawer; what it gives him comes from the system, not a player (D-544)
            var run = new EffectRun(after, context, playerId, definition.Id, hostile: false, parameters: null);
            run.Run(effect, Targets.ForTriggered(run.State, context, definition, playerId));
            events.AddRange(run.Events);
        }

        // A text without automation waits for the player or the admin; the turn goes on (D-410, SPEC «IRL-ивенты»)
        if (definition.Manual || definition.Effect is null)
        {
            events.Add(new ManualEffectCreated(context.Ids.NewId(), playerId, DrawEvent: null, ManualEffectSource.Event, RunId: null) { ObjectId = definition.Id });
        }

        return events;
    }

    /// <summary>Whether a bad event of <paramref name="source"/> may be stopped (D-484 (1)): not the price of a reroll, not the penalty of a drop.</summary>
    public static bool Interceptable(EventSource source) => source is not (EventSource.RerollCost or EventSource.DropPenalty);

    // D-510, D-553: the frozen first is out; the provisional first draws only by his own run
    private static bool DrawsNothing(SeasonState state, Guid playerId, EventSource source) =>
        Finish.Finishes.IsFrozen(state.Players[playerId])
        || (Targets.IsFirst(state, playerId) && source is EventSource.Cell or EventSource.Item or EventSource.Incoming);

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
internal sealed class EventCells : ITriggerHandler
{
    public static EventCells Instance { get; } = new();

    public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) =>
        state.Ruleset is not null && state.Rules.Features.Events
            && trigger is PlayerMoved { Paused: false } move && move.Steps != 0 && CellStops.Triggers(move.Reason)
            && move.Reason != MoveReason.DropPenalty
            ? Stop(state, context, move)
            : [];

    /// <summary>
    /// The draw of the event cell <paramref name="move"/> stopped on, if it is one. The stop of a drop's penalty move draws
    /// inside the drop, before the drop's own bad event (D-504), so the chain leaves that move alone.
    /// </summary>
    public static List<IGameEvent> Stop(SeasonState state, EngineContext context, PlayerMoved move) =>
        state.Rules.Features.Events && !move.Paused && move.Steps != 0
            && state.Map.CellById(move.To) is { Type: CellType.Event, Deck: { } deck }
            ? EventDraws.Draw(state, context, move.PlayerId, deck, EventSource.Cell)
            : [];
}
