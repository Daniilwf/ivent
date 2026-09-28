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

/// <summary>
/// What stands in the way of publishing a valid map now, apart from the map itself (SPEC «Проверки», D-305, D-308): the
/// cells where players stand and are removed or change between a finish and not, and pending branch choices. The
/// editor shows them before the admin publishes; <see cref="PublishMap"/> refuses on the first kind found. Codes are
/// those of the rejection; the subject is the cell, or <c>map</c> for a pending branch choice.
/// </summary>
public static class MapPublicationChecks
{
    public static IReadOnlyList<MapError> For(SeasonState state, MapGraph map)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(map);
        var errors = new List<MapError>();
        foreach (var cell in state.Players.Values.Where(p => !map.HasCell(p.CellId)).Select(p => p.CellId).Distinct().Order(StringComparer.Ordinal))
        {
            errors.Add(new MapError(RejectionCodes.MapOccupiedCellRemoved, cell, $"Players stand on cell '{cell}' the new map removes."));
        }

        // A player who has not finished must not stand on a finish, a finisher must stay on one: a token on a finish never moves (D-308)
        foreach (var cell in state.Players.Values
            .Where(p => map.HasCell(p.CellId) && (map.CellById(p.CellId).Type == CellType.Finish) != (p.Finish is not null))
            .Select(p => p.CellId).Distinct().Order(StringComparer.Ordinal))
        {
            errors.Add(new MapError(RejectionCodes.MapOccupiedCellRetyped, cell, $"The new map makes a finish of cell '{cell}' a player is still on, or the other way round."));
        }

        // D-305: the remaining steps of a pending branch choice lead from a fork of the current map
        if (state.Players.Values.Any(p => p.Choice?.Kind == ChoiceKind.Branch))
        {
            errors.Add(new MapError(RejectionCodes.BranchChoicePending, "map", "A player is choosing a branch: wait for the choice or discard it first."));
        }

        return errors;
    }
}

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

        // SPEC «Проверки»: клетки, где стоят игроки, не удалены; D-305, D-308
        // The first kind of conflict found refuses: its code, the cells it is about
        if (MapPublicationChecks.For(state, command.Map).GroupBy(c => c.Code).FirstOrDefault() is { } conflict)
        {
            return Decision.Reject(conflict.Key, string.Join(" ", conflict.Select(c => c.Message)));
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
