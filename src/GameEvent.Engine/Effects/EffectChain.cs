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
/// The trigger dispatcher (D-24, D-103): after a command is accepted, every handler reacts to its events, then to those
/// reactions, level by level. The command's own events are level 0; reactions may go down to
/// <see cref="Limits.MaxEffectDepth"/> levels and the command to <see cref="Limits.MaxEventsPerCommand"/> events in all;
/// the first reaction beyond either limit ends the chain with <see cref="EffectChainCut"/>.
/// </summary>
internal static class EffectChain
{
    public static IReadOnlyList<IGameEvent> Run(SeasonState state, IReadOnlyList<IGameEvent> commandEvents, EngineContext context)
    {
        var handlers = context.Handlers;
        if (handlers.Count == 0)
        {
            return [];
        }

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
                    foreach (var reaction in handler.React(state, trigger, context))
                    {
                        if (depth > Limits.MaxEffectDepth || total >= Limits.MaxEventsPerCommand)
                        {
                            var limit = depth > Limits.MaxEffectDepth ? EffectChainLimit.Depth : EffectChainLimit.Events;
                            written.Add(new EffectChainCut(limit, depth, total));
                            return written;
                        }

                        state = SeasonEngine.Apply(state, reaction);
                        written.Add(reaction);
                        next.Add(reaction);
                        total++;
                    }
                }
            }

            level = next;
        }

        return written;
    }

    public static SeasonState Apply(SeasonState state, EffectChainCut e) => state;
}
