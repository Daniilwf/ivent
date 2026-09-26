using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Seasons;

/// <summary>
/// Create a season in the Draft status playing by <paramref name="Ruleset"/>. The map: generated from
/// <c>map.linearLength</c> in the linear mode, <paramref name="Map"/> — required and checked — in the graph mode (D-300).
/// Players can be added while it is a draft; the game starts with <see cref="ChangeSeasonStatus"/> to Active.
/// </summary>
public sealed record CreateSeason(
    Guid SeasonId, string Name, Ruleset Ruleset, DateTimeOffset? Deadline = null, MapGraph? Map = null) : ICommand;

/// <summary>Move the season along its lifecycle. Only the next status is allowed (SE1).</summary>
public sealed record ChangeSeasonStatus(SeasonStatus To) : ICommand;

/// <summary>
/// Set or move the deadline (stored in UTC); null removes it. Only in Draft or Active: once closing, the deadline is
/// history (D-101).
/// </summary>
public sealed record SetSeasonDeadline(DateTimeOffset? Deadline) : ICommand;

/// <summary>
/// The scheduler's command (D-101): the deadline has come, the active season goes to Closing. Rejected before the
/// deadline by the engine's clock (<c>season.deadlineNotReached</c>) and when the season is not Active.
/// </summary>
public sealed record ReachDeadline : ICommand;

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

/// <summary>
/// The final table, written right after the season goes to Finished (SPEC «SeasonResult», D-101): the leaderboard at
/// that moment. It never changes afterwards; nominations come with stage 6.
/// </summary>
[EventType("season-result-recorded")]
public sealed record SeasonResultRecorded(EquatableArray<Ranking.LeaderboardRow> Rows) : IGameEvent;

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

        MapGraph map;
        if (command.Ruleset.Features.MapMode == MapMode.Linear)
        {
            if (command.Map is not null)
            {
                return Decision.Reject(RejectionCodes.MapNotInLinearMode, "A linear season generates its map from map.linearLength.");
            }

            map = LinearMap.Generate(command.Ruleset.Map.LinearLength);
        }
        else if (command.Map is null)
        {
            return Decision.Reject(RejectionCodes.MapRequired, "A season with a graph map is created with its map.");
        }
        else if (MapValidator.Check(command.Map, command.Ruleset) is { } badMap)
        {
            return badMap;
        }
        else
        {
            map = command.Map;
        }

        return Decision.Accept(new SeasonCreated(command.SeasonId, command.Name.Trim(), command.Ruleset, map, command.Deadline?.ToUniversalTime()));
    }

    public static Decision Decide(SeasonState state, ChangeSeasonStatus command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        // One way, one step at a time: draft → active → closing → finished → archived (SE1).
        if (!Enum.IsDefined(command.To) || command.To != state.Status + 1)
        {
            return Decision.Reject(RejectionCodes.SeasonInvalidTransition, $"The season cannot go from {state.Status} to {command.To}.");
        }

        // Starting a season whose deadline has passed would open it closed (D-101).
        if (command.To == SeasonStatus.Active && IsPastDeadline(state, context.Clock.UtcNow))
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlineInPast, "The deadline has passed; move it before starting the season.");
        }

        var changed = new SeasonStatusChanged(state.Status, command.To);
        if (command.To != SeasonStatus.Finished)
        {
            return Decision.Accept(changed);
        }

        // Results only once every proof is checked; the leaderboard at that moment is the season's result (D-101).
        if (Proofs.ProofReviewOrder.Order(state).Count > 0)
        {
            return Decision.Reject(RejectionCodes.SeasonProofsPending, "Every completed run must be approved or rejected first.");
        }

        return Decision.Accept(changed, new SeasonResultRecorded(Ranking.Leaderboard.Build(state)));
    }

    public static Decision Decide(SeasonState state, SetSeasonDeadline command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        // Once closing, the deadline is history (D-101).
        if (state.Status is not (SeasonStatus.Draft or SeasonStatus.Active))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}.");
        }

        // A deadline in the past would close the turns at once and the season within a scheduler tick; statuses do not
        // go back, so a typo must not do that. Closing early is the status change (D-101).
        var deadline = command.Deadline?.ToUniversalTime();
        if (deadline is { } set && set <= context.Clock.UtcNow)
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlineInPast, "The deadline must be in the future; close the season by its status instead.");
        }

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

    public static Decision Decide(SeasonState state, ReachDeadline command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (state.Status != SeasonStatus.Active)
        {
            return Decision.Reject(RejectionCodes.SeasonNotActive, $"The season is {state.Status}; only an active season closes at its deadline.");
        }

        return IsPastDeadline(state, context.Clock.UtcNow)
            ? Decision.Accept(new SeasonStatusChanged(SeasonStatus.Active, SeasonStatus.Closing))
            : Decision.Reject(RejectionCodes.SeasonDeadlineNotReached, "The deadline has not come yet.");
    }

    public static SeasonState Apply(SeasonState state, SeasonResultRecorded e) => state with { Result = e.Rows };

    /// <summary>The deadline has come by <paramref name="now"/>: no player turn actions any more, whatever the status (D-101).</summary>
    public static bool IsPastDeadline(SeasonState state, DateTimeOffset now) => state.Deadline is { } deadline && now >= deadline;

    /// <summary>Game actions (roll, start, complete) need a running season.</summary>
    public static Decision? RequireActive(SeasonState state) =>
        !state.IsCreated
            ? Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.")
            : state.Status != SeasonStatus.Active
                ? Decision.Reject(RejectionCodes.SeasonNotActive, $"The season is {state.Status}, game actions need Active.")
                : null;
}
