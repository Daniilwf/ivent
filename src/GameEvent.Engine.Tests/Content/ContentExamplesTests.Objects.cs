using GameEvent.Engine.Content;

namespace GameEvent.Engine.Tests.Content;

public partial class ContentExamplesTests
{
    /// <summary>The objects of docs/CONTENT.md by id (the last sample of an id wins), for the acceptance tests of stages 4–6.</summary>
    internal static IReadOnlyDictionary<string, ObjectDefinition> ExampleObjects() =>
        s_examples.Value.Where(j => Classify(j) == "object")
            .Select(ContentJson.Parse<ObjectDefinition>)
            .GroupBy(o => o.Id)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
}
