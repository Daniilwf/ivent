namespace GameEvent.Engine.Kernel;

/// <summary>Stable rejection codes. Each maps to a text in the interface dictionary.</summary>
public static class RejectionCodes
{
    public const string CommandIdReused = "command.idReused";
    public const string CommandInvalid = "command.invalid";
    public const string SeasonMismatch = "season.mismatch";
    public const string RulesetInvalid = "ruleset.invalid";
    public const string RulesetUnchanged = "ruleset.unchanged";
    public const string RulesetVersionConflict = "ruleset.versionConflict";
    public const string SeasonNotCreated = "season.notCreated";
    public const string SeasonNotActive = "season.notActive";
    public const string SeasonInvalidTransition = "season.invalidTransition";
    public const string SeasonClosed = "season.closed";
    public const string SeasonDeadlinePassed = "season.deadlinePassed";
    public const string SeasonDeadlineNotReached = "season.deadlineNotReached";
    public const string SeasonDeadlineInPast = "season.deadlineInPast";
    public const string SeasonProofsPending = "season.proofsPending";
    public const string CellUnknown = "map.unknownCell";
    public const string NothingToChange = "player.nothingToChange";
    public const string CommentRequired = "player.commentRequired";
    public const string EffectNotPending = "effect.notPending";
    public const string UndoUnknownCommand = "undo.unknownCommand";
    public const string UndoNotUndoable = "undo.notUndoable";
    public const string UndoAlreadyUndone = "undo.alreadyUndone";
    public const string UndoDependents = "undo.dependents";
    public const string UndoNoHistory = "undo.noHistory";
    public const string CommandUndone = "command.undone";
    public const string EffectNotYours = "effect.notYours";
    public const string EffectUnknownOutcome = "effect.unknownOutcome";
    public const string CommentTooLong = "player.commentTooLong";
    public const string InvalidResource = "player.invalidResource";
    public const string DeltaTooLarge = "player.deltaTooLarge";
    public const string TransferToFinish = "map.transferToFinish";
    public const string SeasonNothingToChange = "season.nothingToChange";
    public const string SeasonInvalidName = "season.invalidName";
    public const string PlayerBusy = "player.busy";
    public const string PlayerFinished = "player.finished";
    public const string FinishNothingToRecalculate = "finish.nothingToRecalculate";
    public const string SeasonAlreadyCreated = "season.alreadyCreated";
    public const string PlayerUnknown = "player.unknown";
    public const string PlayerAlreadyAdded = "player.alreadyAdded";
    public const string WrongPhase = "turn.wrongPhase";
    public const string ChoicePending = "turn.choicePending";
    public const string NoPendingChoice = "turn.noPendingChoice";
    public const string UnknownChoiceOption = "turn.unknownOption";
    public const string NoAvailableGames = "roll.noAvailableGames";
    public const string GameNotOffered = "roll.gameNotOffered";
    public const string NotEnoughCoins = "roll.notEnoughCoins";
    public const string TooManyUncheckedRuns = "roll.tooManyUnchecked";
    public const string HoursRequired = "run.hoursRequired";
    public const string InvalidHours = "run.invalidHours";
    public const string TechRerollWindowClosed = "run.techRerollWindowClosed";
    public const string WishRerollNotListed = "run.wishRerollNotListed";
    public const string WishRerollImposed = "run.wishRerollImposed";
    public const string ReasonCommentRequired = "run.reasonCommentRequired";
    public const string RunUnknown = "run.unknown";
    public const string NotTechRerolled = "run.notTechRerolled";
    public const string HoursSourceRequired = "run.hoursSourceRequired";
    public const string HoursSourceTooLong = "run.hoursSourceTooLong";
    public const string FeatureDisabled = "feature.disabled";
    public const string NotYourRun = "run.notYours";
    public const string RunNotCompleted = "run.notCompleted";
    public const string RunNothingToChange = "run.nothingToChange";
    public const string ProofAlreadyReviewed = "proof.alreadyReviewed";
    public const string ProofInvalidLink = "proof.invalidLink";
    public const string ProofEmpty = "proof.empty";
    public const string ProofInvalidFile = "proof.invalidFile";
    public const string ProofWitnessInvalid = "proof.witnessInvalid";
    public const string ProofDifficultyAboveClaimed = "proof.difficultyAboveClaimed";
    public const string MapInvalid = "map.invalid";
    public const string MapRequired = "map.required";
    public const string MapNotInLinearMode = "map.notInLinearMode";
    public const string MapModeFixed = "ruleset.mapModeFixed";

    /// <summary>Bets are switched off only when none is open and no win waits for its run's proof (D-420).</summary>
    public const string BetsInPlay = "ruleset.betsInPlay";
    public const string WeeklyChallengeInPlay = "ruleset.weeklyChallengeInPlay";
    public const string MapOccupiedCellRemoved = "map.occupiedCellRemoved";
    public const string MapUnchanged = "map.unchanged";
    public const string MapOccupiedCellRetyped = "map.occupiedCellRetyped";
    public const string BranchChoicePending = "map.branchChoicePending";
    public const string UndoCellNotOnMap = "undo.cellNotOnMap";
    public const string InvalidRating = "review.invalidRating";
    public const string ReviewTooLong = "review.tooLong";
    public const string ContentInvalid = "content.invalid";
    public const string ContentUnchanged = "content.unchanged";

    /// <summary>The pack was made from an older content version than the one in force (D-491).</summary>
    public const string ContentVersionChanged = "content.versionChanged";
    public const string ItemUnknown = "item.unknown";
    public const string ItemNotAnItem = "item.notAnItem";
    public const string ItemWrongWindow = "item.wrongWindow";
    public const string ItemNotStackable = "item.notStackable";
    public const string ItemTargetRequired = "item.targetRequired";
    public const string ItemInvalidTarget = "item.invalidTarget";
    public const string ItemNoTarget = "item.noTarget";
    public const string ItemInvalidChoice = "item.invalidChoice";

    /// <summary>An effect that hands over one of the user's objects, and he has none it may give (D-478).</summary>
    public const string ItemNothingToGive = "item.nothingToGive";

    /// <summary>An effect that must take something from its target (<c>required</c>), and the target has nothing it may take (D-487).</summary>
    public const string ItemNothingToTake = "item.nothingToTake";

    /// <summary>The item's use condition does not hold for its user (D-483).</summary>
    public const string ItemConditionNotMet = "item.conditionNotMet";
    public const string ItemHostileCap = "item.hostileCap";
    public const string ItemNotApplicable = "item.notApplicable";
    public const string InventoryFrozen = "inventory.frozen";
    public const string InventoryFull = "inventory.full";
    public const string ObjectUnknown = "content.unknownObject";
    public const string ShopNoOffer = "shop.noOffer";
    public const string ShopOfferExpired = "shop.offerExpired";
    public const string ShopUnknownLot = "shop.unknownLot";
    public const string ShopLotSold = "shop.lotSold";
    public const string ShopEmpty = "shop.empty";
    public const string CoinsInDebt = "coins.inDebt";
    public const string TimersNothingDue = "timers.nothingDue";
    public const string BetOnSelf = "bet.onSelf";
    public const string BetNoRun = "bet.noRun";
    public const string BetWindowClosed = "bet.windowClosed";
    public const string BetInvalidDays = "bet.invalidDays";
    public const string BetInvalidStake = "bet.invalidStake";
    public const string BetTooMany = "bet.tooMany";
    public const string BetOnFirst = "bet.onFirst";
    public const string BetAlreadyPlaced = "bet.alreadyPlaced";

    /// <summary>A content pack over the size limits of <c>ContentPublishing</c> (NB-SEC-3, D-653).</summary>
    public const string ContentTooLarge = "content.tooLarge";

    public const string WeeklyChallengeUnknown = "weeklyChallenge.unknown";
    public const string WeeklyChallengeAlreadyCompleted = "weeklyChallenge.alreadyCompleted";
    public const string WeeklyChallengeOutsideWindow = "weeklyChallenge.outsideWindow";
    public const string WeeklyChallengeNotManual = "weeklyChallenge.notManual";

    /// <summary>The window would end past the last moment of the calendar (D-578).</summary>
    public const string WeeklyChallengeWindowInvalid = "weeklyChallenge.windowInvalid";
    public const string SuggestionUnknown = "suggestion.unknown";
    public const string SuggestionAlreadyReviewed = "suggestion.alreadyReviewed";
    public const string SuggestionDailyLimit = "suggestion.dailyLimit";
    public const string SuggestionEmpty = "suggestion.empty";
    public const string SuggestionGameRequired = "suggestion.gameRequired";

    /// <summary>The suggestion's text is over <see cref="Limits.MaxSuggestionLength"/> (D-579).</summary>
    public const string SuggestionTooLong = "suggestion.tooLong";

    /// <summary>The suggested game's card breaks the pool's rules (<see cref="Pool.GameCards"/>, D-579).</summary>
    public const string SuggestionCardInvalid = "suggestion.cardInvalid";

    /// <summary>The host does not know <c>season.timezone</c>, so no calendar day can be counted (D-579).</summary>
    public const string SeasonTimezoneUnknown = "season.timezoneUnknown";
}
