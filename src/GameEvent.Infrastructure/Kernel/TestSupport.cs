using GameEvent.Engine.Kernel;

namespace GameEvent.Infrastructure.Kernel;

/// <summary>A clock the test endpoints may move (E4, D-120): only in Development and Test.</summary>
public interface IAdjustableClock : IClock
{
    void Advance(TimeSpan by);

    void MoveTo(DateTimeOffset at);

    /// <summary>Back to the real time.</summary>
    void Reset();
}

/// <summary>A random source the test endpoints may seed (E4, D-120): the same seed, the same rolls and dice.</summary>
public interface IReseedableRandom : IRandomSource
{
    /// <summary>A fixed seed, or null for unpredictable randomness again.</summary>
    void Seed(int? seed);
}

/// <summary>The real time plus an offset, or a fixed moment (D-120). Registered only in Development and Test.</summary>
public sealed class ShiftableClock : IAdjustableClock
{
    private readonly Lock _lock = new();
    private TimeSpan _offset;
    private DateTimeOffset? _fixed;

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_lock)
            {
                return _fixed ?? DateTimeOffset.UtcNow + _offset;
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            if (_fixed is { } at)
            {
                _fixed = at + by;
            }
            else
            {
                _offset += by;
            }
        }
    }

    public void MoveTo(DateTimeOffset at)
    {
        lock (_lock)
        {
            _fixed = at.ToUniversalTime();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _fixed = null;
            _offset = TimeSpan.Zero;
        }
    }
}

/// <summary>Cryptographic randomness until a seed is set (D-120). Registered only in Development and Test.</summary>
public sealed class ReseedableRandom : IReseedableRandom
{
    private readonly Lock _lock = new();
    private readonly CryptoRandomSource _crypto = new();
    private Random? _seeded;

    public int NextInt(int minInclusive, int maxExclusive)
    {
        lock (_lock)
        {
#pragma warning disable CA5394 // a seeded sequence is the point of the test endpoint; production never registers this class
            return _seeded?.Next(minInclusive, maxExclusive) ?? _crypto.NextInt(minInclusive, maxExclusive);
#pragma warning restore CA5394
        }
    }

    public void Seed(int? seed)
    {
        lock (_lock)
        {
            _seeded = seed is { } value ? new Random(value) : null;
        }
    }
}
