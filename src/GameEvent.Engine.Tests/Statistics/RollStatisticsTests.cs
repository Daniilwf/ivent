using GameEvent.Engine.Runs;
using GameEvent.Engine.Statistics;

namespace GameEvent.Engine.Tests.Statistics;

public sealed class RollStatisticsTests
{
    [Fact]
    public void Face_replacements_extend_the_range_but_total_modifiers_do_not()
    {
        var mods = new RunDiceMods
        {
            SetOne = 3,
            Replaced = [new(5, 7)],
            ExtraDice = [new(6, 5)],
            Added = 500,
            Multiplier = 2,
        };

        // With no dice by hours, SetOne applies to the challenge die before any extra dice.
        var measurement = RollStatistics.Measure([], [new(4, 4)], mods);

        Assert.NotNull(measurement);
        Assert.Equal(new(10, 4, 10), measurement);
        Assert.Equal(1m, measurement.Luck);
    }

    [Fact]
    public void A_fully_fixed_throw_is_measured_but_has_no_relative_luck()
    {
        var mods = new RunDiceMods { Average = true };

        var measurement = RollStatistics.Measure([new(4, 4)], [new(6, 6)], mods);

        Assert.NotNull(measurement);
        Assert.Equal(new(5, 5, 5), measurement);
        Assert.Null(measurement.Luck);
        Assert.False(measurement.HasLuck);
        Assert.Throws<InvalidOperationException>(() => measurement.CompareLuckTo(new(2, 1, 4)));
        Assert.Null(RollStatistics.Measure([], []));
    }

    [Fact]
    public void Luck_comparison_uses_exact_fractions_before_display_rounding()
    {
        var larger = new RollMeasurement(long.MaxValue - 1, 1, long.MaxValue);
        var smaller = new RollMeasurement(long.MaxValue - 2, 1, long.MaxValue - 1);

        Assert.Equal(larger.Luck, smaller.Luck);
        Assert.True(larger.CompareLuckTo(smaller) > 0);
        Assert.True(smaller.CompareLuckTo(larger) < 0);
        Assert.Equal(0, new RollMeasurement(2, 1, 4).CompareLuckTo(new(4, 2, 8)));
    }
}
