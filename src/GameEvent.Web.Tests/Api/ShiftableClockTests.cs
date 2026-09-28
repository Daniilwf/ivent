using GameEvent.Infrastructure.Kernel;

namespace GameEvent.Web.Tests.Api;

/// <summary>The Development clock (D-120): an offset from the real time that keeps running, within a hundred years.</summary>
public sealed class ShiftableClockTests
{
    [Fact]
    public void A_moved_clock_keeps_running()
    {
        var clock = new ShiftableClock();
        var moment = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

        clock.MoveTo(moment);
        var first = clock.UtcNow;
        Thread.Sleep(20);

        Assert.InRange(first, moment, moment.AddSeconds(5));
        Assert.True(clock.UtcNow > first);
    }

    [Fact]
    public void Advance_adds_up_and_reset_returns_to_the_real_time()
    {
        var clock = new ShiftableClock();

        clock.Advance(TimeSpan.FromDays(3));
        clock.Advance(TimeSpan.FromDays(4));
        Assert.InRange(clock.UtcNow - DateTimeOffset.UtcNow, TimeSpan.FromDays(7) - TimeSpan.FromSeconds(5), TimeSpan.FromDays(7) + TimeSpan.FromSeconds(5));

        clock.Reset();
        Assert.InRange(clock.UtcNow - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_clock_does_not_go_past_a_hundred_years_and_a_refused_move_changes_nothing()
    {
        var clock = new ShiftableClock();
        for (var i = 0; i < 9; i++)
        {
            clock.Advance(TimeSpan.FromDays(10 * 365));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromDays(20 * 365)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.MoveTo(DateTimeOffset.MaxValue));
        Assert.InRange(clock.UtcNow - DateTimeOffset.UtcNow, TimeSpan.FromDays(90 * 365) - TimeSpan.FromSeconds(5), TimeSpan.FromDays(90 * 365) + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_offset_follows_every_move_and_a_reset()
    {
        var clock = new ShiftableClock();
        Assert.Equal(TimeSpan.Zero, clock.Offset);

        clock.Advance(TimeSpan.FromMinutes(-30));
        Assert.Equal(TimeSpan.FromMinutes(-30), clock.Offset);

        clock.MoveTo(DateTimeOffset.UtcNow.AddDays(2));
        Assert.InRange(clock.Offset, TimeSpan.FromDays(2) - TimeSpan.FromSeconds(5), TimeSpan.FromDays(2));

        clock.Reset();
        Assert.Equal(TimeSpan.Zero, clock.Offset);
    }

    [Fact]
    public void The_randomness_tells_its_seed_and_repeats_with_it()
    {
        var random = new ReseedableRandom();
        Assert.Null(random.CurrentSeed);

        random.Seed(42);
        var first = Enumerable.Range(0, 8).Select(_ => random.NextInt(0, 1000)).ToList();
        random.Seed(42);
        Assert.Equal(42, random.CurrentSeed);
        Assert.Equal(first, Enumerable.Range(0, 8).Select(_ => random.NextInt(0, 1000)).ToList());

        random.Seed(null);
        Assert.Null(random.CurrentSeed);
    }
}
