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
    public const string CellUnknown = "map.unknownCell";
    public const string NothingToChange = "player.nothingToChange";
    public const string CommentRequired = "player.commentRequired";
    public const string CommentTooLong = "player.commentTooLong";
    public const string InvalidResource = "player.invalidResource";
    public const string DeltaTooLarge = "player.deltaTooLarge";
    public const string TransferToFinish = "map.transferToFinish";
    public const string SeasonNothingToChange = "season.nothingToChange";
    public const string SeasonInvalidName = "season.invalidName";
    public const string PlayerBusy = "player.busy";
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
    public const string HoursRequired = "run.hoursRequired";
    public const string InvalidHours = "run.invalidHours";
    public const string TechRerollWindowClosed = "run.techRerollWindowClosed";
    public const string ReasonCommentRequired = "run.reasonCommentRequired";
    public const string RunUnknown = "run.unknown";
    public const string NotTechRerolled = "run.notTechRerolled";
}
