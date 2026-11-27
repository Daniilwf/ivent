using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;

namespace GameEvent.Engine.Statistics;

/// <summary>The sum of the final faces and its position in the roll's possible range (D-927).</summary>
public sealed record RollMeasurement(long Sum, long Minimum, long Maximum)
{
    public long Numerator => Sum - Minimum;

    public long Denominator => Maximum - Minimum;

    public bool HasLuck => Denominator > 0;

    public decimal? Luck => HasLuck ? (decimal)Numerator / Denominator : null;

    /// <summary>Compares luck without rounding the two fractions.</summary>
    public int CompareLuckTo(RollMeasurement other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!HasLuck || !other.HasLuck)
        {
            throw new InvalidOperationException("A fixed roll has no relative luck.");
        }

        return ((Int128)Numerator * other.Denominator).CompareTo((Int128)other.Numerator * Denominator);
    }
}

public static class RollStatistics
{
    /// <summary>Measures the saved faces after face modifiers. Additions and other modifiers of the total are excluded.</summary>
    public static RollMeasurement? Measure(
        EquatableArray<Die> dice, EquatableArray<Die> challengeDice, RunDiceMods? mods = null)
    {
        var saved = mods ?? RunDiceMods.None;
        var replacementRanges = saved.Replaced.Count > 0 ? new Dictionary<int, (int Minimum, int Maximum)>() : null;
        var first = true;
        long sum = 0;
        long minimum = 0;
        long maximum = 0;
        // Same dice order as RunTotal, including which die receives SetOne.
        foreach (var die in dice.Concat(challengeDice).Concat(saved.ExtraDice))
        {
            var value = saved.Face(die, first);
            var (floor, ceiling) = saved.Average || (first && saved.SetOne is not null)
                ? (value, value)
                : replacementRanges is null ? (1, die.Sides) : ReplacementRange(saved, die.Sides, replacementRanges);
            sum += value;
            minimum += floor;
            maximum += ceiling;
            first = false;
        }

        return first ? null : new(sum, minimum, maximum);
    }

    private static (int Minimum, int Maximum) ReplacementRange(
        RunDiceMods mods, int sides, Dictionary<int, (int Minimum, int Maximum)> ranges)
    {
        if (ranges.TryGetValue(sides, out var range))
        {
            return range;
        }

        var minimum = int.MaxValue;
        var maximum = int.MinValue;
        for (var face = 1; face <= sides; face++)
        {
            var replaced = mods.Face(new Die(sides, face), first: false);
            minimum = Math.Min(minimum, replaced);
            maximum = Math.Max(maximum, replaced);
        }

        return ranges[sides] = (minimum, maximum);
    }
}
