using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Seasons;

/// <summary>Result of executing a command: the decision and the state after its events.</summary>
public sealed record CommandResult(Decision Decision, SeasonState State)
{
    public bool IsAccepted => Decision.IsAccepted;

    public IReadOnlyList<IGameEvent> Events => Decision.Events;

    public Rejection? Rejection => Decision.Rejection;
}

/// <summary>
/// The public contract of the rules engine (D-01): decide a command against the state, and fold events into state.
/// Replaying a log is a fold of <see cref="Apply"/> from <see cref="SeasonState.Empty"/>; it needs no randomness or pool.
/// </summary>
public static class SeasonEngine
{
    public static CommandResult Execute(SeasonState state, ICommand command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var decision = command switch
        {
            CreateSeason c => SeasonSetup.Decide(state, c),
            ChangeRuleset c => RulesetChanges.Decide(state, c),
            ChangeSeasonStatus c => SeasonSetup.Decide(state, c),
            SetSeasonDeadline c => SeasonSetup.Decide(state, c),
            AddSeasonPlayer c => PlayerAdministration.Decide(state, c),
            SetPlayerInactive c => PlayerAdministration.Decide(state, c),
            AdjustPlayer c => PlayerAdministration.Decide(state, c),
            RollGame c => Rolling.Decide(state, c, context),
            DeclareAlreadyPlayed c => Rolling.Decide(state, c, context),
            StartRun c => RunLifecycle.Decide(state, c, context),
            CompleteRun c => RunLifecycle.Decide(state, c, context),
            MakeChoice c => Choosing.Decide(state, c, context),
            _ => throw new ArgumentException($"Unknown command {command.GetType().Name}.", nameof(command)),
        };

        return new CommandResult(decision, decision.Events.Aggregate(state, Apply));
    }

    public static SeasonState Apply(SeasonState state, IGameEvent gameEvent) =>
        gameEvent switch
        {
            SeasonCreated e => SeasonSetup.Apply(state, e),
            SeasonStatusChanged e => SeasonSetup.Apply(state, e),
            SeasonDeadlineSet e => SeasonSetup.Apply(state, e),
            SeasonPlayerAdded e => PlayerAdministration.Apply(state, e),
            PlayerInactivitySet e => PlayerAdministration.Apply(state, e),
            PlayerAdjusted e => PlayerAdministration.Apply(state, e),
            OfferDiscarded e => PlayerAdministration.Apply(state, e),
            CoinsChanged e => PointsLedger.Apply(state, e),
            ResourceChanged e => PointsLedger.Apply(state, e),
            RulesetChanged e => RulesetChanges.Apply(state, e),
            GameRolled e => Rolling.Apply(state, e),
            GameExcluded e => Rolling.Apply(state, e),
            GameChoiceRolled e => Choosing.Apply(state, e),
            ChoiceMade e => Choosing.Apply(state, e),
            ChoiceDiscarded e => Choosing.Apply(state, e),
            RunStarted e => RunLifecycle.Apply(state, e),
            RunCompleted e => RunLifecycle.Apply(state, e),
            CompletionRolled e => RunLifecycle.Apply(state, e),
            PointsChanged e => PointsLedger.Apply(state, e),
            PlayerMoved e => Movement.Apply(state, e),
            _ => throw new ArgumentException($"Unknown event {gameEvent.GetType().Name}.", nameof(gameEvent)),
        };

    public static SeasonState Replay(IEnumerable<IGameEvent> events) => events.Aggregate(SeasonState.Empty, Apply);
}
