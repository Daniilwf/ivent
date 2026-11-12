using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Decks;

/// <summary>Where a drawn event came from (GLOSSARY «Источник ивента»); stage 5 adds the drop, the paid reroll and the difficulty.</summary>
public enum EventSource
{
    /// <summary>A stop on an event cell (D-502).</summary>
    Cell,

    /// <summary>The <c>drawEvent</c> of an item or an effect (D-504).</summary>
    Item,
}

/// <summary>The names a deck is referred to by beyond the ids of the content (CONTENT.md, клетка <c>deck: zone</c>).</summary>
public static class DeckRef
{
    /// <summary>The deck of the zone of the cell the player stands on, else <c>events.defaultDeck</c> (D-502).</summary>
    public const string Zone = "zone";
}

/// <summary>
/// An event was drawn (D-500): <see cref="ObjectId"/> out of deck <see cref="Deck"/>, of the deck's kind, by
/// <see cref="PlayerId"/>; the events of its effect on the player follow in the same command.
/// </summary>
[EventType("event-drawn")]
public sealed record EventDrawn(Guid PlayerId, string Deck, string ObjectId, EventKind Kind, EventSource Source) : IGameEvent;

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
    /// The events of <paramref name="playerId"/> drawing from <paramref name="deck"/>: the drawn event and its effect, or a
    /// miss and a manual effect; nothing for the first finisher, whose effects are frozen (D-510).
    /// </summary>
    public static List<IGameEvent> Draw(SeasonState state, EngineContext context, Guid playerId, string deck, EventSource source)
    {
        var (deckId, kind) = Resolve(state, playerId, deck);
        if (!state.Rules.Features.Events)
        {
            // Drawn by hand, as before stage 5 (D-10)
            return kind is { } byHand
                ? [new ManualEffectCreated(context.Ids.NewId(), playerId, byHand, ManualEffectSource.Item, RunId: null)]
                : [];
        }

        if (Targets.IsFirst(state, playerId))
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
                new ManualEffectCreated(context.Ids.NewId(), playerId, deckKind, ManualEffectSource.Event, RunId: null),
            ];
        }

        var entry = Weighted.Pick(entries, context.Random);
        var definition = catalog.Live(entry.ObjectId)!;
        var drawn = new EventDrawn(playerId, deckId, definition.Id, deckKind, source);
        // The pack keeps only events with an effect on the drawer (D-536): they are played at once, never hostile
        var effect = definition.Effect!;
        var run = new EffectRun(SeasonEngine.Apply(state, drawn), context, playerId, definition.Id, hostile: false, parameters: null);
        run.Run(effect, Targets.ForTriggered(run.State, context, definition, playerId));
        return [drawn, .. run.Events];
    }
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
            && state.Map.CellById(move.To) is { Type: CellType.Event, Deck: { } deck }
            ? EventDraws.Draw(state, context, move.PlayerId, deck, EventSource.Cell)
            : [];
}
