using GameEvent.Infrastructure.Kernel;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// The host's time zone database the queue gives the engine (D-579): the season's calendar of the default ruleset resolves,
/// so the suggestion box counts its days in Moscow and not in UTC.
/// </summary>
public class SystemTimeZonesTests
{
    [Fact]
    public void Moscow_resolves_three_hours_ahead_of_utc()
    {
        Assert.Equal(TimeSpan.FromHours(3), SystemTimeZones.Instance.OffsetAt("Europe/Moscow", new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void An_unknown_zone_is_none()
    {
        Assert.Null(SystemTimeZones.Instance.OffsetAt("Mars/Olympus", DateTimeOffset.UnixEpoch));
    }
}
