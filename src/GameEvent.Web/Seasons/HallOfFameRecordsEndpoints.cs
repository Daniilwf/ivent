using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

public sealed record HallOfFameGameView(Guid SeasonId, string SeasonName, Guid GameId, string Title, Guid RunId, decimal Hours);

public sealed record HallOfFameRecordsView(IReadOnlyList<HallOfFameGameView> LongestGames);

internal static class HallOfFameRecordsEndpoints
{
    public static void MapHallOfFameRecords(this RouteGroupBuilder api)
    {
        api.MapGet("/hall-of-fame/records", ReadAsync)
            .WithTags("Seasons")
            .RequireAuthorization()
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<Ok<HallOfFameRecordsView>> ReadAsync(GameEventDbContext db, HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        var candidates =
            from run in db.Runs.AsNoTracking()
            join season in db.Seasons.AsNoTracking() on run.SeasonId equals season.Id
            join game in db.Games.AsNoTracking() on run.GameId equals game.Id
            where (season.Status == SeasonStatus.Finished || season.Status == SeasonStatus.Archived)
                && run.Status == RunStatus.Completed && run.Hours > 0 && !game.IsDeleted
            orderby season.CreatedAt descending, season.Id, run.Id
            select new HallOfFameGameView(season.Id, season.Name, game.Id, game.Title, run.Id, run.Hours!.Value);

        // Compare exact decimals without involving the database collation's process culture.
        var longest = new List<HallOfFameGameView>();
        var maximum = 0m;
        await foreach (var game in candidates.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (game.Hours < maximum)
            {
                continue;
            }

            if (game.Hours > maximum)
            {
                maximum = game.Hours;
                longest = [];
            }

            longest.Add(game);
        }

        return TypedResults.Ok(new HallOfFameRecordsView(longest));
    }
}
