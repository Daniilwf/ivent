using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rulesets;

/// <summary>
/// C2: reading a ruleset from JSON is strict. A typo must not silently turn into a default value:
/// unknown fields, missing required fields, unknown enum values and numbers written as strings are refused,
/// and the error names the offending field so the admin can find it.
/// </summary>
public class ParseTests
{
    [Fact]
    public void Default_json_parses()
    {
        var ruleset = RulesetJson.Parse(RulesetJson.DefaultJson());

        Assert.Equal(RulesetJson.Default(), ruleset);
    }

    [Fact]
    public void Optional_field_left_out_reads_as_null()
    {
        var json = SchemaTests.Mutate(root => root["reward"]!["dieByDifficulty"]!["extreme"]!.AsObject().Remove("grantEvent"));

        var ruleset = RulesetJson.Parse(json);

        Assert.Null(ruleset.Reward.DieByDifficulty.Extreme.GrantEvent);
    }

    [Fact]
    public void Ruleset_without_max_unchecked_runs_has_no_limit()
    {
        // D-134: the field came later and is optional, so the seasons created before it keep working without a limit
        var json = SchemaTests.Mutate(root => root["season"]!.AsObject().Remove("maxUncheckedRuns"));

        var ruleset = RulesetJson.Parse(json);

        Assert.Null(ruleset.Season.MaxUncheckedRuns);
    }

    [Fact]
    public void Explicit_null_max_unchecked_runs_means_no_limit()
    {
        var json = SchemaTests.Mutate(root => root["season"]!["maxUncheckedRuns"] = null);

        Assert.Null(RulesetJson.Parse(json).Season.MaxUncheckedRuns);
    }

    [Fact]
    public void Max_unchecked_runs_is_read_from_the_json()
    {
        var json = SchemaTests.Mutate(root => root["season"]!["maxUncheckedRuns"] = 5);

        Assert.Equal(5, RulesetJson.Parse(json).Season.MaxUncheckedRuns);
    }

    [Fact]
    public void Pinned_test_ruleset_has_no_unchecked_limit()
    {
        // The pinned ruleset has no such field: mechanics tests are not limited unless they ask for it
        Assert.Null(TestRuleset.Create().Season.MaxUncheckedRuns);
    }

    public static TheoryData<string, Action<JsonObject>, string> BrokenRulesets() => new()
    {
        { "unknown top-level field", root => root["featurez"] = new JsonObject(), "featurez" },
        { "unknown nested field", root => root["reward"]!["diceCont"] = 3, "diceCont" },
        {
            "typo in a field name",
            root =>
            {
                var diceCount = root["reward"]!["diceCount"]!.AsObject();
                var value = diceCount["hoursPerDie"];
                diceCount.Remove("hoursPerDie");
                diceCount["hoursPerDice"] = value;
            },
            "hoursPerDice"
        },
        { "missing required field", root => root["map"]!.AsObject().Remove("linearLength"), "linearLength" },
        { "missing required section", root => root.Remove("drop"), "drop" },
        { "unknown rounding", root => root["reward"]!["diceCount"]!["rounding"] = "up", "rounding" },
        { "unknown tiebreaker in a list", root => root["ranking"]!["tiebreakers"]!.AsArray().Add("coinFlip"), "tiebreakers" },
        { "enum as a number", root => root["reward"]!["unmetConditionPolicy"] = 0, "unmetConditionPolicy" },
        { "number as a string", root => root["reward"]!["diceCount"]!["max"] = "10", "max" },
        { "number as a string in a list", root => root["finish"]!["bonusByOrder"]![0] = "10", "bonusByOrder" },
        { "unknown field in a list item", root => root["bets"]!["payoutByHoursPerDay"]![0]!["upTO"] = 1, "upTO" },
        { "decimal as a string", root => root["reward"]!["coop"]!["pointsShare"] = "0.5", "pointsShare" },
        { "flag as a string", root => root["features"]!["shop"] = "false", "shop" },
        { "null for a required number", root => root["map"]!["linearLength"] = null, "linearLength" },
        { "unchecked limit as a string", root => root["season"]!["maxUncheckedRuns"] = "2", "maxUncheckedRuns" },
    };

    [Theory]
    [MemberData(nameof(BrokenRulesets))]
    public void Broken_ruleset_is_refused_naming_the_field(string what, Action<JsonObject> breakIt, string field)
    {
        var json = SchemaTests.Mutate(breakIt);

        var error = Assert.ThrowsAny<JsonException>(() => RulesetJson.Parse(json));

        Assert.True(
            error.Message.Contains(field, StringComparison.Ordinal),
            $"Parsing a ruleset with {what} failed, but the error does not name '{field}': {error.Message}");
    }

    [Fact]
    public void Json_null_is_refused()
    {
        Assert.ThrowsAny<JsonException>(() => RulesetJson.Parse("null"));
    }
}
