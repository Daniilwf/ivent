using System.Collections.Frozen;
using System.Reflection;

namespace GameEvent.Engine.Kernel;

/// <summary>All event types of the engine by their stable names.</summary>
public static class EventCatalog
{
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
        eventType.GetCustomAttribute<EventTypeAttribute>()
        ?? throw new InvalidOperationException($"{eventType.FullName} has no [EventType].");
}
