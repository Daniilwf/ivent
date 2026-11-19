using System.Security.Claims;
using GameEvent.Engine.Duels;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Hosting;
using GameEvent.Web.Polls;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;

using ActionResult = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<GameEvent.Web.Seasons.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
    Microsoft.AspNetCore.Http.HttpResults.ValidationProblem,
    Microsoft.AspNetCore.Http.HttpResults.NotFound,
    Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>;

namespace GameEvent.Web.Duels;

public sealed record ChallengeDuelRequest(Guid CommandId, Guid OpponentId);

/// <summary>Public season races; private account ids and the internal acceptance snapshot are omitted.</summary>
public sealed record DuelView(
    Guid Id, Guid ChallengerId, Guid OpponentId, DateTimeOffset AcceptedAt, DateTimeOffset ExpiresAt,
    DuelStatus Status, Guid? WinnerId, Guid? RunId, int Coins, DuelEnd? End, DateTimeOffset? EndedAt);

public static class DuelEndpoints
{
    public static void MapDuels(this RouteGroupBuilder api)
    {
        var season = api.MapGroup("/seasons/{seasonId:guid}").WithTags("Duels");
        season.MapGet("/duels", ListAsync).RequireAuthorization()
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit).WithReadErrors();
        season.MapPost("/duels", ChallengeAsync).RequireAuthorization(Policies.Player)
            .RequireRateLimiting(AppSetup.InteractionRateLimit).WithActionErrors().ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static Task<ActionResult> ChallengeAsync(
        Guid seasonId, ChallengeDuelRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
        request.OpponentId == Guid.Empty
            ? Task.FromResult<ActionResult>(SeasonEndpoints.Invalid("opponentId", "An opponent id is required."))
            : SeasonEndpoints.ActAsync(seasonId, request.CommandId, user, db, bus, id => new ChallengeToDuel(id, request.OpponentId), ct);

    private static async Task<Results<Ok<IReadOnlyList<DuelView>>, NotFound, ProblemHttpResult>> ListAsync(
        Guid seasonId, GameEventDbContext db, SeasonStateReader reader, CancellationToken ct)
    {
        if (await SeasonRules.OfAsync(db, seasonId, ct) is not { } rules)
        {
            return TypedResults.NotFound();
        }

        if (!rules.Features.Duels)
        {
            return SeasonRules.FeatureOff("Duels are off in this season.");
        }

        // Duels have no separate table in Events (D-810): reuse the immutable cached fold, not a second projection.
        var state = await reader.ReadAsync(db, seasonId, ct);
        IReadOnlyList<DuelView> views = [.. state.Duels.Values.OrderByDescending(d => d.AcceptedAt).ThenBy(d => d.DuelId)
            .Select(View)];
        return TypedResults.Ok(views);
    }

    internal static DuelView View(Duel duel) => new(duel.DuelId, duel.ChallengerId, duel.OpponentId, duel.AcceptedAt,
        duel.ExpiresAt, duel.Status, duel.WinnerId, duel.RunId, duel.Coins, duel.End, duel.EndedAt);
}
