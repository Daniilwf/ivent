using GameEvent.Engine.Kernel;

namespace GameEvent.Infrastructure.Kernel;

/// <summary>
/// A clock the test endpoints may move (E4, D-120): only in Development and Test. The time keeps running after a move; a
/// move too far from the real time throws and changes nothing.
/// </summary>
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

/// <summary>
/// The real time plus an offset (D-120): a move to a moment is an offset too, so the time runs on from there and the
/// scheduler meets the deadlines as it would. Registered only in Development and Test.
/// </summary>
public sealed class ShiftableClock : IAdjustableClock
{
    /// <summary>How far from the real time the clock may go: far enough for any season, never near the calendar's end.</summary>
    public static readonly TimeSpan MaxOffset = TimeSpan.FromDays(100 * 365);

    private readonly Lock _lock = new();
    private TimeSpan _offset;

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_lock)
            {
                return DateTimeOffset.UtcNow + _offset;
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _offset = Checked(by.Duration() > MaxOffset ? TimeSpan.MaxValue : _offset + by);
        }
    }

    public void MoveTo(DateTimeOffset at)
    {
        lock (_lock)
        {
            _offset = Checked(at.ToUniversalTime() - DateTimeOffset.UtcNow);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _offset = TimeSpan.Zero;
        }
    }

    private static TimeSpan Checked(TimeSpan offset) =>
        offset.Duration() <= MaxOffset ? offset : throw new ArgumentOutOfRangeException(nameof(offset), "The clock goes at most 100 years from the real time.");
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
