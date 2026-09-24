using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GameEvent.Engine.Kernel;

/// <summary>
/// The one JSON format of the engine: rulesets, events in the log, state snapshots.
/// camelCase names, enums as camelCase strings, events polymorphic by their stable type name.
/// </summary>
public static class EngineJson
{
    public const string EventTypeProperty = "$type";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string SerializeEvent(IGameEvent gameEvent) =>
        JsonSerializer.Serialize(gameEvent, Options);

    public static IGameEvent DeserializeEvent(string json) =>
        JsonSerializer.Deserialize<IGameEvent>(json, Options)
        ?? throw new JsonException("Event JSON is null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { AddEventTypes } },
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    private static void AddEventTypes(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(IGameEvent))
        {
            return;
        }

        typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = EventTypeProperty,
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
        };
        foreach (var type in EventCatalog.Types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            typeInfo.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, EventCatalog.Describe(type).Name));
        }
    }
}
