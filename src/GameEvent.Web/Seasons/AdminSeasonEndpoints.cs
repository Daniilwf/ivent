using System.Security.Claims;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.EntityFrameworkCore;

using ActionResult = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<GameEvent.Web.Seasons.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
    Microsoft.AspNetCore.Http.HttpResults.ValidationProblem,
    Microsoft.AspNetCore.Http.HttpResults.NotFound,
    Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>;

namespace GameEvent.Web.Seasons;

/// <summary>The admin moves the season to its next status (SE1, D-101): closing early, finishing, archiving.</summary>
public sealed record SeasonStatusRequest(Guid CommandId, SeasonStatus? To);

/// <summary>The admin sets or removes (null) the deadline; only in draft or active (D-101).</summary>
public sealed record SeasonDeadlineRequest(Guid CommandId, DateTimeOffset? Deadline);

/// <summary>A pending manual effect of any player, oldest first, for the admin to resolve (D-102).</summary>
public sealed record AdminManualEffectView(
    Guid Id, Guid PlayerId, string PlayerName, Engine.Rulesets.EventKind DrawEvent, Engine.Effects.ManualEffectSource Source, Guid? RunId);

/// <summary>The admin's season actions: the lifecycle (D-101) and resolving manual effects (D-102).</summary>
public static class AdminSeasonEndpoints
{
    public static void MapAdminSeasons(this RouteGroupBuilder api)
    {
        var season = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);

        season.MapPost("/status", (Guid seasonId, SeasonStatusRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            request.To is not { } to || !Enum.IsDefined(to)
                ? Task.FromResult<ActionResult>(
                    TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["A known season status is required."] }))
                : SendAsync(seasonId, request.CommandId, new ChangeSeasonStatus(to), user, db, bus, ct))
            .WithActionErrors();

        season.MapGet("/effects", GetEffectsAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Any player's effect; the comment is required (D-89, D-102).
        season.MapPost("/effects/{effectId:guid}/resolve", (Guid seasonId, Guid effectId, ResolveEffectRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            SeasonEndpoints.ResolveInvalid(request) is { } invalid
                ? Task.FromResult<ActionResult>(invalid)
                : SendAsync(seasonId, request.CommandId, new Engine.Effects.ResolveManualEffect(effectId, request.Outcome!.Value, request.Comment, PlayerId: null), user, db, bus, ct))
            .WithActionErrors();

        season.MapPost("/deadline", (Guid seasonId, SeasonDeadlineRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            SendAsync(seasonId, request.CommandId, new SetSeasonDeadline(request.Deadline), user, db, bus, ct))
            .WithActionErrors();
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Results<
        Microsoft.AspNetCore.Http.HttpResults.Ok<IReadOnlyList<AdminManualEffectView>>,
        Microsoft.AspNetCore.Http.HttpResults.NotFound>> GetEffectsAsync(Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        // Effect ids are time-ordered (UUID v7), so their order is the order they were created in.
        IReadOnlyList<AdminManualEffectView> effects = await db.ManualEffects.AsNoTracking()
            .Where(x => x.SeasonId == seasonId)
            .Join(db.SeasonPlayers, x => x.PlayerId, p => p.Id, (x, p) => new { x, p.Name })
            .OrderBy(r => r.x.Id)
            .Select(r => new AdminManualEffectView(r.x.Id, r.x.PlayerId, r.Name, r.x.DrawEvent, r.x.Source, r.x.RunId))
            .ToListAsync(ct);
        return TypedResults.Ok(effects);
    }

    private static async Task<ActionResult> SendAsync(
        Guid seasonId, Guid commandId, Engine.Kernel.ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        return await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);
    }
}
