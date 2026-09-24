using System.Security.Claims;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Runs;

/// <summary>An admin comment on a change to a player's runs; required, the log shows it.</summary>
public sealed record ConvertToDropRequest(Guid CommandId, string? Comment);

/// <summary>Admin actions on runs (D-11, D-94): tech reroll after the window, turning a tech reroll into a drop.</summary>
public static class AdminRunEndpoints
{
    public static void MapAdminRuns(this RouteGroupBuilder api)
    {
        var season = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);

        // On the player's behalf and past the window; ByAdmin is set here only, never from a player request.
        season.MapPost("/players/{playerId:guid}/tech-reroll", (Guid seasonId, Guid playerId, TechRerollRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            SeasonEndpoints.TechRerollInvalid(request) is { } invalid
                ? Task.FromResult<Results<Ok<CommandResponse>, ProblemHttpResult, ValidationProblem, NotFound, ForbidHttpResult>>(invalid)
                : SendAsync(seasonId, request.CommandId, new TechReroll(playerId, request.Reason!.Value, request.Comment, ByAdmin: true), user, db, bus, ct))
            .WithActionErrors();

        season.MapPost("/runs/{runId:guid}/convert-to-drop", (Guid seasonId, Guid runId, ConvertToDropRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            request.Comment is null || request.Comment.Length > SeasonEndpoints.MaxCommentLength
                ? Task.FromResult<Results<Ok<CommandResponse>, ProblemHttpResult, ValidationProblem, NotFound, ForbidHttpResult>>(
                    TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["comment"] = [$"A comment of at most {SeasonEndpoints.MaxCommentLength} characters is required."],
                    }))
                : SendAsync(seasonId, request.CommandId, new ConvertTechRerollToDrop(runId, request.Comment), user, db, bus, ct))
            .WithActionErrors();
    }

    private static async Task<Results<Ok<CommandResponse>, ProblemHttpResult, ValidationProblem, NotFound, ForbidHttpResult>> SendAsync(
        Guid seasonId, Guid commandId, Engine.Kernel.ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (commandId == Guid.Empty)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["commandId"] = ["A command id is required."] });
        }

        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        return await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);
    }
}
