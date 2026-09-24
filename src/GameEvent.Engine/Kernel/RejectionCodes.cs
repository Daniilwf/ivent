namespace GameEvent.Engine.Kernel;

/// <summary>Stable rejection codes. Each maps to a text in the interface dictionary.</summary>
public static class RejectionCodes
{
    public const string CommandIdReused = "command.idReused";
    public const string SeasonMismatch = "season.mismatch";
    public const string RulesetUnsupported = "ruleset.unsupported";
    public const string SeasonNotCreated = "season.notCreated";
    public const string SeasonAlreadyCreated = "season.alreadyCreated";
    public const string PlayerUnknown = "player.unknown";
    public const string PlayerAlreadyAdded = "player.alreadyAdded";
    public const string WrongPhase = "turn.wrongPhase";
    public const string NoAvailableGames = "roll.noAvailableGames";
    public const string HoursRequired = "run.hoursRequired";
    public const string InvalidHours = "run.invalidHours";
}
