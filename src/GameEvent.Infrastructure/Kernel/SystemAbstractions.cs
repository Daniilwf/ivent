using System.Security.Cryptography;
using GameEvent.Engine.Kernel;

namespace GameEvent.Infrastructure.Kernel;

/// <summary>Wall clock in UTC. Tests and the Test environment substitute a controllable clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Cryptographic randomness: players cannot predict dice or the wheel.</summary>
public sealed class CryptoRandomSource : IRandomSource
{
    public int NextInt(int minInclusive, int maxExclusive) => RandomNumberGenerator.GetInt32(minInclusive, maxExclusive);
}

/// <summary>Time-ordered ids (UUID v7), so rows inserted together sit together in indexes.</summary>
public sealed class GuidV7Ids : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}

/// <summary>
/// The system's time zone database (IANA names; .NET reads tzdata on Linux and ICU on Windows) for the season's calendar
/// (D-579). The engine itself reads no system data.
/// </summary>
public sealed class SystemTimeZones : ITimeZones
{
    public static readonly SystemTimeZones Instance = new();

    public TimeSpan? OffsetAt(string zoneId, DateTimeOffset moment) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone) ? zone.GetUtcOffset(moment) : null;
}
