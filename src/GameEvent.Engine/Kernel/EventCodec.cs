using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameEvent.Engine.Kernel;

/// <summary>An event as stored in the log: stable type name, format version and JSON data.</summary>
public sealed record StoredEvent(string Type, int Version, string Data);

/// <summary>
/// Encodes events for the log and decodes them back. Events in the log are never rewritten:
/// when a format changes, its version goes up and an upcaster converts older data on read.
/// </summary>
public static class EventCodec
{
    /// <summary>Upcasters by (type name, version they read); each returns data of the next version.</summary>
    private static readonly Dictionary<(string Type, int FromVersion), Func<JsonObject, JsonObject>> s_upcasters = new()
    {
        // D-116: a proof of v1 had no uploaded screenshots
        [("proof-submitted", 1)] = data =>
        {
            data["files"] = new JsonArray();
            return data;
        },

        // D-136: before the wheel was logged a roll knew only the category it picked
        [("game-rolled", 1)] = WheelOfOne,
        [("game-choice-rolled", 1)] = WheelOfOne,

        // D-121: before bug report screenshots every stored file was an upload
        [("file-stored", 1)] = data =>
        {
            data["kind"] = "upload";
            return data;
        },
    };

    private static JsonObject WheelOfOne(JsonObject data)
    {
        data["sectors"] = new JsonArray(data["category"]?.DeepClone());
        return data;
    }

    public static StoredEvent Encode(IGameEvent gameEvent)
    {
        ArgumentNullException.ThrowIfNull(gameEvent);
        var type = gameEvent.GetType();
        var description = EventCatalog.Describe(type);
        return new StoredEvent(description.Name, description.Version, JsonSerializer.Serialize(gameEvent, type, EngineJson.Options));
    }

    /// <summary>Decodes a stored event, upcasting older versions. Any unreadable record throws <see cref="JsonException"/>.</summary>
    public static IGameEvent Decode(StoredEvent stored) => Decode(stored, s_upcasters);

    internal static IGameEvent Decode(StoredEvent stored, IReadOnlyDictionary<(string Type, int FromVersion), Func<JsonObject, JsonObject>> upcasters)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(upcasters);
        Type type;
        try
        {
            type = EventCatalog.TypeOf(stored.Type);
        }
        catch (KeyNotFoundException e)
        {
            throw new JsonException($"Unknown event type '{stored.Type}'.", e);
        }

        var current = EventCatalog.Describe(type).Version;
        if (stored.Version > current)
        {
            throw new JsonException($"Event '{stored.Type}' v{stored.Version} is newer than this build (v{current}).");
        }

        var data = JsonNode.Parse(stored.Data)?.AsObject() ?? throw new JsonException($"Event '{stored.Type}' has no data.");
        for (var version = stored.Version; version < current; version++)
        {
            if (!upcasters.TryGetValue((stored.Type, version), out var upcast))
            {
                throw new JsonException($"No upcaster for event '{stored.Type}' v{version}.");
            }

            data = upcast(data);
        }

        return (IGameEvent)(data.Deserialize(type, EngineJson.Options)
            ?? throw new JsonException($"Event '{stored.Type}' data is null."));
    }
}
