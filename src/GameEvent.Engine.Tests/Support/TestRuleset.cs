using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Tests.Support;

/// <summary>
/// Ruleset pinned for tests. Tests of mechanics state the numbers they rely on here, so rebalancing
/// docs/ruleset.default.json does not break them. That the default ruleset parses is tested separately.
/// </summary>
public static class TestRuleset
{
    public const string Json = """
        {
          "version": 1,
          "features": { "mapMode": "linear" },
          "season": { "timezone": "Europe/Moscow", "maxActiveRunsPerPlayer": 1 },
          "roll": { "choiceCount": 1, "freeRerollsPerRoll": 1, "techRerollWindowHours": 48 },
          "reward": {
            "diceCount": { "hoursPerDie": 3, "rounding": "nearest", "min": 1, "max": 10 },
            "dieByDifficulty": {
              "easy": { "sides": 2 },
              "normal": { "sides": 4 },
              "hard": { "sides": 6 },
              "extreme": { "sides": 6, "grantEvent": "good" }
            }
          },
          "map": { "linearLength": 60 }
        }
        """;

    public static Ruleset Create() => RulesetJson.Parse(Json);
}
