using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Comments;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

public sealed record PostCommentRequest(Guid CommandId, Guid CommentId, string? Text);
public sealed record EditCommentRequest(Guid CommandId, string? Text);
public sealed record HideCommentRequest(Guid CommandId);
public sealed record ThreadCommentView(
    Guid Id, string Author, string Text, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, bool CanEdit, bool CanHide);
public sealed record CommentThreadView(
    IReadOnlyList<ThreadCommentView> Comments, Guid? NextBefore, bool? PostingAllowed = null);

internal static class CommentEndpoints
{
    private const int PageSize = 50;

    private readonly record struct Thread(Guid? SeasonId, Guid? GameId, CommentTarget Target)
    {
        public static Thread Feed(Guid seasonId, Guid targetId) => new(seasonId, null, new(CommentTargetKind.FeedEntry, targetId));
        public static Thread Review(Guid gameId, Guid runId) => new(null, gameId, new(CommentTargetKind.Review, runId));
        public static Thread ReviewInSeason(Guid seasonId, Guid runId) => new(seasonId, null, new(CommentTargetKind.Review, runId));
    }

    public static void MapComments(this RouteGroupBuilder api)
    {
        var feed = api.MapGroup("/seasons/{seasonId:guid}/feed/{targetCommandId:guid}/comments")
            .WithTags("Seasons").RequireAuthorization();
        feed.MapGet("", (Guid seasonId, Guid targetCommandId, Guid? before, ClaimsPrincipal user, HttpResponse response, GameEventDbContext db,
                IFeedReactionTargets targets, CancellationToken ct) =>
            ListAsync(Thread.Feed(seasonId, targetCommandId), before, user, response, db, targets, ct))
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit);
        feed.MapPost("", (Guid seasonId, Guid targetCommandId, PostCommentRequest request, ClaimsPrincipal user,
                GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            WriteAsync(Thread.Feed(seasonId, targetCommandId), request.CommandId, request.CommentId, request.Text, CommentAction.Post, user, db, bus, ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit);
        feed.MapPut("/{commentId:guid}", (Guid seasonId, Guid targetCommandId, Guid commentId, EditCommentRequest request, ClaimsPrincipal user,
                GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            WriteAsync(Thread.Feed(seasonId, targetCommandId), request.CommandId, commentId, request.Text, CommentAction.Edit, user, db, bus, ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit);
        feed.MapDelete("/{commentId:guid}", (Guid seasonId, Guid targetCommandId, Guid commentId, Guid commandId, ClaimsPrincipal user,
                GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            WriteAsync(Thread.Feed(seasonId, targetCommandId), commandId, commentId, null, CommentAction.Delete, user, db, bus, ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit);

        var review = api.MapGroup("/pool/{gameId:guid}/runs/{runId:guid}/review-comments").WithTags("Pool").RequireAuthorization();
        review.MapGet("", (Guid gameId, Guid runId, Guid? before, ClaimsPrincipal user, HttpResponse response, GameEventDbContext db,
                IFeedReactionTargets targets, CancellationToken ct) =>
            ListAsync(Thread.Review(gameId, runId), before, user, response, db, targets, ct))
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit);
        review.MapPost("", (Guid gameId, Guid runId, PostCommentRequest request, ClaimsPrincipal user,
                GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            WriteAsync(Thread.Review(gameId, runId), request.CommandId, request.CommentId, request.Text, CommentAction.Post, user, db, bus, ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit);
        review.MapPut("/{commentId:guid}", (Guid gameId, Guid runId, Guid commentId, EditCommentRequest request, ClaimsPrincipal user,
                GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            WriteAsync(Thread.Review(gameId, runId), request.CommandId, commentId, request.Text, CommentAction.Edit, user, db, bus, ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit);
        review.MapDelete("/{commentId:guid}", (Guid gameId, Guid runId, Guid commentId, Guid commandId, ClaimsPrincipal user,
                GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            WriteAsync(Thread.Review(gameId, runId), commandId, commentId, null, CommentAction.Delete, user, db, bus, ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit);

        var admin = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);
        admin.MapPost("/feed/{targetCommandId:guid}/comments/{commentId:guid}/hide",
            (Guid seasonId, Guid targetCommandId, Guid commentId, HideCommentRequest request, ClaimsPrincipal user,
                GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            WriteAsync(Thread.Feed(seasonId, targetCommandId), request.CommandId, commentId, null, CommentAction.Hide, user, db, bus, ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit);
        admin.MapPost("/reviews/{runId:guid}/comments/{commentId:guid}/hide",
            (Guid seasonId, Guid runId, Guid commentId, HideCommentRequest request, ClaimsPrincipal user,
                GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            WriteAsync(Thread.ReviewInSeason(seasonId, runId), request.CommandId, commentId, null, CommentAction.Hide, user, db, bus, ct))
            .RequireRateLimiting(AppSetup.InteractionRateLimit);
    }

    private static async Task<Results<Ok<CommentThreadView>, UnauthorizedHttpResult, NotFound, ProblemHttpResult>> ListAsync(
        Thread thread, Guid? before, ClaimsPrincipal user, HttpResponse response, GameEventDbContext db,
        IFeedReactionTargets targets, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        if (user.UserId() is not { } viewer)
        {
            return TypedResults.Unauthorized();
        }

        if (await FindSeasonAsync(thread, db, ct) is not { } seasonId
            || await db.Seasons.AsNoTracking().Where(s => s.Id == seasonId).Select(s => s.RulesetJson).SingleOrDefaultAsync(ct) is not { } rules)
        {
            return TypedResults.NotFound();
        }

        var ruleset = JsonSerializer.Deserialize<Ruleset>(rules, EngineJson.Options);
        if (ruleset is null || !ruleset.Features.Comments)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Comments are disabled for this season.",
                extensions: new Dictionary<string, object?> { ["code"] = RejectionCodes.FeatureDisabled });
        }

        if (thread.Target.Kind == CommentTargetKind.FeedEntry
            && (await targets.FindAsync(db, seasonId, [thread.Target.Id], ct)).Count == 0)
        {
            return TypedResults.NotFound();
        }

        var rows = db.Set<CommentRecord>().AsNoTracking().Where(row =>
            row.SeasonId == seasonId && row.TargetKind == thread.Target.Kind && row.TargetId == thread.Target.Id);
        var visible = rows.Visible();
        if (before is { } cursor)
        {
            var anchor = await rows.Where(row => row.CommentId == cursor)
                .Select(row => new { row.CreatedAt, row.CommentId }).SingleOrDefaultAsync(ct);
            if (anchor is null)
            {
                return TypedResults.NotFound();
            }

            visible = visible.Where(row => row.CreatedAt < anchor.CreatedAt
                || (row.CreatedAt == anchor.CreatedAt && row.CommentId.CompareTo(anchor.CommentId) < 0));
        }

        var canHide = user.IsInRole(nameof(Infrastructure.Accounts.Role.Admin));
        var comments = await visible.Join(db.Users.AsNoTracking(), row => row.UserId, author => author.Id, (row, author) => new { row, author.Name })
            .OrderByDescending(entry => entry.row.CreatedAt).ThenByDescending(entry => entry.row.CommentId)
            .Take(PageSize + 1)
            .Select(entry => new ThreadCommentView(entry.row.CommentId, entry.Name, entry.row.Text,
                entry.row.CreatedAt, entry.row.EditedAt, entry.row.UserId == viewer, canHide))
            .ToListAsync(ct);
        var nextBefore = comments.Count > PageSize ? (Guid?)comments[PageSize - 1].Id : null;
        return TypedResults.Ok(new CommentThreadView(
            comments.Take(PageSize).Reverse().ToArray(), nextBefore, ruleset.Social.CommentsPerDay > 0));
    }

    private enum CommentAction { Post, Edit, Delete, Hide }

    private static async Task<Results<NoContent, UnauthorizedHttpResult, NotFound, ValidationProblem, ProblemHttpResult>> WriteAsync(
        Thread thread, Guid commandId, Guid commentId, string? text, CommentAction action, ClaimsPrincipal user,
        GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (user.UserId() is not { } actor)
        {
            return TypedResults.Unauthorized();
        }

        if (await FindSeasonAsync(thread, db, ct) is not { } seasonId)
        {
            return TypedResults.NotFound();
        }

        ICommentCommand command = action switch
        {
            CommentAction.Post => new PostComment(commentId, thread.Target, actor, text),
            CommentAction.Edit => new EditComment(commentId, thread.Target, actor, text),
            CommentAction.Delete => new DeleteComment(commentId, thread.Target, actor),
            CommentAction.Hide => new HideComment(commentId, thread.Target),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var outcome = await SeasonEndpoints.PrecheckAndSendAsync(seasonId, commandId, command, actor, db, bus, ct);
        switch (outcome.Result)
        {
            case Ok<CommandResponse>:
                return TypedResults.NoContent();
            case NotFound notFound:
                return notFound;
            case ValidationProblem invalid:
                return invalid;
            case ProblemHttpResult problem:
                return Translate(problem);
            default:
                throw new InvalidOperationException("An unexpected result came from the comment command.");
        }
    }

    private static Results<NoContent, UnauthorizedHttpResult, NotFound, ValidationProblem, ProblemHttpResult> Translate(ProblemHttpResult problem)
    {
        if (problem.ProblemDetails.Extensions.TryGetValue("code", out var code))
        {
            switch (code)
            {
                case CommentRejectionCodes.TargetUnknown:
                case CommentRejectionCodes.NotFound:
                    return TypedResults.NotFound();
                case CommentRejectionCodes.NotYours:
                    problem.ProblemDetails.Status = StatusCodes.Status403Forbidden;
                    break;
                case CommentRejectionCodes.Empty:
                case CommentRejectionCodes.TooLong:
                    return SeasonEndpoints.Invalid("text", problem.ProblemDetails.Detail!);
                case CommentRejectionCodes.InvalidId:
                    return SeasonEndpoints.Invalid("commentId", problem.ProblemDetails.Detail!);
            }
        }

        return problem;
    }

    private static async Task<Guid?> FindSeasonAsync(Thread thread, GameEventDbContext db, CancellationToken ct)
    {
        if (thread.Target.Kind == CommentTargetKind.FeedEntry)
        {
            return thread.SeasonId;
        }

        return await db.Reviews.AsNoTracking().Where(r => r.RunId == thread.Target.Id
                && (thread.GameId == null || r.GameId == thread.GameId)
                && (thread.SeasonId == null || r.SeasonId == thread.SeasonId)
                && db.Games.Any(g => g.Id == r.GameId && !g.IsDeleted))
            .Select(r => (Guid?)r.SeasonId).SingleOrDefaultAsync(ct);
    }
}
