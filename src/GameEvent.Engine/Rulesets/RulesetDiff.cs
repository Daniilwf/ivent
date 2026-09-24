namespace GameEvent.Engine.Rulesets;

/// <summary>
/// One changed value between two versions of the rules: the JSON path (for example <c>reward.diceCount.max</c>)
/// and the values before and after as JSON text; null when the field did not exist on that side.
/// </summary>
public sealed record RulesetChange(string Path, string? Before, string? After);

/// <summary>«Было/стало» for the rules history page (C3): leaf-level differences in path order.</summary>
public static class RulesetDiff
{
    public static IReadOnlyList<RulesetChange> Between(Ruleset before, Ruleset after) =>
        throw new NotImplementedException("C1");
}
