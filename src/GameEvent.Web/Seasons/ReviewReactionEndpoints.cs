using System.Security.Claims;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Reactions;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

public sealed record ReactionRequest(Guid CommandId, string? Emoji, bool? Selected)
{
    internal ValidationProblem? Invalid()
    {
        if (CommandId == Guid.Empty)
        {
            return SeasonEndpoints.Invalid("commandId", "A command id is required.");
        }

        if (string.IsNullOrWhiteSpace(Emoji))
        {
            return SeasonEndpoints.Invalid("emoji", "An emoji is required.");
        }

        return Selected is null ? SeasonEndpoints.Invalid("selected", "Choose whether the reaction is selected.") : null;
    }
}

public sealed record ReactionView(string Emoji, int Count, bool Selected);

internal static class ReviewReactionEndpoints
{
    public static void MapReviewReactions(RouteGroupBuilder api)
    {
        api.MapPut("/pool/{gameId:guid}/runs/{runId:guid}/review-reactions", SetAsync)
            .WithTags("Pool").RequireAuthorization().RequireRateLimiting(AppSetup.ReviewReactionRateLimit)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult, ValidationProblem, ProblemHttpResult>> SetAsync(
        Guid gameId, Guid runId, ReactionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (user.UserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        if (request.Invalid() is { } invalid)
        {
            return invalid;
        }

        var seasonId = await db.Reviews.AsNoTracking().Where(r => r.RunId == runId && r.GameId == gameId
                && db.Games.Any(g => g.Id == gameId && !g.IsDeleted))
            .Select(r => (Guid?)r.SeasonId).SingleOrDefaultAsync(ct);
        if (seasonId is not { } season)
        {
            return TypedResults.NotFound();
        }

        var command = new SetReviewReaction(runId, userId, request.Emoji!, request.Selected.GetValueOrDefault());
        var result = await SeasonEndpoints.SendAsync(season, request.CommandId, command, userId, bus, ct);
        return result.Result switch
        {
            Ok<CommandResponse> => TypedResults.NoContent(),
            ProblemHttpResult p when p.ProblemDetails.Extensions.TryGetValue("code", out var code)
                && Equals(code, RejectionCodes.ReviewReactionReviewUnknown) => TypedResults.NotFound(),
            ProblemHttpResult p => p,
            _ => throw new InvalidOperationException("An unexpected result came from the reaction command."),
        };
    }
}
