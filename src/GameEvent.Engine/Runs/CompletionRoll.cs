using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Runs;

/// <summary>
/// Dice for a completed run (D-13, D-14). Pipeline: count from hours (plus a zone's extra dice), sides from difficulty,
/// roll, a zone's addition (D-307). Stage 4 adds reroll, multiply and min/max modifiers from items.
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

    /// <summary>The run's dice count: by the hours, plus the zone's extra dice after the limit (D-307).</summary>
    public static int Count(decimal hours, RunSnapshot snapshot) => Count(hours, snapshot.DiceCount) + (snapshot.Zone?.ExtraDice ?? 0);

    /// <summary>
    /// What a completed run gives in points and steps: its dice, its challenge dice and the zone's addition, never
    /// below 0 (D-307). Every correction and reject takes back by this total.
    /// </summary>
    public static int Total(RunState run) => Total(run.Dice, run.ChallengeDice, run.Snapshot);

    public static int Total(EquatableArray<Die> dice, EquatableArray<Die> challengeDice, RunSnapshot snapshot) =>
        Math.Max(0, dice.Sum(d => d.Value) + challengeDice.Sum(d => d.Value) + (snapshot.Zone?.AddedToSum ?? 0));

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
