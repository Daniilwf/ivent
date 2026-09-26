using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Map;

/// <summary>
/// The admin publishes a new version of the season's graph map (SPEC «Редактор»: players see edits only after
/// publication, D-300, D-308). Checked like a new map, and the cells where players stand must stay. Only in the graph
/// mode, while the season is a draft or running.
/// </summary>
public sealed record PublishMap(MapGraph Map, string Comment) : ICommand;

/// <summary>A new version of the map, stored whole. A player whose last path segment no longer lies on its arrows starts a new segment at their cell.</summary>
[EventType("map-published")]
public sealed record MapPublished(MapGraph Map, string Comment) : IGameEvent;

internal static class MapPublishing
{
    public static Decision Decide(SeasonState state, PublishMap command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (state.Rules.Features.MapMode != MapMode.Graph)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "The season plays on the linear map: switch features.mapMode to graph first.");
        }

        if (state.Status is not (SeasonStatus.Draft or SeasonStatus.Active))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The map is published while the season is a draft or running; it is {state.Status}.");
        }

        if (string.IsNullOrWhiteSpace(command.Comment))
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "Every admin change explains itself in the public log.");
        }

        if (command.Comment.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        if (MapValidator.Check(command.Map, state.Rules) is { } invalid)
        {
            return invalid;
        }

        // SPEC «Проверки»: клетки, где стоят игроки, не удалены
        var stranded = state.Players.Values.Where(p => !command.Map.HasCell(p.CellId)).Select(p => p.CellId).Distinct().Order(StringComparer.Ordinal).ToList();
        if (stranded.Count > 0)
        {
            return Decision.Reject(RejectionCodes.MapOccupiedCellRemoved, $"Players stand on cells the new map removes: {string.Join(", ", stranded)}.");
        }

        // D-305: the remaining steps of a pending branch choice lead from a fork of the current map
        if (state.Players.Values.Any(p => p.Choice?.Kind == ChoiceKind.Branch))
        {
            return Decision.Reject(RejectionCodes.BranchChoicePending, "A player is choosing a branch: wait for the choice or discard it first.");
        }

        return command.Map == state.Map
            ? Decision.Reject(RejectionCodes.MapUnchanged, "The new map equals the current one.")
            : Decision.Accept(new MapPublished(command.Map, command.Comment.Trim()));
    }

    public static SeasonState Apply(SeasonState state, MapPublished e)
    {
        var players = state.Players;
        foreach (var (id, player) in state.Players)
        {
            if (!LiesOn(player.Path, e.Map))
            {
                players = players.SetItem(id, player with { Path = new PlayerPath([.. player.Path.Segments.SkipLast(1), new PathSegment([player.CellId])]) });
            }
        }

        return state with { Map = e.Map, Players = players };
    }

    /// <summary>
    /// The last segment — the one moving back retraces — is on <paramref name="map"/>, every walked step one of its
    /// arrows. Earlier segments are history behind a transfer and are never walked again.
    /// </summary>
    public static bool LiesOn(PlayerPath path, MapGraph map)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(map);
        var cells = path.Segments[^1].Cells;
        var edges = map.Edges.Select(x => (x.From, x.To)).ToHashSet();
        return cells.All(map.HasCell) && cells.Zip(cells.Skip(1)).All(edges.Contains);
    }
}
