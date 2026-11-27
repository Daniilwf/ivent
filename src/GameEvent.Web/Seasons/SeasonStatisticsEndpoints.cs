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
    decimal TotalHours, SeasonRollView? BestRoll, SeasonRollView? WorstRoll);

public sealed record SeasonRollView(Guid RunId, long Sum, long Minimum, long Maximum, decimal Luck);

public sealed record AllTimeStatisticsView(
    int SeasonCount, int CompletedRuns, int DroppedRuns, decimal TotalHours,
    int? BestRating, int? WorstRating, int AttacksMade, int AttacksReceived, SeasonRollView? BestRoll, SeasonRollView? WorstRoll);

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

        api.MapGet("/me/statistics", ReadAllTimeAsync)
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

        var stats = await LoadAsync(db, new Dictionary<Guid, Guid> { [seasonId] = participation.PlayerId }, ct);
        return TypedResults.Ok(new SeasonStatisticsView(seasonId, stats.CompletedRuns, stats.BestRating, stats.WorstRating,
            stats.AttacksMade, stats.AttacksReceived, participation.Name, stats.TotalHours, stats.BestRoll, stats.WorstRoll));
    }

    private static async Task<Results<Ok<AllTimeStatisticsView>, UnauthorizedHttpResult>> ReadAllTimeAsync(
        ClaimsPrincipal user, HttpResponse response, GameEventDbContext db, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        if (user.UserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var memberships = await db.SeasonPlayers.AsNoTracking()
            .Where(player => player.UserId == userId)
            .Select(player => new { player.SeasonId, player.Id })
            .ToDictionaryAsync(player => player.SeasonId, player => player.Id, ct);
        if (memberships.Count == 0)
        {
            return TypedResults.Ok(new AllTimeStatisticsView(0, 0, 0, 0, null, null, 0, 0, null, null));
        }

        var playerIds = memberships.Values.ToArray();
        var droppedRuns = await db.Runs.AsNoTracking()
            .CountAsync(run => playerIds.Contains(run.PlayerId) && run.Status == RunStatus.Dropped, ct);
        var stats = await LoadAsync(db, memberships, ct);
        return TypedResults.Ok(new AllTimeStatisticsView(memberships.Count, stats.CompletedRuns, droppedRuns, stats.TotalHours,
            stats.BestRating, stats.WorstRating, stats.AttacksMade, stats.AttacksReceived, stats.BestRoll, stats.WorstRoll));
    }

    private static async Task<RunStatistics> LoadAsync(
        GameEventDbContext db, IReadOnlyDictionary<Guid, Guid> memberships, CancellationToken ct)
    {
        var seasonIds = memberships.Keys.ToArray();
        var playerIds = memberships.Values.ToArray();
        var ownReviews = db.Reviews.AsNoTracking().Where(review => playerIds.Contains(review.PlayerId));
        var own = await (
            from run in db.Runs.AsNoTracking()
            where seasonIds.Contains(run.SeasonId) && playerIds.Contains(run.PlayerId) && run.Status == RunStatus.Completed
            join review in ownReviews on run.Id equals review.RunId into reviews
            from review in reviews.DefaultIfEmpty()
            orderby run.Id
            select new { run.Id, run.Hours, Rating = (int?)review.Rating, run.DiceJson, run.ChallengeDiceJson, run.DiceModsJson }).ToListAsync(ct);

        (Guid RunId, RollMeasurement Measurement)? best = null;
        (Guid RunId, RollMeasurement Measurement)? worst = null;
        foreach (var run in own)
        {
            if (SavedRoll.Measure(run.DiceJson, run.ChallengeDiceJson, run.DiceModsJson) is not { HasLuck: true } roll)
            {
                continue;
            }

            if (best is not { } previousBest || roll.CompareLuckTo(previousBest.Measurement) > 0)
            {
                best = (run.Id, roll);
            }

            if (worst is not { } previousWorst || roll.CompareLuckTo(previousWorst.Measurement) < 0)
            {
                worst = (run.Id, roll);
            }
        }

        var partnerRuns = await CoopPartnerRuns.LoadAsync(db, seasonIds, playerIds, ct);
        var completedAsPartner = partnerRuns.FindAll(run => run.Status == RunStatus.Completed);
        var totalHours = own.Sum(run => run.Hours ?? 0) + completedAsPartner.Sum(run => run.Hours ?? 0);
        var hits = await db.Events.AsNoTracking()
            .Where(row => seasonIds.Contains(row.SeasonId) && row.Type == s_attackType && row.UndoneByEventId == null)
            .Select(row => new { row.SeasonId, row.Type, row.Version, row.Data }).ToListAsync(ct);
        var attacks = hits.GroupBy(row => row.SeasonId)
            .Select(group => AttackStatistics.ForPlayer(memberships[group.Key],
                group.Select(row => EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)))))
            .ToArray();
        return new(own.Count + completedAsPartner.Count, totalHours, own.Max(run => run.Rating), own.Min(run => run.Rating),
            attacks.Sum(player => player.AttacksMade), attacks.Sum(player => player.AttacksReceived), ViewOf(best), ViewOf(worst));
    }

    private static SeasonRollView? ViewOf((Guid RunId, RollMeasurement Measurement)? roll) =>
        roll is { Measurement.Luck: { } luck } result
            ? new(result.RunId, result.Measurement.Sum, result.Measurement.Minimum, result.Measurement.Maximum, luck)
            : null;

    private sealed record RunStatistics(
        int CompletedRuns, decimal TotalHours, int? BestRating, int? WorstRating,
        int AttacksMade, int AttacksReceived, SeasonRollView? BestRoll, SeasonRollView? WorstRoll);
}
