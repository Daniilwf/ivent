using System.Reflection;
using GameEvent.Web.Tests;
using Xunit.Sdk;
using Xunit.v3;

[assembly: TestCollectionOrderer(typeof(SlowFirstOrderer))]

namespace GameEvent.Web.Tests;

/// <summary>
/// Starts the classes of Slow tests first (D-666), the rest in xUnit's usual random order. A demo season takes most of a
/// minute; started last, it ran alone at the end of the suite with the other cores idle.
/// </summary>
public sealed class SlowFirstOrderer : ITestCollectionOrderer
{
    private static readonly HashSet<string> s_slowClasses = [.. typeof(SlowFirstOrderer).Assembly.GetTypes()
        .Where(t => t.GetCustomAttributes<TraitAttribute>().Any(a => a.Name == "Category" && a.Value == "Slow"))
        .Select(t => t.FullName!)];

    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : ITestCollection
    {
        var usual = DefaultTestCollectionOrderer.Instance.OrderTestCollections(testCollections);
        return [.. usual.Where(c => IsSlow(c)), .. usual.Where(c => !IsSlow(c))];
    }

    // A class without a collection of its own is its own collection, named after the class
    private static bool IsSlow(ITestCollection collection) =>
        s_slowClasses.Any(c => collection.TestCollectionDisplayName.Contains(c, StringComparison.Ordinal));
}
