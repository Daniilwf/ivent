using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Unicode;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Content;

/// <summary>
/// JSON schema of the content pack (D-411): an object definition, its effect and each of the 12 base actions (chosen by
/// <c>type</c>) with the parameters its record declares — the editor in the admin checks against it and offers the
/// actions as templates. Generated from the C# types like the ruleset's schema (D-23) and committed as
/// docs/content.schema.json; the rules beyond types stay in <see cref="ContentPublishing"/>.
/// </summary>
public static class ContentSchema
{
    private static readonly JsonSerializerOptions s_text = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string Generate() => Node(typeof(ContentPack)).ToJsonString(s_text) + "\n";

    private static JsonNode Node(Type type) =>
        ContentJson.Options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Describe,
        });

    // Types with their own converters, which the exporter sees as «any value».
    private static JsonNode Describe(JsonSchemaExporterContext context, JsonNode schema)
    {
        var type = context.TypeInfo.Type;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(EquatableArray<>))
        {
            return new JsonObject { ["type"] = "array", ["items"] = Node(type.GetGenericArguments()[0]) };
        }

        if (type == typeof(ContentValue))
        {
            return new JsonObject
            {
                ["description"] = "A whole number, dice («1d6», «-1d6») or a reference («$roll», «-$roll», «$choice», «$name»).",
                ["type"] = new JsonArray("integer", "string"),
                ["pattern"] = @"^(-?([1-9][0-9]?)?d[1-9][0-9]{0,2}|-?\$[a-zA-Z][a-zA-Z0-9]{0,39})$",
            };
        }

        if (type == typeof(ContentParamDictionary))
        {
            return new JsonObject { ["type"] = new JsonArray("object", "null"), ["additionalProperties"] = new JsonObject { ["type"] = "string" } };
        }

        return schema;
    }
}
