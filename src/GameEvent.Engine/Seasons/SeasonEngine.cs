using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
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

        // The levels of this command's own events (D-559 (1)), never another command's
        context = context with { Levels = new CommandLevels() };
        var decision = command switch
        {
            CreateSeason c => SeasonSetup.Decide(state, c),
            ChangeRuleset c => RulesetChanges.Decide(state, c),
            ChangeSeasonStatus c => SeasonSetup.Decide(state, c, context),
            SetSeasonDeadline c => SeasonSetup.Decide(state, c, context),
            ReachDeadline c => SeasonSetup.Decide(state, c, context),
            AddSeasonPlayer c => PlayerAdministration.Decide(state, c),
            SetPlayerInactive c => PlayerAdministration.Decide(state, c, context),
            AdjustPlayer c => PlayerAdministration.Decide(state, c),
            RollGame c => Rolling.Decide(state, c, context),
            DeclareAlreadyPlayed c => Rolling.Decide(state, c, context),
            Reroll c => Rolling.Decide(state, c, context),
            StartRun c => RunLifecycle.Decide(state, c, context),
            CompleteRun c => RunLifecycle.Decide(state, c, context),
            ReviewRun c => RunLifecycle.Decide(state, c, context),
            CorrectRunHours c => Corrections.Decide(state, c, context),
            SubmitProof c => ProofReview.Decide(state, c, context),
            ApproveProof c => ProofReview.Decide(state, c, context),
            RejectProof c => ProofReview.Decide(state, c, context),
            RejectProofWithDropPenalty c => ProofReview.Decide(state, c, context),
            ChangeRunDifficulty c => Corrections.Decide(state, c, context),
            WaiveRunCondition c => RunConditions.Decide(state, c, context),
            DropRun c => Drops.Decide(state, c, context),
            TechReroll c => Drops.Decide(state, c, context),
            ConvertTechRerollToDrop c => Drops.Decide(state, c, context),
            MakeChoice c => Choosing.Decide(state, c, context),
            PublishMap c => MapPublishing.Decide(state, c),
            ResolveManualEffect c => ManualEffects.Decide(state, c),
            RecalculateFinishBonuses c => Finishing.Decide(state, c),
            Undo.UndoCommand c => Undo.Undoing.Decide(state, c, context),
            PublishContent c => ContentPublishing.Decide(state, c),
            UseItem c => ItemUse.Decide(state, c, context),
            AdjustInventory c => ItemUse.Decide(state, c, context),
            RollShop c => Shop.Decide(state, c, context),
            BuyLot c => Shop.Decide(state, c, context),
            PlaceBet c => Betting.Decide(state, c, context),
            Challenges.SetWeeklyChallenge c => Challenges.WeeklyChallenges.Decide(state, c, context),
            Challenges.MarkWeeklyChallenge c => Challenges.WeeklyChallenges.Decide(state, c),
            Suggestions.SubmitSuggestion c => Suggestions.SuggestionBox.Decide(state, c, context),
            Suggestions.ReviewSuggestion c => Suggestions.SuggestionBox.Decide(state, c, context),
            SubmitIrlProof c => IrlProofs.Decide(state, c, context),
            ReviewIrlProof c => IrlProofs.Decide(state, c, context),
            Interactions.InviteToCoop c => Interactions.Coop.Decide(state, c, context),
            Interactions.AnswerInvitation c => Interactions.Coop.Decide(state, c, context),
            Interactions.WithdrawInvitation c => Interactions.Coop.Decide(state, c, context),
            Interactions.LeaveCoop c => Interactions.Coop.Decide(state, c, context),
            FireTimers => Timers.Decide(state, context),
            _ => throw new ArgumentException($"Unknown command {command.GetType().Name}.", nameof(command)),
        };

        if (!decision.IsAccepted)
        {
            return new CommandResult(decision, state);
        }

        if (command is not MakeChoice && MovesAPlayerChoosingABranch(state, decision.Events) is { } choosing)
        {
            // D-305: the steps left at a fork lead from where the player stands; move them only after the choice
            decision = Decision.Reject(
                RejectionCodes.BranchChoicePending, $"Player {choosing} is choosing a branch: wait for the choice or discard it first.");
            return new CommandResult(decision, state);
        }

        // Bets, weekly challenges, the shop price and the lifetimes of objects follow the command's events without limits (D-413); effects
        // react after them, within the chain limits (D-24, D-103).
        var after = decision.Events.Aggregate(state, Apply);
        var settlements = Settlements.After(after, decision.Events, context);
        after = settlements.Aggregate(after, Apply);
        IReadOnlyList<IGameEvent> own = [.. decision.Events, .. settlements];
        // A continuation of a suspended effect reacts from the level it stopped at, each its own (D-511); the stop of an own
        // move from the level of its draw (D-559 (1))
        var (reactions, reacted) = EffectChain.Run(after, own, context, Levels(state, decision.Events, context.Levels!));
        var lifetimes = Settlements.Lifetimes(reacted, own);
        var final = lifetimes.Aggregate(reacted, Apply);
        return reactions.Count == 0 && settlements.Count == 0 && lifetimes.Count == 0
            ? new CommandResult(decision, after)
            : new CommandResult(Decision.Accept([.. own, .. reactions, .. lifetimes]), final);
    }

    // D-511, D-559 (1): the level each of the command's own events reacts from — a continuation's, a stop's, else 0
    private static IReadOnlyList<int>? Levels(SeasonState state, IReadOnlyList<IGameEvent> events, CommandLevels marked)
    {
        var resumed = Suspensions.Levels(state, events);
        if (marked.IsEmpty)
        {
            return resumed;
        }

        var levels = new int[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            levels[i] = Math.Max(resumed?[i] ?? 0, marked.Of(events[i]));
        }

        return levels;
    }

    // D-305: a player choosing a branch is not moved by other commands, and the run whose steps wait is not corrected or
    // rejected — unless the same command discards the choice.
    private static Guid? MovesAPlayerChoosingABranch(SeasonState state, IReadOnlyList<IGameEvent> events)
    {
        foreach (var player in state.Players.Values.Where(p => p.Choice?.Kind == ChoiceKind.Branch))
        {
            if (events.OfType<ChoiceDiscarded>().Any(d => d.PlayerId == player.PlayerId))
            {
                continue;
            }

            var run = player.Choice!.Move?.RunId;
            if (events.Any(e => e switch
            {
                PlayerMoved moved => moved.PlayerId == player.PlayerId,
                RunHoursCorrected corrected => corrected.RunId == run,
                RunDifficultyChanged changed => changed.RunId == run,
                RunConditionChecked conditionChecked => conditionChecked.RunId == run,
                ProofRejected rejected => rejected.RunId == run,
                _ => false,
            }))
            {
                return player.PlayerId;
            }
        }

        return null;
    }

    public static SeasonState Apply(SeasonState state, IGameEvent gameEvent) =>
        gameEvent switch
        {
            SeasonCreated e => SeasonSetup.Apply(state, e),
            SeasonStatusChanged e => SeasonSetup.Apply(state, e),
            SeasonDeadlineSet e => SeasonSetup.Apply(state, e),
            SeasonResultRecorded e => SeasonSetup.Apply(state, e),
            SeasonPlayerAdded e => PlayerAdministration.Apply(state, e),
            PlayerInactivitySet e => PlayerAdministration.Apply(state, e),
            PlayerAdjusted e => PlayerAdministration.Apply(state, e),
            OfferDiscarded e => PlayerAdministration.Apply(state, e),
            CoinsChanged e => PointsLedger.Apply(state, e),
            ResourceChanged e => PointsLedger.Apply(state, e),
            RulesetChanged e => RulesetChanges.Apply(state, e),
            GameRolled e => Rolling.Apply(state, e),
            GameExcluded e => Rolling.Apply(state, e),
            GameRerolled e => Rolling.Apply(state, e),
            ManualEffectCreated e => ManualEffects.Apply(state, e),
            GameChoiceRolled e => Choosing.Apply(state, e),
            ChoiceMade e => Choosing.Apply(state, e),
            BranchChoiceRequested e => Choosing.Apply(state, e),
            ChoiceDiscarded e => Choosing.Apply(state, e),
            RunStarted e => RunLifecycle.Apply(state, e),
            RunCompleted e => RunLifecycle.Apply(state, e),
            CompletionRolled e => RunLifecycle.Apply(state, e),
            RunReviewed e => RunLifecycle.Apply(state, e),
            RunHoursCorrected e => Corrections.Apply(state, e),
            ProofSubmitted e => ProofReview.Apply(state, e),
            PlayerFinished e => Finishing.Apply(state, e),
            PlayerFrozen e => Finishing.Apply(state, e),
            PlayerFinishRevoked e => Finishing.Apply(state, e),
            FinishSurplusChanged e => Finishing.Apply(state, e),
            FinishBonusRulesRefreshed e => Finishing.Apply(state, e),
            ProofApproved e => ProofReview.Apply(state, e),
            ProofRejected e => ProofReview.Apply(state, e),
            RunDifficultyChanged e => Corrections.Apply(state, e),
            RunConditionChecked e => RunConditions.Apply(state, e),
            ManualEffectResolved e => ManualEffects.Apply(state, e),
            RunDropped e => Drops.Apply(state, e),
            RunTechRerolled e => Drops.Apply(state, e),
            TechRerollConvertedToDrop e => Drops.Apply(state, e),
            ProofRejectPenalized => state,
            PointsChanged e => PointsLedger.Apply(state, e),
            PlayerMoved e => Movement.Apply(state, e),
            MapPublished e => MapPublishing.Apply(state, e),
            EffectChainCut e => EffectChain.Apply(state, e),
            Undo.CommandUndone e => Undo.Undoing.Apply(state, e),
            ContentPublished e => ContentPublishing.Apply(state, e),
            ObjectGiven e => Inventories.Apply(state, e),
            ObjectRemoved e => Inventories.Apply(state, e),
            ObjectTransferred e => Inventories.Apply(state, e),
            ObjectChanged e => Inventories.Apply(state, e),
            HostileReceived e => Inventories.Apply(state, e),
            NextRollModified e => Inventories.Apply(state, e),
            RollModifiersApplied e => Inventories.Apply(state, e),
            NextDiceModified e => Inventories.Apply(state, e),
            RunDiceModified e => DicePipeline.Apply(state, e),
            RunDiceRerolled e => DicePipeline.Apply(state, e),
            ShopRolled e => Shop.Apply(state, e),
            LotBought e => Shop.Apply(state, e),
            ShopOfferExpired e => Shop.Apply(state, e),
            ShopPriceRestarted e => Shop.Apply(state, e),
            BetPlaced e => Betting.Apply(state, e),
            BetSettled e => Betting.Apply(state, e),
            Challenges.WeeklyChallengeSet e => Challenges.WeeklyChallenges.Apply(state, e),
            Challenges.WeeklyChallengeCompleted e => Challenges.WeeklyChallenges.Apply(state, e),
            Challenges.WeeklyChallengeRevoked e => Challenges.WeeklyChallenges.Apply(state, e),
            Suggestions.SuggestionSubmitted e => Suggestions.SuggestionBox.Apply(state, e),
            Suggestions.SuggestionReviewed e => Suggestions.SuggestionBox.Apply(state, e),
            IrlProofSubmitted e => IrlProofs.Apply(state, e),
            IrlProofReviewed e => IrlProofs.Apply(state, e),
            Interactions.InteractionCreated e => Interactions.Coop.Apply(state, e),
            Interactions.InteractionAnswered e => Interactions.Coop.Apply(state, e),
            Interactions.InteractionWithdrawn e => Interactions.Coop.Apply(state, e),
            Interactions.InteractionExpired e => Interactions.Coop.Apply(state, e),
            Interactions.CoopJoined e => Interactions.Coop.Apply(state, e),
            Interactions.CoopLeft e => Interactions.Coop.Apply(state, e),
            Interactions.CoopRewardSplit e => Interactions.Coop.Apply(state, e),

            // Facts for the log and the feed that change nothing by themselves: their consequences are their own events.
            ObjectLost or ItemUsed or EffectTriggered or EffectRolled or HostileIntercepted or WheelSpun or InventoryAdjusted => state,
            Decks.EventDrawn e => Decks.EventDraws.Apply(state, e),
            Decks.EventDrawMissed or Decks.BadEventIntercepted => state,
            EffectSuspended e => Suspensions.Apply(state, e),
            EffectResumed e => Suspensions.Apply(state, e),
            EffectMoveSkipped => state,
            _ => throw new ArgumentException($"Unknown event {gameEvent.GetType().Name}.", nameof(gameEvent)),
        };

    public static SeasonState Replay(IEnumerable<IGameEvent> events) => events.Aggregate(SeasonState.Empty, Apply);
}
