using System.Security.Claims;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;

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

/// <summary>The admin's season lifecycle actions (D-101).</summary>
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

        season.MapPost("/deadline", (Guid seasonId, SeasonDeadlineRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            SendAsync(seasonId, request.CommandId, new SetSeasonDeadline(request.Deadline), user, db, bus, ct))
            .WithActionErrors();
    }

    private static async Task<ActionResult> SendAsync(
        Guid seasonId, Guid commandId, Engine.Kernel.ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        return await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);
    }
}
