using System.Text.Json.Serialization;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Runs;

/// <summary>
/// What items and effects did to a run's throw (SPEC «Конвейер броска», D-14, D-408), each stage kept apart from the dice
/// by hours, so a correction of the hours changes only those: <see cref="Sides"/> of every die rolled for the run,
/// <see cref="ExtraDice"/> added by the <c>count</c> stage, <see cref="Added"/> — the additions (numbers and the rolled
/// dice, signed), <see cref="Multiplier"/>, the bounds <see cref="Min"/> and <see cref="Max"/>, and
/// <see cref="Sources"/> — the objects that changed it («не стакается с собой»).
/// </summary>
public sealed record RunDiceMods
{
    public static RunDiceMods None { get; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Sides { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EquatableArray<Die> ExtraDice { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Added { get; init; }

    public int Multiplier { get; init; } = 1;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Min { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Max { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EquatableArray<string> Sources { get; init; }

    /// <summary>
    /// The run's total through the pipeline (D-14): dice, challenge dice and extra dice, the zone's and the items'
    /// additions, then the multiplier, then never below 0, then the bounds.
    /// </summary>
    public int Total(int diceSum, int zoneAdded)
    {
        var sum = (diceSum + ExtraDice.Sum(d => d.Value) + zoneAdded + Added) * Multiplier;
        var total = Math.Max(0, sum);
        total = Min is { } min ? Math.Max(min, total) : total;
        return Max is { } max ? Math.Min(max, total) : total;
    }

    /// <summary>These mods with <paramref name="stage"/> set to <paramref name="value"/> (a resolved number) by <paramref name="source"/>.</summary>
    public RunDiceMods With(DiceStage stage, int value, string source) =>
        (stage switch
        {
            DiceStage.Sides => this with { Sides = value },
            DiceStage.Add => this with { Added = Added + value },
            DiceStage.Multiply => this with { Multiplier = Multiplier * value },
            DiceStage.Min => this with { Min = Min is { } min ? Math.Max(min, value) : value },
            DiceStage.Max => this with { Max = Max is { } max ? Math.Min(max, value) : value },
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Count and reroll roll dice; they are not a number."),
        }) with
        { Sources = Sources.Contains(source) ? Sources : [.. Sources, source] };
}

/// <summary>
/// Items or effects changed the run's throw (D-408): <see cref="Mods"/> is what the run carries now (the result: dice
/// rolled for it are in <see cref="Rolled"/> and in its extra dice). The points and steps of the difference follow.
/// </summary>
/// <remarks><see cref="SpentNext"/>: how many of the player's changes waiting for the next throw this throw took (written only when some).</remarks>
[EventType("run-dice-modified")]
public sealed record RunDiceModified(
    Guid RunId,
    Guid PlayerId,
    RunDiceMods Mods,
    EquatableArray<Die> Rolled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int SpentNext = 0) : IGameEvent;

/// <summary>
/// The run's throw was rerolled by an item (CONTENT.md «Переброс»): the new dice by hours, challenge dice and extra dice
/// replace the old ones; the result is final. The difference follows as points and steps.
/// </summary>
[EventType("run-dice-rerolled")]
public sealed record RunDiceRerolled(
    Guid RunId,
    Guid PlayerId,
    string ObjectId,
    EquatableArray<Die> Dice,
    EquatableArray<Die> ChallengeDice,
    EquatableArray<Die> ExtraDice) : IGameEvent;
