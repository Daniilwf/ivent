using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Support;
using Json.Schema;

namespace GameEvent.Engine.Tests.Rulesets;

/// <summary>
/// C1: the JSON schema of the ruleset is generated from the C# types (<see cref="RulesetSchema"/>, D-23) and committed
/// as docs/ruleset.schema.json. The admin's JSON editor checks against it.
/// To refresh the file after changing the types: set UPDATE_RULESET_SCHEMA=1 and run <see cref="Schema_is_up_to_date"/>.
/// </summary>
public class SchemaTests
{
    private const string SchemaFile = "ruleset.schema.json";
    private const string UpdateVariable = "UPDATE_RULESET_SCHEMA";

    private static string GenerateSchema() => RulesetSchema.Generate();

    private static JsonSchema SchemaFromDocs() => JsonSchema.FromText(File.ReadAllText(RepositoryPaths.Docs(SchemaFile)));

    private static EvaluationResults Evaluate(JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        return schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
    }

    private static string Describe(EvaluationResults results) =>
        string.Join(
            "\n",
            (results.Details ?? []).Where(d => d.Errors is not null)
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} {e.Value}")));

    [Fact]
    public void Schema_generation_is_deterministic()
    {
        var first = GenerateSchema();

        Assert.Equal(first, GenerateSchema());
        Assert.DoesNotContain('\r', first);
        Assert.EndsWith("}\n", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_is_up_to_date()
    {
        var path = RepositoryPaths.Docs(SchemaFile);
        var generated = GenerateSchema();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }

        Assert.True(File.Exists(path), $"docs/{SchemaFile} is missing. Run the tests with {UpdateVariable}=1 to generate it.");
        var committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(
            generated == committed,
            $"docs/{SchemaFile} is out of date with the Ruleset types. Run the tests with {UpdateVariable}=1 to regenerate it.");
    }

    [Fact]
    public void Default_passes_schema()
    {
        var results = Evaluate(SchemaFromDocs(), File.ReadAllText(RepositoryPaths.Docs("ruleset.default.json")));

        Assert.True(results.IsValid, Describe(results));
    }

    [Fact]
    public void Pinned_test_ruleset_passes_schema()
    {
        var results = Evaluate(SchemaFromDocs(), TestRuleset.Json);

        Assert.True(results.IsValid, Describe(results));
    }

    [Fact]
    public void Optional_field_may_be_left_out()
    {
        // Given the default ruleset without the optional grantEvent of an extreme die
        var json = Mutate(root => root["reward"]!["dieByDifficulty"]!["extreme"]!.AsObject().Remove("grantEvent"));

        var results = Evaluate(SchemaFromDocs(), json);

        Assert.True(results.IsValid, Describe(results));
    }

    public static TheoryData<string, Action<JsonObject>> BrokenRulesets() => new()
    {
        { "unknown top-level field", root => root["featurez"] = new JsonObject() },
        { "unknown nested field", root => root["reward"]!["diceCont"] = 3 },
        { "typo in a field name", root => Rename(root["reward"]!["diceCount"]!.AsObject(), "hoursPerDie", "hoursPerDice") },
        { "missing required field", root => root["map"]!.AsObject().Remove("linearLength") },
        { "unknown rounding", root => root["reward"]!["diceCount"]!["rounding"] = "up" },
        { "unknown map mode", root => root["features"]!["mapMode"] = "hex" },
        { "unknown tiebreaker in a list", root => root["ranking"]!["tiebreakers"]!.AsArray().Add("coinFlip") },
        { "number as a string", root => root["reward"]!["diceCount"]!["max"] = "10" },
        { "number as a string in a list", root => root["finish"]!["bonusByOrder"]![0] = "10" },
        { "unknown field in a list item", root => root["bets"]!["payoutByHoursPerDay"]![0]!["upTO"] = 1 },
        { "flag as a string", root => root["features"]!["shop"] = "false" },
        { "enum as a number", root => root["reward"]!["unmetConditionPolicy"] = 0 },
    };

    [Theory]
    [MemberData(nameof(BrokenRulesets))]
    public void Ruleset_with_a_typo_or_unknown_value_fails_schema(string what, Action<JsonObject> breakIt)
    {
        _ = what;
        var json = Mutate(breakIt);

        var results = Evaluate(SchemaFromDocs(), json);

        Assert.False(results.IsValid, $"The schema accepted a ruleset with {what}.");
    }

    [Theory]
    [MemberData(nameof(BrokenRulesets))]
    public void Generated_schema_rejects_the_same_typos(string what, Action<JsonObject> breakIt)
    {
        // The generated schema itself (not only the committed file) must be strict: no unknown fields, closed enums
        _ = what;
        var schema = JsonSchema.FromText(GenerateSchema());

        var results = Evaluate(schema, Mutate(breakIt));

        Assert.False(results.IsValid, $"The generated schema accepted a ruleset with {what}.");
    }

    internal static string Mutate(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(RulesetJson.DefaultJson())!.AsObject();
        change(root);
        return root.ToJsonString();
    }

    private static void Rename(JsonObject obj, string from, string to)
    {
        var value = obj[from];
        obj.Remove(from);
        obj[to] = value;
    }
}
