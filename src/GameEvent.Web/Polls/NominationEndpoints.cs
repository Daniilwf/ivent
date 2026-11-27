using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
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
using SeasonRules = GameEvent.Web.Seasons.SeasonRules;

namespace GameEvent.Web.Polls;

public sealed record StartNominationPollRequest(Guid CommandId, Guid PollId, VotedNomination? Category, Guid[]? CandidateIds);

public sealed record NominationCandidateView(Guid Id, string Label, string? Detail, string? Href, string? ThumbnailUrl);

public sealed record NominationCandidatesView(VotedNomination Category, int DurationHours, int MinCandidates, int MaxCandidates,
    IReadOnlyList<NominationCandidateView> Candidates);

internal static class NominationEndpoints
{
    public static void MapNominationPolls(this RouteGroupBuilder admin)
    {
        admin.MapPost("/nominations/polls", StartAsync).WithActionErrors();
        admin.MapGet("/nominations/candidates", CandidatesAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .WithReadErrors()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<ActionResult> StartAsync(
        Guid seasonId, StartNominationPollRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (request.PollId == Guid.Empty)
        {
            return SeasonEndpoints.Invalid("pollId", "A poll id is required.");
        }

        if (request.Category is not { } category || !Enum.IsDefined(category))
        {
            return SeasonEndpoints.Invalid("category", "A nomination category is required.");
        }

        if (request.CandidateIds is not { } candidateIds || candidateIds.Any(id => id == Guid.Empty))
        {
            return SeasonEndpoints.Invalid("candidateIds", "Candidate ids are required.");
        }

        return await SeasonEndpoints.PrecheckAndSendAsync(seasonId, request.CommandId,
            new StartNominationPoll(request.PollId, category, [.. candidateIds]), user.UserId(), db, bus, ct);
    }

    private static async Task<Results<Ok<NominationCandidatesView>, NotFound, ValidationProblem, ProblemHttpResult>> CandidatesAsync(
        Guid seasonId, string? category, HttpResponse response, GameEventDbContext db, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        if (!Enum.TryParse<VotedNomination>(category, ignoreCase: true, out var nomination)
            || Enum.GetName(nomination) is not { } name
            || category != JsonNamingPolicy.CamelCase.ConvertName(name))
        {
            return SeasonEndpoints.Invalid("category", "A nomination category is required.");
        }

        if (await SeasonRules.OfAsync(db, seasonId, ct) is not { } rules)
        {
            return TypedResults.NotFound();
        }

        if (!rules.Features.SeasonNominations || !rules.Features.Polls || !rules.Nominations.Voted.Contains(nomination)
            || rules.Nominations is not { DurationHours: { } duration, MinCandidates: { } minimum, MaxCandidates: { } maximum })
        {
            return SeasonRules.FeatureOff("This nomination is off in this season.");
        }

        var candidates = await NominationCandidates.ListAsync(db, seasonId, nomination, rules.Features.Gallery, null, ct);
        return TypedResults.Ok(new NominationCandidatesView(nomination, duration, minimum, maximum,
            [.. candidates.Select(candidate => ViewOf(seasonId, candidate))]));
    }

    internal static NominationCandidateView ViewOf(Guid seasonId, NominationCandidate candidate) => new(
        candidate.Id, candidate.Label, candidate.Detail,
        candidate.GameId is { } gameId ? $"/games/{gameId:D}" : $"/seasons/{seasonId:D}/gallery",
        candidate.HasThumbnail ? $"/api/seasons/{seasonId:D}/gallery/{candidate.Id:D}/thumbnail" : null);
}
