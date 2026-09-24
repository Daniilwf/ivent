using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;

namespace GameEvent.Engine.Kernel;

/// <summary>
/// The one JSON format of the engine: rulesets, event data in the log, state snapshots.
/// camelCase property names, enums as camelCase strings, nulls written explicitly (D-48).
/// Dictionary keys are data and are written as is.
/// </summary>
public static class EngineJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            // Cyrillic names stay readable in the log; HTML-sensitive characters are still escaped.
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
}
