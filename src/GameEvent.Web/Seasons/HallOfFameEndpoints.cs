using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

public sealed record HallOfFameWinnerView(Guid PlayerId, string Name, int Points, Guid? ProfileUserId);

public sealed record HallOfFameSeasonView(Guid SeasonId, string SeasonName, IReadOnlyList<HallOfFameWinnerView> Winners);

internal static class HallOfFameEndpoints
{
    public static void MapHallOfFame(this RouteGroupBuilder api)
    {
        api.MapGet("/hall-of-fame", ReadAsync)
            .WithTags("Seasons")
            .RequireAuthorization()
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<Ok<IReadOnlyList<HallOfFameSeasonView>>> ReadAsync(
        GameEventDbContext db, HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        var winners = await (
            from season in db.Seasons.AsNoTracking()
            join result in db.SeasonResults.AsNoTracking() on season.Id equals result.SeasonId
            join player in db.SeasonPlayers.AsNoTracking() on new { result.SeasonId, result.PlayerId }
                equals new { player.SeasonId, PlayerId = player.Id }
            where (season.Status == SeasonStatus.Finished || season.Status == SeasonStatus.Archived) && result.Place == 1
            orderby season.CreatedAt descending, season.Id, result.Row
            select new
            {
                SeasonId = season.Id,
                SeasonName = season.Name,
                Winner = new HallOfFameWinnerView(result.PlayerId, player.Name, result.Points,
                    db.Users.Any(user => user.Id == player.UserId && !user.IsDeleted) ? player.UserId : (Guid?)null),
            }).ToListAsync(ct);

        return TypedResults.Ok<IReadOnlyList<HallOfFameSeasonView>>(
            [.. winners.GroupBy(row => (row.SeasonId, row.SeasonName)).Select(season =>
                new HallOfFameSeasonView(season.Key.SeasonId, season.Key.SeasonName, [.. season.Select(row => row.Winner)]))]);
    }
}
