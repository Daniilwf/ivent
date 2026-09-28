using System.Security.Claims;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
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
/// A run to check, in queue order (D-98, Q-3): the runs that decide a finish on top (<c>decidesFinish</c>), then by
/// completion time; with the claimed difficulty, the counted hours and the dice total, what an approval at a lower
/// difficulty or a reject changes. <c>reachedFinish</c>: this run's latest move stands on the finish.
/// <c>rollClosed</c>: the player's next roll is closed by the limit of unchecked runs (D-134) until the admin checks one.
/// </summary>
public sealed record ProofQueueItemView(
    Guid RunId,
    Guid PlayerId,
    string PlayerName,
    string GameTitle,
    DateTimeOffset? CompletedAt,
    bool ReachedFinish,
    ProofStatus? Status,
    IReadOnlyList<string> Links,
    string? Note,
    string? WitnessName,
    Difficulty? Difficulty,
    decimal? Hours,
    int DiceTotal,
    bool DecidesFinish,
    IReadOnlyList<Files.FileLinkView> Files,
    bool RollClosed);

/// <summary>Approve a run: with its proof, or without one («без скрина», a comment then); a lower proven difficulty.</summary>
public sealed record ApproveProofRequest(Guid CommandId, Difficulty? Difficulty = null, string? Comment = null);

/// <summary>Reject a run: its points, cells and coins are taken back (D-15, D-98).</summary>
public sealed record RejectProofRequest(Guid CommandId, string? Comment);

/// <summary>The admin's proof queue and checks (SPEC «Админка»: очередь пруфов).</summary>
public static class AdminProofEndpoints
{
    public static void MapAdminProofs(this RouteGroupBuilder api)
    {
        var season = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);

        season.MapGet("/proofs", GetQueueAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        season.MapPost("/runs/{runId:guid}/approve", (Guid seasonId, Guid runId, ApproveProofRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            request.Difficulty is { } difficulty && !Enum.IsDefined(difficulty)
                ? Invalid("difficulty", "A known difficulty is required.")
                : request.Comment?.Length > SeasonEndpoints.MaxCommentLength
                    ? Invalid("comment", $"At most {SeasonEndpoints.MaxCommentLength} characters.")
                    : SendAsync(seasonId, request.CommandId, new ApproveProof(runId, request.Difficulty, request.Comment), user, db, bus, ct))
            .WithActionErrors();

        season.MapPost("/runs/{runId:guid}/reject", (Guid seasonId, Guid runId, RejectProofRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            request.Comment is null || request.Comment.Length > SeasonEndpoints.MaxCommentLength
                ? Invalid("comment", $"A comment of at most {SeasonEndpoints.MaxCommentLength} characters is required.")
                : SendAsync(seasonId, request.CommandId, new RejectProof(runId, request.Comment), user, db, bus, ct))
            .WithActionErrors();
    }

    // The order is the engine's own (ProofReviewOrder) over the season folded from the log; the rate limit caps the cost.
    private static async Task<Results<Ok<IReadOnlyList<ProofQueueItemView>>, NotFound>> GetQueueAsync(
        Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
        if (!state.IsCreated)
        {
            return TypedResults.NotFound();
        }

        var order = ProofReviewOrder.Order(state);
        var gameIds = order.Select(id => state.Runs[id].GameId).Distinct().ToList();
        var titles = await db.Games.AsNoTracking().Where(g => gameIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Title, ct);
        IReadOnlyList<ProofQueueItemView> items =
        [
            .. order.Select(id =>
            {
                var run = state.Runs[id];
                var proof = run.Proof;
                return new ProofQueueItemView(
                    run.RunId,
                    run.PlayerId,
                    state.Players[run.PlayerId].Name,
                    titles.GetValueOrDefault(run.GameId, ""),
                    run.CompletedAt,
                    run.ReachedFinish,
                    proof?.Status,
                    proof is null ? [] : [.. proof.Links],
                    proof?.Note,
                    proof?.WitnessId is { } witness ? state.Players[witness].Name : null,
                    run.Difficulty,
                    run.Hours,
                    RunTotal.Of(run.Dice, run.ChallengeDice, run.Snapshot),
                    ProofReviewOrder.DecidesFinish(state, run),
                    proof is null ? [] : [.. proof.Files.Select(Files.FileLinkView.Of)],
                    Engine.Rolls.UncheckedRuns.ClosesRoll(state, run.PlayerId));
            }),
        ];
        return TypedResults.Ok(items);
    }

    private static Task<ActionResult> Invalid(string field, string message) =>
        Task.FromResult<ActionResult>(TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }));

    private static async Task<ActionResult> SendAsync(
        Guid seasonId, Guid commandId, Engine.Kernel.ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
        await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);
}
