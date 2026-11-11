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

    private static JsonNode Node(Type type)
    {
        var node = ContentJson.Options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Describe,
        });

        // A type met twice gets a «$ref» relative to this node; the node is embedded in a bigger schema, so the reference
        // is replaced by a copy of what it points at (a use condition and an effect's condition, D-483).
        return Inline(node, node);
    }

    private static JsonNode Inline(JsonNode node, JsonNode root)
    {
        switch (node)
        {
            case JsonObject { Count: 1 } reference when reference["$ref"] is JsonValue pointer && ((string)pointer!).StartsWith("#/", StringComparison.Ordinal):
                var target = ((string)pointer!)[2..].Split('/').Aggregate((JsonNode?)root, (at, part) => at?[part]);
                return Inline(target!.DeepClone(), root);
            case JsonObject properties:
                foreach (var name in properties.Select(p => p.Key).ToList())
                {
                    if (properties[name] is { } child)
                    {
                        properties[name] = Inline(child, root) is var inlined && ReferenceEquals(inlined, child) ? child : inlined;
                    }
                }

                return node;
            case JsonArray items:
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is { } child && Inline(child, root) is var inlined && !ReferenceEquals(inlined, child))
                    {
                        items[i] = inlined;
                    }
                }

                return node;
            default:
                return node;
        }
    }

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
