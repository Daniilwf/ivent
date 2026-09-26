using System.Security.Claims;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Web.Accounts;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GameEvent.Web.Pool;

/// <summary>
/// A game's status in the season as the pool page shows it (SPEC «Статусы игры в сезоне», H6, D-160): <c>taken</c> —
/// completed in the season or being played (an offer and a pending option count), with the player and, for a completed
/// game, when; <c>marks</c> — other players who dropped or tech-rerolled it (the game is free again);
/// <c>excludedForMe</c> — why it never comes to the viewer again. Only games with something to show are listed.
/// </summary>
public sealed record SeasonGameView(
    Guid GameId,
    RollMissReason? Taken,
    string? TakenBy,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<GameMarkView> Marks,
    ExclusionReason? ExcludedForMe);

public static class SeasonPoolEndpoints
{
    public static void MapSeasonPool(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/seasons/{seasonId:guid}/games", GetAsync)
            .WithTags("Pool")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    // Read-only: the state is folded from the log so the page and the wheel share one predicate (PoolStats.Taken, D-160)
    private static async Task<Results<Ok<IReadOnlyList<SeasonGameView>>, NotFound>> GetAsync(
        Guid seasonId, ClaimsPrincipal user, GameEventDbContext db, CancellationToken ct)
    {
        if (seasonId == Guid.Empty)
        {
            return TypedResults.NotFound();
        }

        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
        if (!state.IsCreated)
        {
            return TypedResults.NotFound();
        }

        var viewer = user.UserId();
        var me = state.Players.Values.FirstOrDefault(p => p.UserId == viewer);
        var taken = PoolStats.Taken(state).ToDictionary(m => m.GameId);
        var marks = state.Runs.Values
            .Where(r => r.PlayerId != me?.PlayerId && r.Status is RunStatus.Dropped or RunStatus.TechRerolled)
            .OrderBy(r => r.StartedAt)
            .ToLookup(
                r => r.GameId,
                r => new GameMarkView(state.Players[r.PlayerId].Name, r.Status == RunStatus.Dropped ? GameMarkKind.Dropped : GameMarkKind.TechRerolled));
        var excluded = (me?.Exclusions ?? []).ToDictionary(x => x.GameId, x => x.Reason);

        IReadOnlyList<SeasonGameView> views = [.. taken.Keys.Concat(marks.Select(g => g.Key)).Concat(excluded.Keys)
            .Distinct()
            .Order()
            .Select(gameId =>
            {
                var miss = taken.GetValueOrDefault(gameId);
                var completedAt = miss?.Reason == RollMissReason.CompletedInSeason
                    ? state.Runs.Values
                        .Where(r => r.GameId == gameId && r.PlayerId == miss.ByPlayerId && r.Status == RunStatus.Completed)
                        .Select(r => r.CompletedAt)
                        .Max()
                    : null;
                return new SeasonGameView(
                    gameId,
                    miss?.Reason,
                    miss is null ? null : state.Players[miss.ByPlayerId].Name,
                    completedAt,
                    [.. marks[gameId]],
                    excluded.TryGetValue(gameId, out var reason) ? reason : null);
            })];
        return TypedResults.Ok(views);
    }
}
