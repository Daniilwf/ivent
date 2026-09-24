namespace GameEvent.Engine.Rulesets;

/// <summary>
/// Values this build cannot play yet (D-22): a season must not start with a mechanic that is not implemented.
/// Each check goes away when its task lands (graph map — stage 2, choice of N — C5, several runs — C4).
/// </summary>
public static class RulesetSupport
{
    public static string? Unsupported(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);

        if (ruleset.Features.MapMode != MapMode.Linear)
        {
            return $"features.mapMode '{ruleset.Features.MapMode}' is not implemented yet.";
        }

        if (ruleset.Roll.ChoiceCount != 1)
        {
            return $"roll.choiceCount {ruleset.Roll.ChoiceCount} is not implemented yet (only 1).";
        }

        if (ruleset.Season.MaxActiveRunsPerPlayer != 1)
        {
            return $"season.maxActiveRunsPerPlayer {ruleset.Season.MaxActiveRunsPerPlayer} is not implemented yet (only 1).";
        }

        return null;
    }
}
