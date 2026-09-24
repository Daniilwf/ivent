using GameEvent.Engine.Rolls;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Pool;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GameEvent.Web.Rolls;

/// <summary>Pool health of a season for the admin (G10, G11, D-92).</summary>
public sealed record PoolStatsView(IReadOnlyList<CategoryStatView> Categories, IReadOnlyList<PlayerWithoutGamesView> PlayersWithoutGames);

/// <summary>How many games of a category can be rolled now; personal exclusions are not subtracted.</summary>
public sealed record CategoryStatView(string Category, int Weight, int Available);

/// <summary>A player whose next roll would find no game: the empty-pool signal.</summary>
public sealed record PlayerWithoutGamesView(Guid Id, string Name);

public static class PoolStatsEndpoints
{
    public static void MapPoolStats(this RouteGroupBuilder api) =>
        api.MapGet("/admin/seasons/{seasonId:guid}/pool-stats", GetAsync)
            .WithTags("Admin")
            .RequireAuthorization(Policies.Admin)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

    // Read-only: the state is folded from the log, the queue is not involved; the rate limit caps the cost (D-92).
    private static async Task<Results<Ok<PoolStatsView>, NotFound>> GetAsync(Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
        if (!state.IsCreated)
        {
            return TypedResults.NotFound();
        }

        var pool = await PoolReader.LoadAsync(db, ct);
        return TypedResults.Ok(new PoolStatsView(
            [.. PoolStats.Categories(state, pool).Select(c => new CategoryStatView(c.Category, c.Weight, c.Available))],
            [.. PoolStats.PlayersWithoutGames(state, pool).Select(id => new PlayerWithoutGamesView(id, state.Players[id].Name))]));
    }
}
