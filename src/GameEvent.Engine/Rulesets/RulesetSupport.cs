namespace GameEvent.Engine.Rulesets;

/// <summary>
/// Values this build cannot play yet (D-22, D-53): a season must not start or continue with a mechanic that is not
/// implemented. Each check goes away when its task lands: graph map — stage 2, each feature flag — its stage.
/// Several active runs need a turn per run; SPEC keeps one, the limit is in the config for later (D-91).
/// </summary>
public static class RulesetSupport
{
    public static IReadOnlyList<RulesetError> Unsupported(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        var errors = new List<RulesetError>();
        const string NotYet = "is not implemented in this build yet";

        if (ruleset.Features.MapMode != MapMode.Linear)
        {
            errors.Add(new("features.mapMode", $"'{ruleset.Features.MapMode}' {NotYet} (only linear)"));
        }

        if (ruleset.Season.MaxActiveRunsPerPlayer > 1)
        {
            errors.Add(new("season.maxActiveRunsPerPlayer", $"{ruleset.Season.MaxActiveRunsPerPlayer} {NotYet} (only 1)"));
        }

        if (ruleset.Roll.LastDaysLengthFilter.Enabled)
        {
            errors.Add(new("roll.lastDaysLengthFilter.enabled", NotYet));
        }

        var f = ruleset.Features;
        foreach (var (name, on) in new[]
        {
            ("shop", f.Shop), ("items", f.Items), ("events", f.Events), ("bets", f.Bets), ("polls", f.Polls),
            ("achievements", f.Achievements), ("weeklyChallenge", f.WeeklyChallenge), ("partnerBoard", f.PartnerBoard),
            ("reactions", f.Reactions), ("comments", f.Comments), ("gallery", f.Gallery),
        })
        {
            if (on)
            {
                errors.Add(new($"features.{name}", NotYet));
            }
        }

        return errors;
    }
}
