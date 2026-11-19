using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
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

public sealed record InviteCoopRequest(Guid CommandId, Guid PartnerId);

/// <summary>Missing accept is invalid input, not a silent decline.</summary>
public sealed record AnswerInvitationRequest(Guid CommandId, bool? Accept);

/// <summary>
/// A participant's own incoming or outgoing invitation. A pending board response also has its effective deadline,
/// the earlier of the invitation and the matched request's scheduled deadlines (D-742).
/// </summary>
public sealed record InteractionView(
    Guid Id, InteractionKind Kind, Guid FromPlayerId, Guid ToPlayerId, Guid? RunId,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, InteractionStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? EffectiveExpiresAt = null);

/// <summary>The live shared run of the caller, also after new coop invitations have been switched off.</summary>
public sealed record CoopView(Guid RunId, Guid RollerId, Guid PartnerId, DateTimeOffset JoinedAt);

public static class InteractionEndpoints
{
    public static void MapInteractions(this RouteGroupBuilder api)
    {
        var player = api.MapGroup("/seasons/{seasonId:guid}").WithTags("Interactions").RequireAuthorization(Policies.Player);
        player.MapGet("/interactions", ListAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit).WithReadErrors().ProducesProblem(StatusCodes.Status403Forbidden);
        player.MapGet("/coop", CoopAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit).WithReadErrors().ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces<CoopView>()
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                var media = operation.Responses!["200"].Content!["application/json"];
                media.Schema = new Microsoft.OpenApi.OpenApiSchema
                {
                    AnyOf = [media.Schema!, new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.Null }],
                };
                return Task.CompletedTask;
            });
        player.MapPost("/coop/invite", InviteAsync)
            .RequireRateLimiting(AppSetup.InteractionRateLimit).WithActionErrors().ProducesProblem(StatusCodes.Status429TooManyRequests);
        player.MapPost("/interactions/{interactionId:guid}/answer", AnswerAsync)
            .RequireRateLimiting(AppSetup.InteractionRateLimit).WithActionErrors().ProducesProblem(StatusCodes.Status429TooManyRequests);
        player.MapPost("/interactions/{interactionId:guid}/withdraw", (Guid seasonId, Guid interactionId, SeasonActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                interactionId == Guid.Empty
                    ? Task.FromResult<ActionResult>(SeasonEndpoints.Invalid("interactionId", "An invitation id is required."))
                    : SeasonEndpoints.ActAsync(seasonId, request.CommandId, user, db, bus, id => new WithdrawInvitation(id, interactionId), ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit).WithActionErrors().ProducesProblem(StatusCodes.Status429TooManyRequests);
        player.MapPost("/coop/leave", (Guid seasonId, SeasonActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SeasonEndpoints.ActAsync(seasonId, request.CommandId, user, db, bus, id => new LeaveCoop(id), ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit).WithActionErrors().ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static Task<ActionResult> InviteAsync(
        Guid seasonId, InviteCoopRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
        request.PartnerId == Guid.Empty
            ? Task.FromResult<ActionResult>(SeasonEndpoints.Invalid("partnerId", "A partner id is required."))
            : SeasonEndpoints.ActAsync(seasonId, request.CommandId, user, db, bus, id => new InviteToCoop(id, request.PartnerId), ct);

    private static Task<ActionResult> AnswerAsync(
        Guid seasonId, Guid interactionId, AnswerInvitationRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
        interactionId == Guid.Empty || request.Accept is null
            ? Task.FromResult<ActionResult>(SeasonEndpoints.Invalid(interactionId == Guid.Empty ? "interactionId" : "accept", "An invitation id and an explicit answer are required."))
            : SeasonEndpoints.ActAsync(seasonId, request.CommandId, user, db, bus, id => new AnswerInvitation(id, interactionId, request.Accept.Value), ct);

    private static async Task<Results<Ok<IReadOnlyList<InteractionView>>, NotFound, ForbidHttpResult>> ListAsync(
        Guid seasonId, ClaimsPrincipal user, GameEventDbContext db, CancellationToken ct)
    {
        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        if (await PlayerAsync(db, seasonId, user.UserId(), ct) is not { } player)
        {
            return TypedResults.Forbid();
        }

        // Do not erase residual invitations when new coop invitations are off: decline and withdrawal remain valid.
        var rows = await db.Interactions.AsNoTracking()
            .Where(i => i.SeasonId == seasonId && (i.FromPlayerId == player || i.ToPlayerId == player))
            .Select(i => new
            {
                Invitation = i,
                // Match PartnerBoard.OutlivedItsRequest (D-742) in the same database snapshot as the invitation.
                RequestExpiresAt = i.Kind == InteractionKind.Coop && i.Status == InteractionStatus.Pending && i.FromPlayerId != i.ToPlayerId
                    ? db.PartnerRequests.Where(r => r.SeasonId == i.SeasonId && r.RunId == i.RunId
                        && r.PlayerId == i.ToPlayerId && r.CreatedAt <= i.CreatedAt)
                        .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
                        .Select(r => (DateTimeOffset?)r.ExpiresAt).FirstOrDefault()
                    : null,
            }).ToListAsync(ct);
        IReadOnlyList<InteractionView> views = [.. rows.OrderByDescending(row => row.Invitation.CreatedAt).ThenBy(row => row.Invitation.Id)
            .Select(row =>
            {
                var i = row.Invitation;
                var effectiveExpiresAt = row.RequestExpiresAt is { } requestTerm && i.ExpiresAt < requestTerm
                    ? i.ExpiresAt
                    : row.RequestExpiresAt;
                return new InteractionView(i.Id, i.Kind, i.FromPlayerId, i.ToPlayerId, i.RunId, i.CreatedAt, i.ExpiresAt, i.Status, effectiveExpiresAt);
            })];
        return TypedResults.Ok(views);
    }

    private static async Task<Results<JsonHttpResult<JsonElement>, NotFound, ForbidHttpResult>> CoopAsync(
        Guid seasonId, ClaimsPrincipal user, GameEventDbContext db, CancellationToken ct)
    {
        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        if (await PlayerAsync(db, seasonId, user.UserId(), ct) is not { } player)
        {
            return TypedResults.Forbid();
        }

        var row = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.SeasonId == seasonId
            && r.Status == RunStatus.Playing && r.PartnerId != null && (r.PlayerId == player || r.PartnerId == player), ct);
        CoopView? view = row is null ? null : new CoopView(row.Id, row.PlayerId, row.PartnerId!.Value,
            JsonSerializer.Deserialize<CoopPartner>(row.CoopJson!, EngineJson.Options)!.JoinedAt);
        // A null reference result writes no body in Minimal APIs. A JSON null element is an actual JSON response.
        return TypedResults.Json(JsonSerializer.SerializeToElement(view, EngineJson.Options));
    }

    private static async Task<Guid?> PlayerAsync(GameEventDbContext db, Guid seasonId, Guid? userId, CancellationToken ct) =>
        await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == seasonId && p.UserId == userId)
            .Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
}
