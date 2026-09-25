using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Runs;

/// <summary>
/// The admin corrects the hours of a completed run (SPEC «Каждый кубик хранится отдельно», D-14, D-97): the dice count by
/// the new hours; missing dice are rolled and added, extra dice are removed from the end; challenge dice stay. Points,
/// position and completion coins change by the difference.
/// </summary>
public sealed record CorrectRunHours(Guid RunId, decimal Hours, string Comment) : ICommand;

/// <summary>
/// The admin changes the difficulty of a completed run, usually by the proof (SPEC «Сложность засчитывается по пруфу»,
/// Q-5, D-97): every die, challenge dice too, becomes ⌈old × new sides / old sides⌉. Points and position change by the
/// difference; the difficulty's granted event follows the change.
/// </summary>
public sealed record ChangeRunDifficulty(Guid RunId, Difficulty Difficulty, string Comment) : ICommand;

/// <summary>One die before and after a correction (Q-5: the event keeps both).</summary>
public sealed record DieChange(Die Before, Die After);

/// <summary>
/// The hours of a run were corrected: <see cref="Added"/> dice were rolled and appended, <see cref="Removed"/> dice
/// were taken off the end of the dice by hours.
/// </summary>
[EventType("run-hours-corrected")]
public sealed record RunHoursCorrected(
    Guid RunId,
    Guid PlayerId,
    decimal OldHours,
    decimal NewHours,
    EquatableArray<Die> Added,
    int Removed,
    string Comment,
    DateTimeOffset CorrectedAt) : IGameEvent;

/// <summary>The difficulty of a run changed; every die recalculated, both values kept (Q-5).</summary>
[EventType("run-difficulty-changed")]
public sealed record RunDifficultyChanged(
    Guid RunId,
    Guid PlayerId,
    Difficulty OldDifficulty,
    Difficulty NewDifficulty,
    EquatableArray<DieChange> Dice,
    EquatableArray<DieChange> ChallengeDice,
    string Comment,
    DateTimeOffset ChangedAt) : IGameEvent;

internal static class Corrections
{
    public static Decision Decide(SeasonState state, CorrectRunHours command, EngineContext context) =>
        throw new NotImplementedException("C7");

    public static Decision Decide(SeasonState state, ChangeRunDifficulty command, EngineContext context) =>
        throw new NotImplementedException("C7");

    public static SeasonState Apply(SeasonState state, RunHoursCorrected e) =>
        throw new NotImplementedException("C7");

    public static SeasonState Apply(SeasonState state, RunDifficultyChanged e) =>
        throw new NotImplementedException("C7");
}
