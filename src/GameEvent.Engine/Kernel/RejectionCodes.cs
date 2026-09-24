namespace GameEvent.Engine.Kernel;

/// <summary>Stable rejection codes. Each maps to a text in the interface dictionary.</summary>
public static class RejectionCodes
{
    public const string CommandIdReused = "command.idReused";
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
    public const string PlayerBusy = "player.busy";
    public const string SeasonAlreadyCreated = "season.alreadyCreated";
    public const string PlayerUnknown = "player.unknown";
    public const string PlayerAlreadyAdded = "player.alreadyAdded";
    public const string WrongPhase = "turn.wrongPhase";
    public const string NoAvailableGames = "roll.noAvailableGames";
    public const string HoursRequired = "run.hoursRequired";
    public const string InvalidHours = "run.invalidHours";
}
