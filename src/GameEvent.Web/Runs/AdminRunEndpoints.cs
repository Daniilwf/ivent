using System.Security.Claims;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;

using ActionResult = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<GameEvent.Web.Seasons.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
    Microsoft.AspNetCore.Http.HttpResults.ValidationProblem,
    Microsoft.AspNetCore.Http.HttpResults.NotFound,
    Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>;

namespace GameEvent.Web.Runs;

/// <summary>The admin corrects the hours of a completed run (D-97).</summary>
public sealed record HoursCorrectionRequest(Guid CommandId, decimal? Hours, string? Comment);

/// <summary>The admin changes the difficulty of a completed run, usually by the proof (Q-5, D-97).</summary>
public sealed record DifficultyChangeRequest(Guid CommandId, Engine.Runs.Difficulty? Difficulty, string? Comment);

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
                ? Task.FromResult<ActionResult>(invalid)
                : SendAsync(seasonId, request.CommandId, new TechReroll(playerId, request.Reason!.Value, request.Comment, ByAdmin: true), user, db, bus, ct))
            .WithActionErrors();

        season.MapPost("/runs/{runId:guid}/hours", (Guid seasonId, Guid runId, HoursCorrectionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            request.Hours is not { } hours || hours <= 0 || hours > SeasonEndpoints.MaxEstimatedHours
                ? Invalid("hours", $"Hours must be greater than 0 and at most {SeasonEndpoints.MaxEstimatedHours}.")
                : CommentInvalid(request.Comment) is { } badComment
                    ? Task.FromResult<ActionResult>(badComment)
                    : SendAsync(seasonId, request.CommandId, new CorrectRunHours(runId, hours, request.Comment!), user, db, bus, ct))
            .WithActionErrors();

        season.MapPost("/runs/{runId:guid}/difficulty", (Guid seasonId, Guid runId, DifficultyChangeRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            request.Difficulty is not { } difficulty || !Enum.IsDefined(difficulty)
                ? Invalid("difficulty", "A known difficulty is required.")
                : CommentInvalid(request.Comment) is { } badComment
                    ? Task.FromResult<ActionResult>(badComment)
                    : SendAsync(seasonId, request.CommandId, new ChangeRunDifficulty(runId, difficulty, request.Comment!), user, db, bus, ct))
            .WithActionErrors();

        season.MapPost("/runs/{runId:guid}/convert-to-drop", (Guid seasonId, Guid runId, ConvertToDropRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            request.Comment is null || request.Comment.Length > SeasonEndpoints.MaxCommentLength
                ? Task.FromResult<ActionResult>(
                    TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["comment"] = [$"A comment of at most {SeasonEndpoints.MaxCommentLength} characters is required."],
                    }))
                : SendAsync(seasonId, request.CommandId, new ConvertTechRerollToDrop(runId, request.Comment), user, db, bus, ct))
            .WithActionErrors();
    }

    private static Task<ActionResult> Invalid(string field, string message) =>
        Task.FromResult<ActionResult>(TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }));

    // A missing comment or one over the limit is a malformed request; a blank one is the engine's rule (409).
    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? CommentInvalid(string? comment) =>
        comment is null || comment.Length > SeasonEndpoints.MaxCommentLength
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["comment"] = [$"A comment of at most {SeasonEndpoints.MaxCommentLength} characters is required."],
            })
            : null;

    private static async Task<ActionResult> SendAsync(
        Guid seasonId, Guid commandId, Engine.Kernel.ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        return await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);
    }
}
