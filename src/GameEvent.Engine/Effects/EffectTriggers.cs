using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Effects;

/// <summary>
/// The triggers that react to what happened (SPEC «Триггеры по фазам хода», D-412): after a roll, the completion, after
/// the throw, a drop, a tech reroll, and every step, pass and stop of a move that triggers cells. The owner's effects fire
/// through the effect chain, so its limits hold (3 levels, 50 events, D-103). The triggers before a roll and before the
/// throw fire inside those commands (they change them).
/// </summary>
internal sealed class EffectTriggers : ITriggerHandler, IFiringTriggerHandler
{
    public static EffectTriggers Instance { get; } = new();

    public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) =>
        ReactInFirings(state, trigger, context, depth: 1, eventsBefore: 0).SelectMany(f => f);

    public IEnumerable<IReadOnlyList<IGameEvent>> ReactInFirings(
        SeasonState state, IGameEvent trigger, EngineContext context, int depth, int eventsBefore)
    {
        if (state.Ruleset is null || !state.Rules.Features.Items || state.Catalog.Objects.Count == 0)
        {
            return [];
        }

        var at = new ChainPoint(depth, eventsBefore);
        return trigger switch
        {
            GameRolled e => Fire(state, context, e.PlayerId, [Trigger.AfterRoll], null, DiceScope.Pending, at),
            GameChoiceRolled e => Fire(state, context, e.PlayerId, [Trigger.AfterRoll], null, DiceScope.Pending, at),
            RunCompleted e => Fire(state, context, e.PlayerId, [Trigger.RunCompleted, Trigger.RunEnded], e.RunId, DiceScope.Pending, at),
            // No throw by an unmet condition, nothing to react to (D-592)
            CompletionRolled e when Runs.RunConditions.WithoutDice(state.Runs[e.RunId]) => [],
            CompletionRolled e => Fire(state, context, e.PlayerId, [Trigger.AfterDice], e.RunId, DiceScope.AfterThrow, at),
            RunDropped e => Fire(state, context, e.PlayerId, [Trigger.Drop, Trigger.RunEnded], e.RunId, DiceScope.Pending, at),
            RunTechRerolled e => Fire(state, context, e.PlayerId, [Trigger.TechReroll], e.RunId, DiceScope.Pending, at),
            PlayerMoved e when e.Steps != 0 && CellStops.Triggers(e.Reason) =>
                Fire(state, context, e.PlayerId, [.. Movement.Visits(e).Select(Of)], null, DiceScope.Pending, at),
            _ => [],
        };
    }

    private static Trigger Of(CellVisit visit) =>
        visit.Kind switch
        {
            CellVisitKind.MoveStep => Trigger.MoveStep,
            CellVisitKind.Pass => Trigger.Pass,
            _ => Trigger.Stop,
        };

    private static List<IReadOnlyList<IGameEvent>> Fire(
        SeasonState state, EngineContext context, Guid ownerId, IReadOnlyList<Trigger> triggers, Guid? runId, DiceScope dice, ChainPoint at)
    {
        var firings = new List<IReadOnlyList<IGameEvent>>();
        var eventsBefore = at.EventsBefore;
        foreach (var trigger in triggers)
        {
            var (fired, after) = Firing.Each(state, context, ownerId, trigger, runId, dice, at with { EventsBefore = eventsBefore });
            firings.AddRange(fired);
            eventsBefore += fired.Sum(f => f.Count);
            state = after;
        }

        return firings;
    }
}
