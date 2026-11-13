using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;

namespace GameEvent.Engine.Kernel;

/// <summary>All event types of the engine by their stable names.</summary>
public static class EventCatalog
{
    // Every event written or read asks for its name and version: the attribute is read once per type (D-671). First:
    // the catalog below is built through it.
    private static readonly ConcurrentDictionary<Type, EventTypeAttribute> s_descriptions = new();

    private static readonly FrozenDictionary<string, Type> s_byName = typeof(IGameEvent).Assembly
        .GetTypes()
        .Where(t => typeof(IGameEvent).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
        .ToFrozenDictionary(t => Describe(t).Name, t => t);

    public static IReadOnlyCollection<Type> Types => s_byName.Values;

    public static Type TypeOf(string name) =>
        s_byName.TryGetValue(name, out var type)
            ? type
            : throw new KeyNotFoundException($"Unknown event type '{name}'.");

    public static EventTypeAttribute Describe(Type eventType) =>
        s_descriptions.GetOrAdd(
            eventType,
            static t => t.GetCustomAttribute<EventTypeAttribute>() ?? throw new InvalidOperationException($"{t.FullName} has no [EventType]."));
}
