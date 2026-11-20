using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Reactions;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

internal static class FeedReactionEndpoints
{
    public static void MapFeedReactions(RouteGroupBuilder api)
    {
        api.MapGet("/seasons/{seasonId:guid}/feed/reactions", ListAsync)
            .WithTags("Seasons").RequireAuthorization().RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        api.MapPut("/seasons/{seasonId:guid}/feed/{targetCommandId:guid}/reactions", SetAsync)
            .WithTags("Seasons").RequireAuthorization().RequireRateLimiting(AppSetup.ReviewReactionRateLimit)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult, ValidationProblem, ProblemHttpResult>> SetAsync(
        Guid seasonId, Guid targetCommandId, ReactionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (user.UserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        if (request.Invalid() is { } invalid)
        {
            return invalid;
        }

        var command = new SetFeedReaction(targetCommandId, userId, request.Emoji!, request.Selected.GetValueOrDefault());
        var result = await SeasonEndpoints.PrecheckAndSendAsync(seasonId, request.CommandId, command, userId, db, bus, ct);
        return result.Result switch
        {
            Ok<CommandResponse> => TypedResults.NoContent(),
            ProblemHttpResult p when p.ProblemDetails.Extensions.TryGetValue("code", out var code)
                && Equals(code, RejectionCodes.FeedReactionTargetUnknown) => TypedResults.NotFound(),
            ProblemHttpResult p => p,
            NotFound notFound => notFound,
            ValidationProblem problem => problem,
            _ => throw new InvalidOperationException("An unexpected result came from the reaction command."),
        };
    }

    private static async Task<Results<Ok<IReadOnlyDictionary<Guid, ReactionView[]>>, NotFound, ValidationProblem>> ListAsync(
        Guid seasonId, [FromQuery] Guid[] commandIds, ClaimsPrincipal user, GameEventDbContext db, IFeedReactionTargets targets, CancellationToken ct)
    {
        if (commandIds.Length > FeedEndpoints.MaxFeedPage)
        {
            return SeasonEndpoints.Invalid("commandIds", $"Request up to {FeedEndpoints.MaxFeedPage} feed entries at once.");
        }

        var rulesetJson = await db.Seasons.AsNoTracking().Where(s => s.Id == seasonId).Select(s => s.RulesetJson).SingleOrDefaultAsync(ct);
        if (seasonId == Guid.Empty || rulesetJson is null)
        {
            return TypedResults.NotFound();
        }

        var rules = JsonSerializer.Deserialize<Ruleset>(rulesetJson, EngineJson.Options)!;
        var reactions = rules.Features.Reactions
            ? await ReadAsync(db, targets, seasonId, commandIds.Distinct().ToArray(), rules.Social.Reactions, user.UserId(), ct)
            : new Dictionary<Guid, ReactionView[]>();
        return TypedResults.Ok(reactions);
    }

    internal static async Task<IReadOnlyDictionary<Guid, ReactionView[]>> ReadAsync(
        GameEventDbContext db, IFeedReactionTargets targets, Guid seasonId, IReadOnlyCollection<Guid> commandIds, EquatableArray<string> emojis,
        Guid? viewer, CancellationToken ct)
    {
        var eligible = (await targets.FindAsync(db, seasonId, commandIds, ct)).ToArray();
        if (eligible.Length == 0)
        {
            return new Dictionary<Guid, ReactionView[]>();
        }

        var counts = (await db.FeedReactions.AsNoTracking().Where(r => r.SeasonId == seasonId && eligible.Contains(r.TargetCommandId))
                .GroupBy(r => new { r.TargetCommandId, r.Emoji })
                .Select(group => new ReactionCount(group.Key.TargetCommandId, group.Key.Emoji, group.Count(), group.Count(r => r.UserId == viewer) > 0))
                .ToListAsync(ct)).ToDictionary(c => (c.TargetCommandId, c.Emoji));

        return eligible.ToDictionary(id => id, id => emojis.Select(emoji =>
        {
            var count = counts.GetValueOrDefault((id, emoji));
            return new ReactionView(emoji, count?.Total ?? 0, count?.Selected ?? false);
        }).ToArray());
    }

    private sealed record ReactionCount(Guid TargetCommandId, string Emoji, int Total, bool Selected);
}
