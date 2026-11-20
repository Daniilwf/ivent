using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

public sealed record HallOfFameGameView(Guid SeasonId, string SeasonName, Guid GameId, string Title, Guid RunId, decimal Hours);

public sealed record HallOfFameFinishView(Guid SeasonId, string SeasonName, Guid PlayerId, string Name, decimal ElapsedSeconds, Guid? ProfileUserId);

public sealed record HallOfFameRecordsView(IReadOnlyList<HallOfFameGameView> LongestGames, IReadOnlyList<HallOfFameFinishView> FastestFinishes);

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
        return TypedResults.Ok(new HallOfFameRecordsView(await ReadLongestAsync(db, ct), await ReadFastestAsync(db, ct)));
    }

    private static async Task<IReadOnlyList<HallOfFameGameView>> ReadLongestAsync(GameEventDbContext db, CancellationToken ct)
    {
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

        return longest;
    }

    private static async Task<IReadOnlyList<HallOfFameFinishView>> ReadFastestAsync(GameEventDbContext db, CancellationToken ct)
    {
        // A season can only finish after proof review. Revoking a finish clears FinishedAt in the projection.
        var candidates = await (
            from player in db.SeasonPlayers.AsNoTracking()
            join season in db.Seasons.AsNoTracking() on player.SeasonId equals season.Id
            where (season.Status == SeasonStatus.Finished || season.Status == SeasonStatus.Archived) && player.FinishedAt != null
            orderby season.CreatedAt descending, season.Id, player.FinishOrder, player.Id
            select new
            {
                SeasonId = season.Id,
                SeasonName = season.Name,
                PlayerId = player.Id,
                player.Name,
                FinishedAt = player.FinishedAt!.Value,
                ProfileUserId = db.Users.Any(user => user.Id == player.UserId && !user.IsDeleted) ? player.UserId : (Guid?)null,
            }).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return [];
        }

        // Creation and publication can happen long before and after play. Only the saved activation starts the clock.
        var seasonIds = candidates.Select(candidate => candidate.SeasonId).Distinct().ToArray();
        var milestones = db.Events.AsNoTracking()
            .Where(row => seasonIds.Contains(row.SeasonId) && row.Type == "season-status-changed" && row.UndoneByEventId == null)
            .Select(row => new { row.SeasonId, row.OccurredAt, row.Type, row.Version, row.Data });
        var starts = new Dictionary<Guid, DateTimeOffset>();
        await foreach (var row in milestones.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)) is SeasonStatusChanged { To: SeasonStatus.Active })
            {
                starts.Add(row.SeasonId, row.OccurredAt);
            }
        }

        var fastest = new List<HallOfFameFinishView>();
        var minimum = long.MaxValue;
        foreach (var candidate in candidates)
        {
            if (!starts.TryGetValue(candidate.SeasonId, out var started))
            {
                continue;
            }

            var ticks = (candidate.FinishedAt - started).Ticks;
            if (ticks < 0 || ticks > minimum)
            {
                continue;
            }

            if (ticks < minimum)
            {
                minimum = ticks;
                fastest.Clear();
            }

            fastest.Add(new HallOfFameFinishView(candidate.SeasonId, candidate.SeasonName, candidate.PlayerId,
                candidate.Name, ticks / (decimal)TimeSpan.TicksPerSecond, candidate.ProfileUserId));
        }

        return fastest;
    }
}
