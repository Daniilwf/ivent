using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Tests.Support;

/// <summary>Clock that moves only when the test says so.</summary>
public sealed class FixedClock(DateTimeOffset start) : IClock
{
    public static readonly DateTimeOffset SeasonStart = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// Returns scripted values first (checked against the requested range), then values from a seeded generator.
/// Tests script only what they care about, for example the dice of the next completion.
/// </summary>
public sealed class ScriptedRandom(int seed = 42) : IRandomSource
{
    private readonly Queue<int> _scripted = new();
    private readonly Random _fallback = new(seed);

    public int ScriptedLeft => _scripted.Count;

    public void Enqueue(params int[] values)
    {
        foreach (var value in values)
        {
            _scripted.Enqueue(value);
        }
    }

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (_scripted.Count == 0)
        {
            return _fallback.Next(minInclusive, maxExclusive);
        }

        var value = _scripted.Dequeue();
        if (value < minInclusive || value >= maxExclusive)
        {
            throw new InvalidOperationException(
                $"Scripted value {value} is outside the requested range [{minInclusive}, {maxExclusive}).");
        }

        return value;
    }
}

/// <summary>Deterministic ids: 00000000-0000-0000-0000-000000000001, …002, … with an optional prefix in the first group.</summary>
public sealed class SequentialIds(uint prefix = 0) : IIdGenerator
{
    private long _next;

    public Guid NewId() => Make(prefix, ++_next);

    public static Guid Make(uint prefix, long number) =>
        Guid.Parse($"{prefix:x8}-0000-0000-0000-{number:x12}");
}
