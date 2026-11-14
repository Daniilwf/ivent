using System.Security.Claims;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;
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

    // The proofs live on the pending effects of the season folded from the log; the rate limit caps the cost
    private static async Task<Results<Ok<IReadOnlyList<IrlProofQueueItemView>>, NotFound>> GetQueueAsync(
        Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
        if (!state.IsCreated)
        {
            return TypedResults.NotFound();
        }

        IReadOnlyList<IrlProofQueueItemView> items =
        [
            .. state.ManualEffects.Values
                .Where(e => e.IrlProof is { Status: IrlProofStatus.Pending })
                .OrderBy(e => e.IrlProof!.SubmittedAt)
                .ThenBy(e => e.EffectId)
                .Select(e =>
                {
                    var definition = e.ObjectId is { } id ? state.Catalog.Find(id) : null;
                    var proof = e.IrlProof!;
                    return new IrlProofQueueItemView(
                        e.EffectId,
                        e.PlayerId,
                        state.Players.TryGetValue(e.PlayerId, out var player) ? player.Name : "",
                        e.ObjectId ?? "",
                        definition?.Name ?? e.ObjectId ?? "",
                        definition?.Description ?? "",
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
