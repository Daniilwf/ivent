using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Seasons;

/// <summary>
/// Create a season in the Draft status playing by <paramref name="Ruleset"/>; the map comes from it.
/// Players can be added while it is a draft; the game starts with <see cref="ChangeSeasonStatus"/> to Active.
/// </summary>
public sealed record CreateSeason(Guid SeasonId, string Name, Ruleset Ruleset, DateTimeOffset? Deadline = null) : ICommand;

/// <summary>Move the season along its lifecycle. Only the next status is allowed (SE1).</summary>
public sealed record ChangeSeasonStatus(SeasonStatus To) : ICommand;

/// <summary>Set or move the deadline (stored in UTC); null removes it. After the deadline the season closes (C10).</summary>
public sealed record SetSeasonDeadline(DateTimeOffset? Deadline) : ICommand;

/// <summary>
/// The season is created as a draft with ruleset version 1. The whole ruleset is in the log: the rules a season
/// plays by are part of its history (D-82).
/// </summary>
[EventType("season-created")]
public sealed record SeasonCreated(Guid SeasonId, string Name, Ruleset Ruleset, MapGraph Map, DateTimeOffset? Deadline) : IGameEvent;

[EventType("season-status-changed")]
public sealed record SeasonStatusChanged(SeasonStatus From, SeasonStatus To) : IGameEvent;

[EventType("season-deadline-set")]
public sealed record SeasonDeadlineSet(DateTimeOffset? Deadline) : IGameEvent;

internal static class SeasonSetup
{
    public const int FirstRulesetVersion = 1;
    public const int MaxNameLength = 100;

    public static Decision Decide(SeasonState state, CreateSeason command)
    {
        if (state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonAlreadyCreated, $"Season {state.SeasonId} already exists.");
        }

        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > MaxNameLength)
        {
            return Decision.Reject(RejectionCodes.SeasonInvalidName, $"A season needs a name of 1–{MaxNameLength} characters.");
        }

        if (RulesetValidator.Check(command.Ruleset) is { } rejection)
        {
            return rejection;
        }

        var map = LinearMap.Generate(command.Ruleset.Map.LinearLength);
        return Decision.Accept(new SeasonCreated(command.SeasonId, command.Name.Trim(), command.Ruleset, map, command.Deadline?.ToUniversalTime()));
    }

    public static Decision Decide(SeasonState state, ChangeSeasonStatus command)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        // One way, one step at a time: draft → active → closing → finished → archived (SE1).
        return Enum.IsDefined(command.To) && command.To == state.Status + 1
            ? Decision.Accept(new SeasonStatusChanged(state.Status, command.To))
            : Decision.Reject(RejectionCodes.SeasonInvalidTransition, $"The season cannot go from {state.Status} to {command.To}.");
    }

    public static Decision Decide(SeasonState state, SetSeasonDeadline command)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (IsOver(state))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}.");
        }

        var deadline = command.Deadline?.ToUniversalTime();
        return deadline == state.Deadline
            ? Decision.Reject(RejectionCodes.SeasonNothingToChange, "The deadline is already set to that value.")
            : Decision.Accept(new SeasonDeadlineSet(deadline));
    }

    /// <summary>Results are fixed: nothing about the season or its players changes any more.</summary>
    public static bool IsOver(SeasonState state) => state.Status is SeasonStatus.Finished or SeasonStatus.Archived;

    public static SeasonState Apply(SeasonState state, SeasonCreated e) =>
        state with
        {
            SeasonId = e.SeasonId,
            Name = e.Name,
            Status = SeasonStatus.Draft,
            Deadline = e.Deadline,
            Map = e.Map,
            Ruleset = e.Ruleset,
            RulesetVersion = FirstRulesetVersion,
        };

    public static SeasonState Apply(SeasonState state, SeasonStatusChanged e) => state with { Status = e.To };

    public static SeasonState Apply(SeasonState state, SeasonDeadlineSet e) => state with { Deadline = e.Deadline };

    /// <summary>Game actions (roll, start, complete) need a running season.</summary>
    public static Decision? RequireActive(SeasonState state) =>
        !state.IsCreated
            ? Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.")
            : state.Status != SeasonStatus.Active
                ? Decision.Reject(RejectionCodes.SeasonNotActive, $"The season is {state.Status}, game actions need Active.")
                : null;
}
