namespace GameEvent.Engine.Kernel;

/// <summary>
/// The host's time zone database (D-579): the engine reads no system data itself, as with <see cref="IClock"/>. The season's
/// calendar (<c>season.timezone</c>) asks it for the zone's offset at a moment.
/// </summary>
public interface ITimeZones
{
    /// <summary>The offset from UTC of the IANA zone <paramref name="zoneId"/> at <paramref name="moment"/>; null for a zone it does not know.</summary>
    TimeSpan? OffsetAt(string zoneId, DateTimeOffset moment);
}
