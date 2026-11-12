using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Effects;

/// <summary>
/// Reacts to the events of a command with more events: the entry point of effects, cells and achievements (stages 2–6).
/// Stage 1 has no handlers in the product; tests plug their own to check the chain limits (D-24, D-103).
/// </summary>
public interface ITriggerHandler
{
    /// <summary>The events <paramref name="trigger"/> causes in <paramref name="state"/>, which already holds it.</summary>
    IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context);
}

/// <summary>Which limit stopped an effect chain (SPEC «Лимит цепочки»).</summary>
public enum EffectChainLimit
{
    /// <summary>A reaction would be nested deeper than <see cref="Limits.MaxEffectDepth"/>.</summary>
    Depth,

    /// <summary>The command would write more than <see cref="Limits.MaxEventsPerCommand"/> events.</summary>
    Events,
}

/// <summary>
/// An effect chain was cut (SPEC «Лимит цепочки», E2): the reactions from here on were not written. <see cref="Depth"/>
/// is the level of the reaction that was dropped, <see cref="Events"/> how many events the command wrote before the cut.
/// </summary>
[EventType("effect-chain-cut")]
public sealed record EffectChainCut(EffectChainLimit Limit, int Depth, int Events) : IGameEvent;

/// <summary>
/// A handler whose reactions come in firings (an effect: its trigger, actions and the use spent) that are written whole or
/// not at all (AU4-4): a cut never leaves half an effect applied. <c>depth</c> and <c>eventsBefore</c>
/// tell a countdown that goes off inside a firing how much of the chain is left (AU4-1).
/// </summary>
internal interface IFiringTriggerHandler
{
    IEnumerable<IReadOnlyList<IGameEvent>> ReactInFirings(SeasonState state, IGameEvent trigger, EngineContext context, int depth, int eventsBefore);
}

/// <summary>
/// The chain limits of the season (invariant 8): <c>effects.maxChainDepth</c> and <c>effects.maxEventsPerCommand</c> of the
/// ruleset, never above the protective ceilings of SPEC «Лимит цепочки» in <see cref="Limits"/> (D-418, AU4-7).
/// </summary>
internal readonly record struct ChainLimits(int Depth, int Events)
{
    public static ChainLimits Of(SeasonState state) =>
        state.Ruleset is { } rules
            ? new(Math.Min(rules.Effects.MaxChainDepth, Limits.MaxEffectDepth), Math.Min(rules.Effects.MaxEventsPerCommand, Limits.MaxEventsPerCommand))
            : new(Limits.MaxEffectDepth, Limits.MaxEventsPerCommand);

    /// <summary>The cut for a reaction on <paramref name="depth"/> with <paramref name="count"/> events after <paramref name="eventsBefore"/>; null when it fits.</summary>
    public EffectChainCut? Cut(int depth, int eventsBefore, int count) =>
        depth > Depth ? new EffectChainCut(EffectChainLimit.Depth, depth, eventsBefore)
        : eventsBefore + count > Events ? new EffectChainCut(EffectChainLimit.Events, depth, eventsBefore)
        : null;
}

/// <summary>
/// The trigger dispatcher (D-24, D-103): after a command is accepted, every handler reacts to its events, then to those
/// reactions, level by level. The command's own events are level 0; reactions may go down to the season's
/// <see cref="ChainLimits"/> (3 levels, 50 events in all); the first reaction beyond either limit ends the chain with
/// <see cref="EffectChainCut"/>, written over the 50 (D-103). A firing of an effect is one reaction: it fits whole or is cut
/// whole (AU4-4). The command's own events are not limited here: commands write a handful; a command already cut inside
/// (a countdown going off too deep, AU4-1) reacts no further.
/// </summary>
internal static class EffectChain
{
    public static (IReadOnlyList<IGameEvent> Reactions, SeasonState State) Run(
        SeasonState state, IReadOnlyList<IGameEvent> commandEvents, EngineContext context)
    {
        if (commandEvents.Any(e => e is EffectChainCut))
        {
            return ([], state);
        }

        // Event cells (D-502) and the effects of items and cells (D-412) react first, then any handlers of the context (tests, D-24).
        IReadOnlyList<ITriggerHandler> handlers = [Decks.EventCells.Instance, EffectTriggers.Instance, .. context.Triggers ?? []];
        var limits = ChainLimits.Of(state);

        var written = new List<IGameEvent>();
        var total = commandEvents.Count;
        IReadOnlyList<IGameEvent> level = commandEvents;
        for (var depth = 1; level.Count > 0; depth++)
        {
            var next = new List<IGameEvent>();
            foreach (var trigger in level)
            {
                foreach (var handler in handlers)
                {
                    var reactions = handler is IFiringTriggerHandler firings
                        ? firings.ReactInFirings(state, trigger, context, depth, total)
                        : handler is Decks.EventCells
                            // A drawn event and what it plays are one firing: cut whole or written whole (D-488)
                            ? handler.React(state, trigger, context).ToList() is { Count: > 0 } drawn ? [drawn] : []
                            : handler.React(state, trigger, context).Select(e => (IReadOnlyList<IGameEvent>)[e]);
                    foreach (var reaction in reactions)
                    {
                        if (limits.Cut(depth, total, reaction.Count) is { } cut)
                        {
                            written.Add(cut);
                            return (written, state);
                        }

                        foreach (var e in reaction)
                        {
                            state = SeasonEngine.Apply(state, e);
                        }

                        written.AddRange(reaction);
                        next.AddRange(reaction);
                        total += reaction.Count;

                        // A countdown inside the firing went past the limits: its cut ends the chain (AU4-1)
                        if (reaction.Any(e => e is EffectChainCut))
                        {
                            return (written, state);
                        }
                    }
                }
            }

            level = next;
        }

        return (written, state);
    }

    public static SeasonState Apply(SeasonState state, EffectChainCut e) => state;
}
