using System.Collections.Frozen;
using GameEvent.Engine.Content;

namespace GameEvent.Engine.Tests.Content;

public partial class ContentExamplesTests
{
    /// <summary>The objects of docs/CONTENT.md by id (the last sample of an id wins), for the acceptance tests of stages 4–6.</summary>
    /// <remarks>Parsed once per test run (D-670): every random game of the suspended events starts from these.</remarks>
    internal static IReadOnlyDictionary<string, ObjectDefinition> ExampleObjects() => s_exampleObjects.Value;

    private static readonly Lazy<FrozenDictionary<string, ObjectDefinition>> s_exampleObjects = new(() =>
        s_examples.Value.Where(j => Classify(j) == "object")
            .Select(ContentJson.Parse<ObjectDefinition>)
            .GroupBy(o => o.Id)
            .ToFrozenDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal));

    /// <summary>
    /// The ids of «Примеры расширений формата» (the import of the old pack): content files as they are, kept apart from
    /// the examples 1–10 the acceptance tests of stage 4 publish.
    /// </summary>
    internal static IReadOnlySet<string> ExtensionExampleIds() => s_extensionExampleIds.Value;

    private static readonly Lazy<FrozenSet<string>> s_extensionExampleIds = new(() =>
    {
        var doc = s_docText.Value;
        var start = doc.IndexOf("## Примеры расширений формата", StringComparison.Ordinal);
        var end = doc.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return JsonBlock().Matches(doc[start..end])
            .Select(m => ContentJson.Parse<ObjectDefinition>(m.Groups["json"].Value).Id)
            .ToFrozenSet(StringComparer.Ordinal);
    });
}
