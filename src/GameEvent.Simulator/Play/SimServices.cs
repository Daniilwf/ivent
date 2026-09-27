using GameEvent.Engine.Kernel;

namespace GameEvent.Simulator.Play;

/// <summary>The simulated time: moves only when the simulation takes the next event of its queue.</summary>
public sealed class SimClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public void MoveTo(DateTimeOffset at)
    {
        if (at < UtcNow)
        {
            throw new InvalidOperationException($"The simulated clock cannot go back from {UtcNow:O} to {at:O}.");
        }

        UtcNow = at;
    }
}

/// <summary>Sequential ids with a prefix: the same season log for the same seed.</summary>
public sealed class SimIds(uint prefix) : IIdGenerator
{
    private long _next;

    public Guid NewId() => Make(prefix, ++_next);

    public static Guid Make(uint prefix, long number)
    {
        Span<byte> tail = stackalloc byte[8];
        BitConverter.TryWriteBytes(tail, number);
        return new Guid((int)prefix, 0, 0, tail.ToArray());
    }
}
