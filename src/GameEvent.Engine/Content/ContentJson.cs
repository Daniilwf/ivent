using System.Text.Json;
using System.Text.Json.Serialization;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Content;

/// <summary>
/// Reads content (CONTENT.md): the engine format, strict about unknown fields like the ruleset (D-23), so a typo in an
/// admin's JSON is an error and not a silently ignored field (D-103).
/// </summary>
public static class ContentJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(EngineJson.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            AllowOutOfOrderMetadataProperties = true,
        };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Parses and type-checks one piece of content; <see cref="JsonException"/> names the offending field.</summary>
    public static T Parse<T>(string json)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var content = JsonSerializer.Deserialize<T>(json, Options);
            return content is null ? throw new JsonException("Content JSON is null.") : content;
        }
        catch (JsonException e) when (e.Path is { } path && !e.Message.Contains(path, StringComparison.Ordinal))
        {
            throw new JsonException($"{path}: {e.Message}", path, e.LineNumber, e.BytePositionInLine, e);
        }
    }

    public static string Write<T>(T content) => JsonSerializer.Serialize(content, Options);
}
