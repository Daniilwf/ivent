using System.Text.Json;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Rulesets;

/// <summary>Reads rulesets from JSON. The default ruleset is docs/ruleset.default.json, embedded into the engine.</summary>
public static class RulesetJson
{
    private const string DefaultResource = "ruleset.default.json";

    public static Ruleset Parse(string json) =>
        JsonSerializer.Deserialize<Ruleset>(json, EngineJson.Options)
        ?? throw new JsonException("Ruleset JSON is null.");

    public static string DefaultJson()
    {
        using var stream = typeof(RulesetJson).Assembly.GetManifestResourceStream(DefaultResource)
            ?? throw new InvalidOperationException($"Embedded resource {DefaultResource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Ruleset Default() => Parse(DefaultJson());
}
