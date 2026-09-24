using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Runs;

/// <summary>
/// «Дроп»: give up the active run with a penalty (SPEC «Реролл, дроп, тех-реролл», D-09). Allowed any time while
/// Playing; <c>roll.minPlayMinutesBeforeDrop</c> is only a hint in the interface. The penalty dice take points and
/// position (<c>drop.affectsPoints</c>, <c>drop.affectsPosition</c>), never below the start; a mandatory bad event
/// follows (<c>drop.mandatoryEvent</c>); no coins. The game is excluded for the player and free for everyone else.
/// </summary>
public sealed record DropRun(Guid PlayerId) : ICommand;

/// <summary>Why a game does not start (D-11); <see cref="Other"/> needs a comment.</summary>
public enum TechRerollReason
{
    WeakPc,
    PaidUnavailable,
    DoesNotLaunch,
    EmulatorTooSlow,
    Other,
}

/// <summary>
/// «Тех-реролл»: give up the active run for a technical reason, free (D-11). The player may do it within
/// <c>roll.techRerollWindowHours</c> after the roll; later only the admin, on the player's behalf
/// (<see cref="ByAdmin"/>, set by the admin endpoint), with a mark in the log. The game is excluded for the player and a
/// new roll follows at once (SPEC: Playing → Rolling), with its own free rerolls (D-07).
/// </summary>
public sealed record TechReroll(Guid PlayerId, TechRerollReason Reason, string? Comment, bool ByAdmin = false) : ICommand;

/// <summary>
/// The admin turns a tech reroll into a drop (D-11): the drop penalty and the bad event hit the player's current points
/// and position; the exclusion's reason becomes a drop. Allowed until the season is finished.
/// </summary>
public sealed record ConvertTechRerollToDrop(Guid RunId, string Comment) : ICommand;

/// <summary>The run was dropped; <see cref="PenaltyDice"/> are the penalty roll, each die separately.</summary>
[EventType("run-dropped")]
public sealed record RunDropped(Guid RunId, Guid PlayerId, EquatableArray<Die> PenaltyDice, DateTimeOffset DroppedAt) : IGameEvent;

[EventType("run-tech-rerolled")]
public sealed record RunTechRerolled(
    Guid RunId, Guid PlayerId, TechRerollReason Reason, string? Comment, bool ByAdmin, DateTimeOffset RerolledAt) : IGameEvent;

/// <summary>A tech reroll became a drop; the penalty events follow in the same command.</summary>
[EventType("tech-reroll-converted-to-drop")]
public sealed record TechRerollConvertedToDrop(Guid RunId, Guid PlayerId, string Comment, EquatableArray<Die> PenaltyDice) : IGameEvent;

internal static class Drops
{
    public static Decision Decide(Seasons.SeasonState state, DropRun command, Seasons.EngineContext context) =>
        throw new NotImplementedException("C6");

    public static Decision Decide(Seasons.SeasonState state, TechReroll command, Seasons.EngineContext context) =>
        throw new NotImplementedException("C6");

    public static Decision Decide(Seasons.SeasonState state, ConvertTechRerollToDrop command, Seasons.EngineContext context) =>
        throw new NotImplementedException("C6");

    public static Seasons.SeasonState Apply(Seasons.SeasonState state, RunDropped e) =>
        throw new NotImplementedException("C6");

    public static Seasons.SeasonState Apply(Seasons.SeasonState state, RunTechRerolled e) =>
        throw new NotImplementedException("C6");

    public static Seasons.SeasonState Apply(Seasons.SeasonState state, TechRerollConvertedToDrop e) =>
        throw new NotImplementedException("C6");
}
