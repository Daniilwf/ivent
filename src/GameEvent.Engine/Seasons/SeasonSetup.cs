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

/// <summary>Set or move the deadline (UTC); null removes it. After the deadline the season closes (C10).</summary>
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

    public static Decision Decide(SeasonState state, CreateSeason command)
    {
        if (state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonAlreadyCreated, $"Season {state.SeasonId} already exists.");
        }

        if (RulesetValidator.Check(command.Ruleset) is { } rejection)
        {
            return rejection;
        }

        var map = LinearMap.Generate(command.Ruleset.Map.LinearLength);
        return Decision.Accept(new SeasonCreated(command.SeasonId, command.Name, command.Ruleset, map, command.Deadline));
    }

    public static Decision Decide(SeasonState state, ChangeSeasonStatus command) =>
        throw new NotImplementedException("C2");

    public static Decision Decide(SeasonState state, SetSeasonDeadline command) =>
        throw new NotImplementedException("C2");

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
