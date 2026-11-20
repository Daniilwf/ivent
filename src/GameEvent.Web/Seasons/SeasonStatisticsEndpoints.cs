using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Statistics;
using GameEvent.Infrastructure.Database;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

public sealed record SeasonStatisticsView(
    Guid SeasonId, int CompletedRuns, int? BestRating, int? WorstRating, int AttacksMade, int AttacksReceived, string SeasonName,
    decimal TotalHours);

internal static class SeasonStatisticsEndpoints
{
    private static readonly string s_attackType = EventCatalog.Describe(typeof(HostileReceived)).Name;

    public static void MapSeasonStatistics(this RouteGroupBuilder api)
    {
        api.MapGet("/seasons/{seasonId:guid}/my-statistics", ReadAsync)
            .WithTags("Seasons")
            .RequireAuthorization()
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    internal static bool IsEnabled(string rulesetJson) =>
        JsonSerializer.Deserialize<Ruleset>(rulesetJson, EngineJson.Options)?.Features.SeasonStatistics == true;

    private static async Task<Results<Ok<SeasonStatisticsView>, NotFound>> ReadAsync(
        Guid seasonId, ClaimsPrincipal user, HttpResponse response, GameEventDbContext db, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        if (user.UserId() is not { } userId)
        {
            return TypedResults.NotFound();
        }

        var participation = await (
            from player in db.SeasonPlayers.AsNoTracking()
            join season in db.Seasons.AsNoTracking() on player.SeasonId equals season.Id
            where player.SeasonId == seasonId && player.UserId == userId
            select new { PlayerId = player.Id, season.RulesetJson, season.Name }).SingleOrDefaultAsync(ct);
        if (participation is null || !IsEnabled(participation.RulesetJson))
        {
            return TypedResults.NotFound();
        }

        var ownReviews = db.Reviews.AsNoTracking().Where(review => review.PlayerId == participation.PlayerId);
        var own = await (
            from run in db.Runs.AsNoTracking()
            where run.SeasonId == seasonId && run.PlayerId == participation.PlayerId && run.Status == RunStatus.Completed
            join review in ownReviews on run.Id equals review.RunId into reviews
            from review in reviews.DefaultIfEmpty()
            select new { run.Hours, Rating = (int?)review.Rating }).ToListAsync(ct);

        var partnerRuns = await CoopPartnerRuns.LoadAsync(db, [seasonId], [participation.PlayerId], ct);
        var completedAsPartner = partnerRuns.FindAll(run => run.Status == RunStatus.Completed);
        var totalHours = own.Sum(run => run.Hours ?? 0) + completedAsPartner.Sum(run => run.Hours ?? 0);
        var hits = await db.Events.AsNoTracking()
            .Where(row => row.SeasonId == seasonId && row.Type == s_attackType && row.UndoneByEventId == null)
            .Select(row => new { row.Type, row.Version, row.Data }).ToListAsync(ct);
        var attacks = AttackStatistics.ForPlayer(participation.PlayerId,
            hits.Select(row => EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data))));
        return TypedResults.Ok(new SeasonStatisticsView(
            seasonId, own.Count + completedAsPartner.Count, own.Max(run => run.Rating), own.Min(run => run.Rating),
            attacks.AttacksMade, attacks.AttacksReceived, participation.Name, totalHours));
    }
}
