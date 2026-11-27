using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Files;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Suggestions;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
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

namespace GameEvent.Web.Suggestions;

/// <summary>A player's idea; a game uses the same card as the pool. The author comes from the session.</summary>
public sealed record SubmitSuggestionRequest(Guid CommandId, SuggestionKind? Kind, string? Text, GameCard? Game = null);

/// <summary>The admin's explicit decision; a missing answer must not silently reject a suggestion.</summary>
public sealed record ReviewSuggestionRequest(Guid CommandId, bool? Approve, string? Comment = null);

/// <summary>A suggestion and the moderation visible to its author and the admin, without an account id.</summary>
public sealed record SuggestionView(
    Guid Id, Guid PlayerId, SuggestionKind Kind, string Text, GameCard? Game, DateTimeOffset SubmittedAt, SuggestionStatus Status, string? Comment);

/// <summary>The private suggestion box: players read their own ideas; the admin reads and moderates the season's list.</summary>
public static class SuggestionEndpoints
{
    public static void MapSuggestions(this RouteGroupBuilder api)
    {
        var player = api.MapGroup("/seasons/{seasonId:guid}/suggestions").WithTags("Suggestions").RequireAuthorization(Policies.Player);
        player.MapGet("", MineAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit).WithReadErrors().ProducesProblem(StatusCodes.Status403Forbidden);
        player.MapPost("", SubmitAsync)
            .RequireRateLimiting(AppSetup.InteractionRateLimit).WithActionErrors().ProducesProblem(StatusCodes.Status429TooManyRequests);

        var admin = api.MapGroup("/admin/seasons/{seasonId:guid}/suggestions").WithTags("Admin").RequireAuthorization(Policies.Admin);
        admin.MapGet("", AllAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit).WithReadErrors().ProducesProblem(StatusCodes.Status403Forbidden);
        admin.MapPost("/{suggestionId:guid}/review", ReviewAsync).WithActionErrors();
    }

    private static async Task<Results<Ok<IReadOnlyList<SuggestionView>>, NotFound, ForbidHttpResult, ProblemHttpResult>> MineAsync(
        Guid seasonId, ClaimsPrincipal user, GameEventDbContext db, CancellationToken ct)
    {
        if (await SeasonRules.OfAsync(db, seasonId, ct) is not { } rules)
        {
            return TypedResults.NotFound();
        }

        var userId = user.UserId();
        var playerId = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == seasonId && p.UserId == userId)
            .Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
        if (playerId is null)
        {
            return TypedResults.Forbid();
        }

        if (!rules.Features.Suggestions)
        {
            return SeasonRules.FeatureOff("The suggestion box is off in this season.");
        }

        var rows = await db.Suggestions.AsNoTracking().Where(s => s.SeasonId == seasonId && s.PlayerId == playerId).ToListAsync(ct);
        return TypedResults.Ok(Views(rows));
    }

    private static async Task<Results<Ok<IReadOnlyList<SuggestionView>>, NotFound, ProblemHttpResult>> AllAsync(
        Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        if (await SeasonRules.OfAsync(db, seasonId, ct) is not { } rules)
        {
            return TypedResults.NotFound();
        }

        if (!rules.Features.Suggestions)
        {
            return SeasonRules.FeatureOff("The suggestion box is off in this season.");
        }

        return TypedResults.Ok(Views(await db.Suggestions.AsNoTracking().Where(s => s.SeasonId == seasonId).ToListAsync(ct)));
    }

    private static IReadOnlyList<SuggestionView> Views(IReadOnlyList<SuggestionRecord> rows) =>
        [.. rows.OrderByDescending(s => s.SubmittedAt).ThenBy(s => s.Id)
            .Select(s => new SuggestionView(s.Id, s.PlayerId, s.Kind, s.Text,
                s.GameJson is null ? null : JsonSerializer.Deserialize<GameCard>(s.GameJson, EngineJson.Options),
                s.SubmittedAt, s.Status, s.Comment))];

    private static Task<ActionResult> SubmitAsync(
        Guid seasonId, SubmitSuggestionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (request.Kind is not { } kind || !Enum.IsDefined(kind))
        {
            return Task.FromResult<ActionResult>(SeasonEndpoints.Invalid("kind", "A suggestion kind is «game», «event» or «item»."));
        }

        return SeasonEndpoints.ActAsync(seasonId, request.CommandId, user, db, bus,
            id => new SubmitSuggestion(id, kind, request.Text ?? "", request.Game), ct, async () =>
            {
                // A private file or another user's upload must never become the public cover of an approved game.
                if (request.Game?.CoverFileId is { } cover && !await db.Files.AsNoTracking().AnyAsync(
                    f => f.Id == cover && !f.IsDeleted && f.Kind == FileKind.Upload && f.OwnerId == user.UserId(), ct))
                {
                    return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                        title: "The cover is unavailable.", detail: "The cover is one of your own live uploads.",
                        extensions: new Dictionary<string, object?> { ["code"] = PoolRules.CoverUnknown });
                }

                return null;
            });
    }

    private static Task<ActionResult> ReviewAsync(
        Guid seasonId, Guid suggestionId, ReviewSuggestionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (suggestionId == Guid.Empty || request.Approve is null)
        {
            return Task.FromResult<ActionResult>(SeasonEndpoints.Invalid(suggestionId == Guid.Empty ? "suggestionId" : "approve",
                "A suggestion id and an explicit decision are required."));
        }

        // D-813/D-817: the queue adds an approved game to the pool before returning, including on a duplicate review.
        return SeasonEndpoints.PrecheckAndSendAsync(seasonId, request.CommandId,
            new ReviewSuggestion(suggestionId, request.Approve.Value, request.Comment), user.UserId(), db, bus, ct);
    }
}
