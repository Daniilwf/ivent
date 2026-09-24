namespace GameEvent.Engine.Kernel;

/// <summary>The only source of current time for the engine. Always UTC.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
