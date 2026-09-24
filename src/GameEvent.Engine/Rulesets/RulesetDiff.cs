using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Rulesets;

/// <summary>
/// One changed value between two versions of the rules: the JSON path (for example <c>reward.diceCount.max</c>)
/// and the values before and after as JSON text (a JSON null is the text <c>"null"</c>); null when the field
/// did not exist on that side.
/// </summary>
public sealed record RulesetChange(string Path, string? Before, string? After);

/// <summary>«Было/стало» for the rules history page (C3): leaf-level differences, ordered by path.</summary>
public static class RulesetDiff
{
    public static IReadOnlyList<RulesetChange> Between(Ruleset before, Ruleset after)
    {
        var changes = new List<RulesetChange>();
        Compare("", Slot.Of(Node(before)), Slot.Of(Node(after)), changes);
        return [.. changes.OrderBy(c => c.Path, StringComparer.Ordinal)];
    }

    private static JsonNode? Node(Ruleset ruleset) => JsonSerializer.SerializeToNode(ruleset, EngineJson.Options);

    private static void Compare(string path, Slot before, Slot after, List<RulesetChange> changes)
    {
        if (before.Node is JsonObject || after.Node is JsonObject)
        {
            var b = before.Node as JsonObject;
            var a = after.Node as JsonObject;
            var keys = (b?.Select(p => p.Key) ?? []).Union(a?.Select(p => p.Key) ?? []);
            foreach (var key in keys)
            {
                Compare(Join(path, key), Slot.Child(b, key), Slot.Child(a, key), changes);
            }

            return;
        }

        if (before.Node is JsonArray || after.Node is JsonArray)
        {
            var b = before.Node as JsonArray;
            var a = after.Node as JsonArray;
            for (var i = 0; i < Math.Max(b?.Count ?? 0, a?.Count ?? 0); i++)
            {
                Compare($"{path}[{i}]", Slot.Item(b, i), Slot.Item(a, i), changes);
            }

            return;
        }

        if (before.Present != after.Present || !JsonNode.DeepEquals(before.Node, after.Node))
        {
            changes.Add(new RulesetChange(path, before.Text, after.Text));
        }
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    /// <summary>A value that may be absent (not the same as present and JSON null).</summary>
    private readonly record struct Slot(bool Present, JsonNode? Node)
    {
        public string? Text => Present ? Node?.ToJsonString(EngineJson.Options) ?? "null" : null;

        public static Slot Of(JsonNode? node) => new(true, node);

        public static Slot Child(JsonObject? node, string key) =>
            node is not null && node.TryGetPropertyValue(key, out var value) ? new(true, value) : default;

        public static Slot Item(JsonArray? node, int index) =>
            node is not null && index < node.Count ? new(true, node[index]) : default;
    }
}
