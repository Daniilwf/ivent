using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Rulesets;

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
