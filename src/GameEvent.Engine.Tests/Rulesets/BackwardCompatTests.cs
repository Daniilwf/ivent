using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rulesets;

/// <summary>
/// C4 / D-50: rulesets stored in old events (season-created, ruleset-changed) must keep reading after the types grow.
/// A field added later must be optional, otherwise <c>RespectRequiredConstructorParameters</c> and <c>required</c>
/// members refuse the old JSON. The guard walks the ruleset types and every field missing from a reference JSON
/// must be optional.
/// </summary>
public class BackwardCompatTests
{
    /// <summary>
    /// The ruleset format as of the first release. Until the release formats may change freely (D-73), so this may
    /// follow docs/ruleset.default.json; after it, this text is frozen and new fields go only to the types.
    /// </summary>
    // Frozen copy of the format-1 ruleset (D-73: frozen before the first release). Never edit it: a new format gets
    // its own file. docs/ruleset.default.json may change; this one may not.
    private static readonly string s_releaseBaseline = File.ReadAllText(
        Path.Combine(RepositoryPaths.Root(), "src", "GameEvent.Engine.Tests", "Rulesets", "Baselines", "ruleset.format1.json"));

    public static TheoryData<string> ReferenceJsons() => new()
    {
        "release baseline",
        "docs default",
        "pinned test ruleset",
    };

    private static string Reference(string name) => name switch
    {
        "release baseline" => s_releaseBaseline,
        "docs default" => File.ReadAllText(RepositoryPaths.Docs("ruleset.default.json")),
        _ => TestRuleset.Json,
    };

    [Theory]
    [MemberData(nameof(ReferenceJsons))]
    public void Every_field_missing_from_the_reference_json_is_optional(string reference)
    {
        var root = JsonNode.Parse(Reference(reference))!.AsObject();
        var violations = new List<string>();

        CollectRequiredButMissing(typeof(Ruleset), root, path: "", violations);

        Assert.True(
            violations.Count == 0,
            $"Fields missing from the {reference} JSON must be optional (D-50): {string.Join(", ", violations)}");
    }

    [Fact]
    public void Guard_notices_a_required_field_missing_from_the_json()
    {
        // The guard itself works: dropping a required field from the reference is reported
        var root = JsonNode.Parse(RulesetJson.DefaultJson())!.AsObject();
        root["reward"]!["diceCount"]!.AsObject().Remove("max");
        var violations = new List<string>();

        CollectRequiredButMissing(typeof(Ruleset), root, path: "", violations);

        Assert.Equal(["reward.diceCount.max"], violations);
    }

    [Fact]
    public void Season_created_event_with_the_release_ruleset_reads_back()
    {
        var stored = EventCodec.Encode(new SeasonCreated(
            SequentialIds.Make(0x30000000, 1), "Тестовый сезон", RulesetJson.Parse(s_releaseBaseline), new MapGraph([], []), null));

        var read = Assert.IsType<SeasonCreated>(EventCodec.Decode(stored));

        Assert.Equal(RulesetJson.Parse(s_releaseBaseline), read.Ruleset);
    }

    [Fact]
    public void Ruleset_without_an_optional_field_reads_inside_an_event()
    {
        // An older ruleset-changed record where the extreme die has no grantEvent at all
        var ruleset = JsonNode.Parse(s_releaseBaseline)!.AsObject();
        ruleset["reward"]!["dieByDifficulty"]!["extreme"]!.AsObject().Remove("grantEvent");
        var data = new JsonObject { ["version"] = 2, ["ruleset"] = ruleset };
        var stored = new StoredEvent("ruleset-changed", EventCatalog.Describe(typeof(RulesetChanged)).Version, data.ToJsonString());

        var read = Assert.IsType<RulesetChanged>(EventCodec.Decode(stored));

        Assert.Equal(2, read.Version);
        Assert.Null(read.Ruleset.Reward.DieByDifficulty.Extreme.GrantEvent);
    }

    /// <summary>Walks <paramref name="type"/> by its JSON contract and reports required properties absent from <paramref name="node"/>.</summary>
    private static void CollectRequiredButMissing(Type type, JsonObject node, string path, List<string> violations)
    {
        var info = EngineJson.Options.GetTypeInfo(type);
        foreach (var property in info.Properties)
        {
            var propertyPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
            if (!node.TryGetPropertyValue(property.Name, out var value))
            {
                if (property.IsRequired)
                {
                    violations.Add(propertyPath);
                }

                continue;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            switch (value)
            {
                case JsonObject child when IsObject(propertyType):
                    CollectRequiredButMissing(propertyType, child, propertyPath, violations);
                    break;
                case JsonArray items when ElementType(propertyType) is { } elementType && IsObject(elementType):
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (items[i] is JsonObject item)
                        {
                            CollectRequiredButMissing(elementType, item, $"{propertyPath}[{i}]", violations);
                        }
                    }

                    break;
            }
        }
    }

    private static bool IsObject(Type type) =>
        EngineJson.Options.GetTypeInfo(type).Kind == JsonTypeInfoKind.Object;

    private static Type? ElementType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(EquatableArray<>) ? type.GetGenericArguments()[0] : null;
}
