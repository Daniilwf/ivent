using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Statistics;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Hosting;
using GameEvent.Web.Polls;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

public sealed record NominationPlayerView(Guid PlayerId, string Name, Guid? ProfileUserId);

public sealed record AutomaticNominationView(AutoNomination Nomination, decimal Value, IReadOnlyList<NominationPlayerView> Winners);

public sealed record VotedNominationView(VotedNomination Nomination, Guid PollId, bool HasWinner, NominationCandidateView? Winner);

public sealed record NominationResultView(IReadOnlyList<AutomaticNominationView> Automatic, IReadOnlyList<VotedNominationView> Voted);

internal static class NominationResultEndpoints
{
    private static readonly string s_snapshotType = EventCatalog.Describe(typeof(SeasonNominationsRecorded)).Name;

    public static void MapNominationResults(this RouteGroupBuilder api)
    {
        api.MapGet("/seasons/{seasonId:guid}/nominations", ReadAsync)
            .WithTags("Seasons")
            .RequireAuthorization()
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<Results<Ok<NominationResultView>, NotFound>> ReadAsync(
        Guid seasonId, GameEventDbContext db, HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        var row = await (
            from season in db.Seasons.AsNoTracking()
            join entry in db.Events.AsNoTracking() on season.Id equals entry.SeasonId
            where season.Id == seasonId && (season.Status == SeasonStatus.Finished || season.Status == SeasonStatus.Archived)
                && entry.Type == s_snapshotType && entry.UndoneByEventId == null
            select new { season.RulesetJson, entry.Type, entry.Version, entry.Data }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        var features = RulesetJson.Parse(row.RulesetJson).Features;
        if (!features.SeasonNominations
            || EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)) is not SeasonNominationsRecorded result)
        {
            return TypedResults.NotFound();
        }

        var automatic = await ReadAutomaticAsync(db, seasonId, result.Automatic, ct);
        var voted = new List<VotedNominationView>(result.Voted.Count);
        foreach (var nomination in result.Voted)
        {
            NominationCandidateView? winner = null;
            if (nomination.WinnerCandidateId is { } winnerId)
            {
                // The frozen winner reference survives moderation, while the public content always follows current visibility.
                var visible = await NominationCandidates.ListAsync(db, seasonId, nomination.Nomination, features.Gallery, [winnerId], ct);
                if (visible.SingleOrDefault() is { } candidate
                    && (nomination.Nomination != VotedNomination.BestReview
                        || await MatchesReviewAsync(db, seasonId, nomination.PollId, candidate, ct)))
                {
                    winner = NominationEndpoints.ViewOf(seasonId, candidate);
                }
            }

            voted.Add(new VotedNominationView(nomination.Nomination, nomination.PollId, nomination.WinnerCandidateId is not null, winner));
        }

        return TypedResults.Ok(new NominationResultView(automatic, voted));
    }

    private static async Task<bool> MatchesReviewAsync(
        GameEventDbContext db, Guid seasonId, Guid pollId, NominationCandidate candidate, CancellationToken ct)
    {
        var pollJson = await db.Polls.AsNoTracking().Where(poll => poll.SeasonId == seasonId && poll.Id == pollId)
            .Select(poll => poll.DataJson).SingleOrDefaultAsync(ct);
        return pollJson is not null && JsonSerializer.Deserialize<Poll>(pollJson, EngineJson.Options) is { } poll
            && poll.MatchesNominationReview(candidate.Id, candidate.Detail);
    }

    private static async Task<IReadOnlyList<AutomaticNominationView>> ReadAutomaticAsync(
        GameEventDbContext db, Guid seasonId, EquatableArray<AutomaticNominationResult> nominations, CancellationToken ct)
    {
        var winnerIds = nominations.SelectMany(nomination => nomination.WinnerPlayerIds).Distinct().ToArray();
        var players = await db.SeasonPlayers.AsNoTracking()
            .Where(player => player.SeasonId == seasonId && winnerIds.Contains(player.Id))
            .Select(player => new NominationPlayerView(player.Id, player.Name,
                db.Users.Any(user => user.Id == player.UserId && !user.IsDeleted) ? player.UserId : (Guid?)null))
            .ToDictionaryAsync(player => player.PlayerId, ct);
        return [.. nominations.Select(nomination => new AutomaticNominationView(nomination.Nomination, nomination.Value,
            [.. nomination.WinnerPlayerIds.Where(players.ContainsKey).Select(id => players[id])]))];
    }
}
