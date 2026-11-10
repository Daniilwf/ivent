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
internal sealed class EffectTriggers : ITriggerHandler
{
    public static EffectTriggers Instance { get; } = new();

    public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context)
    {
        if (state.Ruleset is null || !state.Rules.Features.Items || state.Catalog.Objects.Count == 0)
        {
            return [];
        }

        return trigger switch
        {
            GameRolled e => Fire(state, context, e.PlayerId, [Trigger.AfterRoll], null, DiceScope.Pending),
            GameChoiceRolled e => Fire(state, context, e.PlayerId, [Trigger.AfterRoll], null, DiceScope.Pending),
            RunCompleted e => Fire(state, context, e.PlayerId, [Trigger.RunCompleted], e.RunId, DiceScope.Pending),
            CompletionRolled e => Fire(state, context, e.PlayerId, [Trigger.AfterDice], e.RunId, DiceScope.AfterThrow),
            RunDropped e => Fire(state, context, e.PlayerId, [Trigger.Drop], e.RunId, DiceScope.Pending),
            RunTechRerolled e => Fire(state, context, e.PlayerId, [Trigger.TechReroll], e.RunId, DiceScope.Pending),
            PlayerMoved e when e.Steps != 0 && CellStops.Triggers(e.Reason) =>
                Fire(state, context, e.PlayerId, [.. Movement.Visits(e).Select(Of).Distinct()], null, DiceScope.Pending),
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

    private static List<IGameEvent> Fire(
        SeasonState state, EngineContext context, Guid ownerId, IReadOnlyList<Trigger> triggers, Guid? runId, DiceScope dice)
    {
        var events = new List<IGameEvent>();
        foreach (var trigger in triggers)
        {
            var (fired, after) = Firing.All(state, context, ownerId, trigger, runId, dice);
            events.AddRange(fired);
            state = after;
        }

        return events;
    }
}
