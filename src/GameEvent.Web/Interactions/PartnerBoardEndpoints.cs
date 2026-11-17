using System.Security.Claims;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Hosting;
using GameEvent.Web.Polls;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using ActionResult = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<GameEvent.Web.Seasons.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
    Microsoft.AspNetCore.Http.HttpResults.ValidationProblem,
    Microsoft.AspNetCore.Http.HttpResults.NotFound,
    Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>;

namespace GameEvent.Web.Interactions;

/// <summary>An open request for a partner to its author's running coop game.</summary>
public sealed record PartnerRequestView(
    Guid Id, Guid PlayerId, Guid RunId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, PartnerRequestStatus Status);

/// <summary>The public partner board: reads use the projection; every write goes through the command queue.</summary>
public static class PartnerBoardEndpoints
{
    public static void MapPartnerBoard(this RouteGroupBuilder api)
    {
        var seasons = api.MapGroup("/seasons/{seasonId:guid}").WithTags("PartnerBoard").RequireAuthorization();
        seasons.MapGet("/partner-requests", ListAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit).WithReadErrors();

        var player = seasons.MapGroup("/partner-requests").RequireAuthorization(Policies.Player);
        player.MapPost("", (Guid seasonId, SeasonActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SeasonEndpoints.ActAsync(seasonId, request.CommandId, user, db, bus, id => new PostPartnerRequest(id), ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit).WithActionErrors().ProducesProblem(StatusCodes.Status429TooManyRequests);
        player.MapPost("/{requestId:guid}/respond", (Guid seasonId, Guid requestId, SeasonActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                requestId == Guid.Empty
                    ? Task.FromResult<ActionResult>(SeasonEndpoints.Invalid("requestId", "A partner request id is required."))
                    : SeasonEndpoints.ActAsync(seasonId, request.CommandId, user, db, bus, id => new RespondToPartnerRequest(id, requestId), ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit).WithActionErrors().ProducesProblem(StatusCodes.Status429TooManyRequests);
        // Closing an existing request remains valid with either flag off (D-790, D-794); the engine checks its author.
        player.MapPost("/{requestId:guid}/close", (Guid seasonId, Guid requestId, SeasonActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                requestId == Guid.Empty
                    ? Task.FromResult<ActionResult>(SeasonEndpoints.Invalid("requestId", "A partner request id is required."))
                    : SeasonEndpoints.ActAsync(seasonId, request.CommandId, user, db, bus, id => new ClosePartnerRequest(id, requestId), ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit).WithActionErrors().ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<Results<Ok<IReadOnlyList<PartnerRequestView>>, NotFound, ProblemHttpResult>> ListAsync(
        Guid seasonId, GameEventDbContext db, IClock clock, CancellationToken ct)
    {
        if (await SeasonRules.OfAsync(db, seasonId, ct) is not { } rules)
        {
            return TypedResults.NotFound();
        }

        if (!rules.Features.PartnerBoard || !rules.Features.Coop)
        {
            return SeasonRules.FeatureOff("The partner board is off in this season.");
        }

        var now = clock.UtcNow;
        var rows = await db.PartnerRequests.AsNoTracking()
            .Where(r => r.SeasonId == seasonId && r.Status == PartnerRequestStatus.Open).ToListAsync(ct);
        // SQLite does not compare DateTimeOffset: filter the projected columns with the same clock as the engine.
        IReadOnlyList<PartnerRequestView> requests = [.. rows.Where(r => now < r.ExpiresAt)
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new PartnerRequestView(r.Id, r.PlayerId, r.RunId, r.CreatedAt, r.ExpiresAt, r.Status))];
        return TypedResults.Ok(requests);
    }
}
