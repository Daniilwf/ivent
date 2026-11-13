using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Seasons;

/// <summary>
/// The season's calendar (Н-20): days by <c>season.timezone</c>, the offsets given by the host (D-579). An unknown zone is
/// no day at all — the caller refuses rather than counting in UTC.
/// </summary>
public static class SeasonCalendar
{
    public static DateOnly? Day(ITimeZones? zones, string zoneId, DateTimeOffset moment) =>
        zones?.OffsetAt(zoneId, moment) is { } offset ? DateOnly.FromDateTime(moment.UtcDateTime + offset) : null;
}
