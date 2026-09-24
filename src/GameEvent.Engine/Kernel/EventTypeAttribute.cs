namespace GameEvent.Engine.Kernel;

/// <summary>
/// Stable name and format version of an event type in the log. The name never changes;
/// a format change bumps the version and adds an upcast on read.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventTypeAttribute(string name, int version = 1) : Attribute
{
    public string Name { get; } = name;

    public int Version { get; } = version;
}
