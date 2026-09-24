using System.Text.Json;
using System.Text.Json.Serialization;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Rulesets;

/// <summary>Reads rulesets from JSON. The default ruleset is docs/ruleset.default.json, embedded into the engine.</summary>
public static class RulesetJson
{
    private const string DefaultResource = "ruleset.default.json";

    /// <summary>
    /// The engine format, strict about unknown fields: a typo in an admin's config is an error, not a silently
    /// ignored field. Rulesets already in the log are read with the lenient <see cref="EngineJson.Options"/>.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(EngineJson.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
        };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Parses and type-checks a ruleset; <see cref="JsonException"/> names the offending field.</summary>
    public static Ruleset Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Ruleset>(json, Options)
                ?? throw new JsonException("Ruleset JSON is null.");
        }
        catch (JsonException e) when (e.Path is { } path && !e.Message.Contains(path, StringComparison.Ordinal))
        {
            // Errors inside lists carry the field only in Path; put it into the message the admin sees.
            throw new JsonException($"{path}: {Plain(e.Message)}", path, e.LineNumber, e.BytePositionInLine, e);
        }
    }

    // System.Text.Json names .NET types ("GameEvent.Engine.Rulesets.DiceCountRule"); the admin needs the field only.
    private static string Plain(string message) =>
        System.Text.RegularExpressions.Regex.Replace(message, @"GameEvent\.Engine\.[A-Za-z.]+\.", "", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    public static string DefaultJson()
    {
        using var stream = typeof(RulesetJson).Assembly.GetManifestResourceStream(DefaultResource)
            ?? throw new InvalidOperationException($"Embedded resource {DefaultResource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Ruleset Default() => Parse(DefaultJson());
}
