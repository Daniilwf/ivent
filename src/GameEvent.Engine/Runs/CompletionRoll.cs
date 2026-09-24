using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Runs;

/// <summary>
/// Dice for a completed run (D-13, D-14). Stage 1 pipeline: count from hours, sides from difficulty, roll.
/// Later stages add reroll, add, multiply and min/max modifiers from items and zones.
/// </summary>
internal static class CompletionRoll
{
    /// <summary>hours / hoursPerDie, rounded by the rule (nearest: half away from zero), then clamped to min..max.</summary>
    public static int Count(decimal hours, DiceCountRule rule)
    {
        var raw = hours / rule.HoursPerDie;
        var rounded = rule.Rounding switch
        {
            Rounding.Nearest => Math.Round(raw, MidpointRounding.AwayFromZero),
            Rounding.Floor => Math.Floor(raw),
            Rounding.Ceil => Math.Ceiling(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Rounding, "Unknown rounding."),
        };

        return (int)Math.Clamp(rounded, rule.Min, rule.Max);
    }

    public static DieRule DieFor(Difficulty difficulty, DieByDifficulty dice) =>
        difficulty switch
        {
            Difficulty.Easy => dice.Easy,
            Difficulty.Normal => dice.Normal,
            Difficulty.Hard => dice.Hard,
            Difficulty.Extreme => dice.Extreme,
            _ => throw new ArgumentOutOfRangeException(nameof(difficulty), difficulty, "Unknown difficulty."),
        };

    public static EquatableArray<Die> Roll(int count, int sides, IRandomSource random) =>
        [.. Enumerable.Range(0, count).Select(_ => new Die(sides, random.NextInt(1, sides + 1)))];
}
