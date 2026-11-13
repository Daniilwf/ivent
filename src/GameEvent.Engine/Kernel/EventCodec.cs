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

        // D-410: v2 lets «drawEvent» be null for the text of an object (with «objectId»); a v1 effect always drew an event
        [("manual-effect-created", 1)] = data => data,

        // D-121: before bug report screenshots every stored file was an upload
        [("file-stored", 1)] = data =>
        {
            data["kind"] = "upload";
            return data;
        },

        // D-208: a game deleted before a reason was required has none
        [("game-deleted", 1)] = data =>
        {
            data["reason"] = null;
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
        var type = TypeOf(stored.Type);
        var current = EventCatalog.Describe(type).Version;
        if (stored.Version > current)
        {
            throw new JsonException($"Event '{stored.Type}' v{stored.Version} is newer than this build (v{current}).");
        }

        // An event of the current version is read straight from its text; only an upcast needs the JSON tree (D-671)
        JsonObject? data = null;
        for (var version = stored.Version; version < current; version++)
        {
            if (!upcasters.TryGetValue((stored.Type, version), out var upcast))
            {
                throw new JsonException($"No upcaster for event '{stored.Type}' v{version}.");
            }

            data = upcast(data ?? Tree(stored));
        }

        var decoded = data is null
            ? JsonSerializer.Deserialize(stored.Data, type, EngineJson.Options)
            : data.Deserialize(type, EngineJson.Options);
        return (IGameEvent)(decoded ?? throw new JsonException($"Event '{stored.Type}' data is null."));
    }

    private static JsonObject Tree(StoredEvent stored) =>
        JsonNode.Parse(stored.Data)?.AsObject() ?? throw new JsonException($"Event '{stored.Type}' has no data.");

    // A method of its own, no local assigned inside try: mutation testing can mutate Decode (D-281)
    private static Type TypeOf(string name)
    {
        try
        {
            return EventCatalog.TypeOf(name);
        }
        catch (KeyNotFoundException e)
        {
            throw new JsonException($"Unknown event type '{name}'.", e);
        }
    }
}
