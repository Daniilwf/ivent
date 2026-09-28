using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Unicode;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Rulesets;

/// <summary>
/// JSON schema of <see cref="Ruleset"/>, generated from the C# types with <c>JsonSchemaExporter</c> (D-23) and
/// committed as docs/ruleset.schema.json (C1). The admin's JSON editor checks against it. The text is
/// deterministic: indented, LF line ends, a trailing newline. The schema is strict: no unknown fields,
/// closed enums, list items described by their own schema.
/// </summary>
public static class RulesetSchema
{
    private static readonly JsonSerializerOptions s_text = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string Generate() => Node(typeof(Ruleset)).ToJsonString(s_text) + "\n";

    private static JsonNode Node(Type type) =>
        RulesetJson.Options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = DescribeLists,
        });

    // EquatableArray<T> has its own converter, which the exporter sees as «any value»: describe it as an array of T.
    private static JsonNode DescribeLists(JsonSchemaExporterContext context, JsonNode schema)
    {
        var type = context.TypeInfo.Type;
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(EquatableArray<>))
        {
            return schema;
        }

        return new JsonObject
        {
            ["type"] = "array",
            ["items"] = Node(type.GetGenericArguments()[0]),
        };
    }
}
