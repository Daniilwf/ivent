using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ActionResult = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<GameEvent.Web.Seasons.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
    Microsoft.AspNetCore.Http.HttpResults.ValidationProblem,
    Microsoft.AspNetCore.Http.HttpResults.NotFound,
    Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>;

namespace GameEvent.Web.Proofs;

/// <summary>
/// An IRL proof waiting for the admin (D-509, D-820), oldest first: whose, the event (its card from the content in force),
/// the player's own pictures and the https link — shown, never opened by the server — and when it was sent.
/// </summary>
public sealed record IrlProofQueueItemView(
    Guid EffectId,
    Guid PlayerId,
    string PlayerName,
    string ObjectId,
    string Name,
    string Description,
    IReadOnlyList<Files.FileLinkView> Files,
    string? Link,
    DateTimeOffset SubmittedAt);

/// <summary>
/// The admin's check of an IRL proof (D-509): <c>approved</c> resolves the effect «применено» and gives nothing; a reject
/// explains itself in <c>comment</c> (required then, at most 500 characters) and the player sends a new proof.
/// </summary>
public sealed record IrlProofReviewRequest(Guid CommandId, bool? Approved, string? Comment = null);

/// <summary>The admin's queue of IRL proofs and their check (SPEC «IRL-ивенты», D-686: only the admin checks).</summary>
public static class AdminIrlProofEndpoints
{
    public static void MapAdminIrlProofs(this RouteGroupBuilder api)
    {
        var season = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);

        season.MapGet("/irl-proofs", GetQueueAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        season.MapPost("/effects/{effectId:guid}/irl-proof/review", (Guid seasonId, Guid effectId, IrlProofReviewRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            request.Approved is not { } approved
                ? Invalid("approved", "A verdict is required: approved true or false.")
                : request.Comment?.Length > Limits.MaxCommentLength
                    ? Invalid("comment", $"At most {Limits.MaxCommentLength} characters.")
                    : SendAsync(seasonId, request.CommandId, new ReviewIrlProof(effectId, approved, request.Comment), user, db, bus, ct))
            .WithActionErrors();
    }

    private static async Task<Results<Ok<IReadOnlyList<IrlProofQueueItemView>>, NotFound>> GetQueueAsync(
        Guid seasonId, GameEventDbContext db, ContentCards contentCards, CancellationToken ct)
    {
        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        // Only effects with a stored proof need decoding. Proof status and submission order are read in memory.
        var rows = await db.ManualEffects.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.IrlProofJson != null)
            .Join(db.SeasonPlayers, effect => effect.PlayerId, player => player.Id, (effect, player) => new
            {
                effect.Id,
                effect.PlayerId,
                PlayerName = player.Name,
                effect.ObjectId,
                effect.IrlProofJson,
            })
            .ToListAsync(ct);
        var pending = rows.Select(row => (row.Id, row.PlayerId, row.PlayerName, row.ObjectId,
                Proof: JsonSerializer.Deserialize<IrlProofState>(row.IrlProofJson!, EngineJson.Options)))
            .Where(item => item.Proof is { Status: IrlProofStatus.Pending })
            .ToList();
        if (pending.Count == 0)
        {
            return TypedResults.Ok<IReadOnlyList<IrlProofQueueItemView>>([]);
        }

        var cards = await contentCards.OfAsync(db, seasonId, ct);
        IReadOnlyList<IrlProofQueueItemView> items =
        [
            .. pending.OrderBy(item => item.Proof!.SubmittedAt).ThenBy(item => item.Id)
                .Select(item =>
                {
                    var card = item.ObjectId is { } id ? cards.GetValueOrDefault(id) : null;
                    var proof = item.Proof!;
                    return new IrlProofQueueItemView(
                        item.Id,
                        item.PlayerId,
                        item.PlayerName,
                        item.ObjectId ?? "",
                        card?.Name ?? item.ObjectId ?? "",
                        card?.Description ?? "",
                        [.. proof.Files.Select(Files.FileLinkView.Of)],
                        proof.Link,
                        proof.SubmittedAt);
                }),
        ];
        return TypedResults.Ok(items);
    }

    private static Task<ActionResult> Invalid(string field, string message) =>
        Task.FromResult<ActionResult>(TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }));

    private static async Task<ActionResult> SendAsync(
        Guid seasonId, Guid commandId, ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
        await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);
}
