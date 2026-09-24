using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Rulesets;

/// <summary>A problem in a ruleset: the JSON path of the field and what is wrong, in English for logs.</summary>
public sealed record RulesetError(string Path, string Message);

/// <summary>
/// Semantic checks a JSON schema cannot express (min ≤ max, dice with at least two sides, …) and mechanics this
/// build cannot play yet (D-22, D-53). An invalid ruleset is never stored (C2).
/// </summary>
public static class RulesetValidator
{
    public static IReadOnlyList<RulesetError> Validate(Ruleset ruleset) =>
        throw new NotImplementedException("C1");

    /// <summary>The rejection for an invalid ruleset, or null when it is valid.</summary>
    internal static Decision? Check(Ruleset ruleset)
    {
        var errors = Validate(ruleset);
        return errors.Count == 0
            ? null
            : Decision.Reject(RejectionCodes.RulesetInvalid, string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
    }
}
