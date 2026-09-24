using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;

namespace GameEvent.Engine.Seasons;

/// <summary>Create a season with the map from the current ruleset.</summary>
public sealed record CreateSeason(Guid SeasonId) : ICommand;

/// <summary>Add a player to the season; the player starts on the start cell with zero points.</summary>
public sealed record AddPlayer(Guid PlayerId, string Name) : ICommand;

[EventType("season-created")]
public sealed record SeasonCreated(Guid SeasonId, int RulesetVersion, MapGraph Map) : IGameEvent;

[EventType("player-added")]
public sealed record PlayerAdded(Guid PlayerId, string Name, string CellId) : IGameEvent;

internal static class SeasonSetup
{
    public static Decision Decide(SeasonState state, CreateSeason command, EngineContext context)
    {
        if (state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonAlreadyCreated, $"Season {state.SeasonId} already exists.");
        }

        var map = LinearMap.Generate(context.Ruleset.Map.LinearLength);
        return Decision.Accept(new SeasonCreated(command.SeasonId, context.Ruleset.Version, map));
    }

    public static Decision Decide(SeasonState state, AddPlayer command)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (state.Players.ContainsKey(command.PlayerId))
        {
            return Decision.Reject(RejectionCodes.PlayerAlreadyAdded, $"Player {command.PlayerId} is already in the season.");
        }

        return Decision.Accept(new PlayerAdded(command.PlayerId, command.Name, state.Map.Start.Id));
    }

    public static SeasonState Apply(SeasonState state, SeasonCreated e) =>
        state with { SeasonId = e.SeasonId, Map = e.Map };

    public static SeasonState Apply(SeasonState state, PlayerAdded e) =>
        state with
        {
            Players = state.Players.Add(
                e.PlayerId,
                new PlayerState(e.PlayerId, e.Name, e.CellId, Points: 0, TurnPhase.Idle, Offer: null, ActiveRunId: null)),
        };
}
