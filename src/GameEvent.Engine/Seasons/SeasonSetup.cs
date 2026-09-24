using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Seasons;

/// <summary>Create a season playing by <paramref name="Ruleset"/>; the map comes from it.</summary>
public sealed record CreateSeason(Guid SeasonId, Ruleset Ruleset) : ICommand;

/// <summary>
/// Add a user to the season as a player; the player starts on the start cell with zero points.
/// <c>PlayerId</c> is the participation id; a user takes part in a season at most once.
/// </summary>
public sealed record AddSeasonPlayer(Guid PlayerId, Guid UserId, string Name) : ICommand;

/// <summary>
/// The season is created with ruleset version 1. The whole ruleset is in the log: the rules a season plays by
/// are part of its history (D-82).
/// </summary>
[EventType("season-created")]
public sealed record SeasonCreated(Guid SeasonId, Ruleset Ruleset, MapGraph Map) : IGameEvent;

[EventType("season-player-added")]
public sealed record SeasonPlayerAdded(Guid PlayerId, Guid UserId, string Name, string CellId) : IGameEvent;

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
        return Decision.Accept(new SeasonCreated(command.SeasonId, command.Ruleset, map));
    }

    public static Decision Decide(SeasonState state, AddSeasonPlayer command)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (state.Players.ContainsKey(command.PlayerId) || state.Players.Values.Any(p => p.UserId == command.UserId))
        {
            return Decision.Reject(RejectionCodes.PlayerAlreadyAdded, $"Player {command.PlayerId} or user {command.UserId} is already in the season.");
        }

        return Decision.Accept(new SeasonPlayerAdded(command.PlayerId, command.UserId, command.Name, state.Map.Start.Id));
    }

    public static SeasonState Apply(SeasonState state, SeasonCreated e) =>
        state with { SeasonId = e.SeasonId, Map = e.Map, Ruleset = e.Ruleset, RulesetVersion = FirstRulesetVersion };

    public static SeasonState Apply(SeasonState state, SeasonPlayerAdded e) =>
        state with
        {
            Players = state.Players.Add(
                e.PlayerId,
                new SeasonPlayer(e.PlayerId, e.UserId, e.Name, e.CellId, Points: 0, TurnPhase.Idle, Offer: null, ActiveRunId: null)),
        };
}
