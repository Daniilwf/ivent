using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
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
using SeasonRules = GameEvent.Web.Seasons.SeasonRules;

namespace GameEvent.Web.Polls;

/// <summary>A vote of the signed-in user (D-513, D-830): the option's id from the poll's snapshot. The voter is never in the body.</summary>
public sealed record VoteRequest(Guid CommandId, string OptionId);

/// <summary>The admin starts a poll (D-512): <c>poll</c> is the poll of CONTENT.md «голосование».</summary>
public sealed record StartPollRequest(Guid CommandId, JsonElement Poll);

/// <summary>
/// A poll as everyone sees it (D-830, D-831): the snapshot of its options, who votes, whether it is anonymous, its status
/// and term. <c>counts</c> — the votes per option, only while the poll shows live counts or once it is closed (D-541);
/// <c>result</c> — the option that won, after the close; <c>myVote</c> — the signed-in user's own vote, null to anyone
/// else. No answer names a voter. <c>voting</c> describes whether the current session may cast a new or different vote;
/// posting the option already held remains a refusal.
/// </summary>
public sealed record PollView(
    Guid Id,
    string Question,
    IReadOnlyList<PollOptionView> Options,
    PollVoters Voters,
    bool Anonymous,
    PollStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset ClosesAt,
    IReadOnlyList<PollCountView>? Counts,
    string? Result,
    string? MyVote,
    PollSource Source,
    PollVotingView? Voting = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VotedNomination? Nomination = null);

/// <summary>The signed-in user's current voting availability; a later POST still checks the authoritative rules.</summary>
public sealed record PollVotingView(bool CanVote, string? Reason, bool AllowChangeVote);

/// <summary>An option of the poll: the id a vote names, the words the screen shows.</summary>
public sealed record PollOptionView(string Id, string Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Href = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThumbnailUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Unavailable = null);

public sealed record PollCountView(string OptionId, int Votes);

/// <summary>
/// Polls over HTTP (task 5.19, D-830, D-831): the list and the card to anyone signed in, the vote of the signed-in user,
/// the admin's start and early close. Reads come from the projection's tables (Poll, PollOption, Vote); every change is a
/// command of the queue.
/// </summary>
public static class PollEndpoints
{
    /// <summary>The longest option a vote may name: a list's text, a category's name or a game's id are all shorter.</summary>
    public const int MaxOptionLength = Engine.Pool.GameCards.MaxTitleLength;

    public static void MapPolls(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var seasons = api.MapGroup("/seasons/{seasonId:guid}").WithTags("Polls").RequireAuthorization();
        seasons.MapGet("/polls", ListAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .WithReadErrors();
        seasons.MapGet("/polls/{pollId:guid}", CardAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .WithReadErrors();

        // Who may vote is the engine's rule (players of the season, spectators when the rules allow): no role policy here
        seasons.MapPost("/polls/{pollId:guid}/vote", VoteAsync)
            .RequireRateLimiting(AppSetup.VoteRateLimit)
            .WithActionErrors()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        var admin = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);
        admin.MapPost("/polls", StartAsync).WithActionErrors();
        admin.MapPost("/polls/{pollId:guid}/close", (Guid seasonId, Guid pollId, SeasonActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SeasonEndpoints.PrecheckAndSendAsync(seasonId, request.CommandId, new ClosePoll(pollId), user.UserId(), db, bus, ct))
            .WithActionErrors();
        admin.MapNominationPolls();
    }

    /// <summary>Error answers of a read, described so the generated client has their types.</summary>
    internal static RouteHandlerBuilder WithReadErrors(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<RejectionProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    private static async Task<Results<Ok<IReadOnlyList<PollView>>, NotFound, ProblemHttpResult>> ListAsync(
        Guid seasonId, ClaimsPrincipal user, HttpResponse response, GameEventDbContext db, IClock clock, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        if (await db.Seasons.AsNoTracking().SingleOrDefaultAsync(s => s.Id == seasonId, ct) is not { } season)
        {
            return TypedResults.NotFound();
        }

        var rules = RulesetJson.Parse(season.RulesetJson);
        if (!rules.Features.Polls)
        {
            return SeasonRules.FeatureOff("Polls are off in this season.");
        }

        var rows = await db.Polls.AsNoTracking().Where(p => p.SeasonId == seasonId).OrderByDescending(p => p.StartedAt).ToListAsync(ct);
        return TypedResults.Ok(await ViewsAsync(db, season, rules, rows, user.UserId(), clock.UtcNow, ct));
    }

    private static async Task<Results<Ok<PollView>, NotFound, ProblemHttpResult>> CardAsync(
        Guid seasonId, Guid pollId, ClaimsPrincipal user, HttpResponse response, GameEventDbContext db, IClock clock, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        if (await db.Seasons.AsNoTracking().SingleOrDefaultAsync(s => s.Id == seasonId, ct) is not { } season)
        {
            return TypedResults.NotFound();
        }

        var rules = RulesetJson.Parse(season.RulesetJson);
        if (!rules.Features.Polls)
        {
            return SeasonRules.FeatureOff("Polls are off in this season.");
        }

        var row = await db.Polls.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pollId && p.SeasonId == seasonId, ct);
        if (row is not null && await ViewsAsync(db, season, rules, [row], user.UserId(), clock.UtcNow, ct) is [var view])
        {
            return TypedResults.Ok(view);
        }

        return TypedResults.NotFound();
    }

    private static async Task<IReadOnlyList<PollView>> ViewsAsync(
        GameEventDbContext db, SeasonRecord season, Ruleset rules, List<PollRecord> rows, Guid? userId, DateTimeOffset now, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var polls = rows.ToDictionary(row => row.Id, row => JsonSerializer.Deserialize<Poll>(row.DataJson, EngineJson.Options)!);
        rows = rows.Where(row => polls[row.Id].Nomination is not { } category
            || rules.Features.SeasonNominations && rules.Nominations.Voted.Contains(category)).ToList();
        var ids = rows.Select(p => p.Id).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var options = (await db.PollOptions.AsNoTracking().Where(o => ids.Contains(o.PollId)).ToListAsync(ct)).ToLookup(o => o.PollId);
        var nomineeGroups = ids.Select(id => polls[id]).Where(poll => poll.Nomination != null)
            .GroupBy(poll => poll.Nomination.GetValueOrDefault());
        var candidates = new Dictionary<(VotedNomination Category, Guid Id), NominationCandidateView>();
        foreach (var group in nomineeGroups)
        {
            var candidateIds = group.SelectMany(poll => options[poll.PollId]).Select(option => Guid.Parse(option.OptionId)).Distinct().ToArray();
            foreach (var candidate in await NominationCandidates.ListAsync(db, season.Id, group.Key, rules.Features.Gallery, candidateIds, ct))
            {
                candidates.Add((group.Key, candidate.Id), NominationEndpoints.ViewOf(season.Id, candidate));
            }
        }

        var counts = (await db.Votes.AsNoTracking()
                .Where(v => ids.Contains(v.PollId))
                .GroupBy(v => new { v.PollId, v.OptionId })
                .Select(g => new { g.Key.PollId, g.Key.OptionId, Votes = g.Count() })
                .ToListAsync(ct))
            .ToLookup(c => c.PollId);
        var mine = await db.Votes.AsNoTracking()
            .Where(v => ids.Contains(v.PollId) && v.UserId == userId)
            .ToDictionaryAsync(v => v.PollId, v => v.OptionId, ct);
        // Participation is by user, not account role. The provisional first also stops affecting other players.
        var players = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == season.Id)
            .Select(p => new { p.Id, p.UserId, p.FinishOrder })
            .ToListAsync(ct);
        var player = players.SingleOrDefault(p => p.UserId == userId);
        var first = players.Where(p => p.FinishOrder is > 0).MinBy(p => p.FinishOrder);
        var isFirst = player is not null && player.Id == first?.Id;
        var allowChangeVote = rules.Polls?.AllowChangeVote == true;

        PollOptionView OptionOf(PollOptionRecord option, Poll poll)
        {
            if (poll.Nomination is not { } nomination)
            {
                return new PollOptionView(option.OptionId, option.Label);
            }

            var candidateId = Guid.Parse(option.OptionId);
            if (!candidates.TryGetValue((nomination, candidateId), out var candidate)
                || nomination == VotedNomination.BestReview && !poll.MatchesNominationReview(candidateId, candidate.Detail))
            {
                return new PollOptionView(option.OptionId, "", Unavailable: true);
            }

            return new PollOptionView(option.OptionId, candidate.Label, candidate.Detail, candidate.Href, candidate.ThumbnailUrl);
        }

        PollVotingView VotingOf(PollRecord row, Poll poll)
        {
            // Keep the order of Polling.CastVote's guards. A read neither spends a vote nor fires a due timer.
            // The season deadline does not stop nominations: only the poll's own term and status do.
            string? reason = null;
            if (season.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
            {
                reason = RejectionCodes.SeasonClosed;
            }
            else if (row.Status != PollStatus.Open || now >= row.ClosesAt)
            {
                reason = RejectionCodes.PollClosed;
            }
            else if (player is null && !(poll.Voters == PollVoters.Everyone && rules.Social.SpectatorsCanVote))
            {
                reason = RejectionCodes.PollNotVoter;
            }
            else if (isFirst && poll.HasEffect)
            {
                reason = RejectionCodes.PollFrozenVoter;
            }
            else if (mine.ContainsKey(row.Id) && !allowChangeVote)
            {
                reason = RejectionCodes.PollAlreadyVoted;
            }
            return new PollVotingView(reason is null, reason, allowChangeVote);
        }

        return [.. rows.Select(row =>
        {
            var poll = polls[row.Id];
            var snapshot = options[row.Id].OrderBy(o => o.Position).ToList();
            var byOption = counts[row.Id].ToDictionary(c => c.OptionId, c => c.Votes, StringComparer.Ordinal);

            // D-541: the counts by option are public only in the result, unless the poll shows them live
            var showCounts = row.Status == PollStatus.Closed || (row.Status == PollStatus.Open && poll.ShowLiveCounts && !row.Anonymous);
            return new PollView(
                row.Id,
                poll.Question,
                [.. snapshot.Select(o => OptionOf(o, poll))],
                poll.Voters,
                row.Anonymous,
                row.Status,
                row.StartedAt,
                row.ClosesAt,
                showCounts ? [.. snapshot.Select(o => new PollCountView(o.OptionId, byOption.GetValueOrDefault(o.OptionId)))] : null,
                row.Status == PollStatus.Closed ? poll.Result : null,
                mine.GetValueOrDefault(row.Id),
                poll.Source,
                VotingOf(row, poll),
                poll.Nomination);
        })];
    }

    private static async Task<ActionResult> VoteAsync(
        Guid seasonId, Guid pollId, VoteRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (request.OptionId is not { Length: > 0 and <= MaxOptionLength } option || option.Any(char.IsControl))
        {
            return SeasonEndpoints.Invalid("optionId", $"An option of the poll is 1–{MaxOptionLength} characters.");
        }

        if (await SeasonEndpoints.PrecheckAsync(seasonId, request.CommandId, db, ct) is { } refused)
        {
            return refused;
        }

        // The voter is the signed-in user, never the body (D-830). An anonymous poll's vote has no author in the log: the
        // user is kept by the command for «one vote» and the undo, and nothing that names a command's author (the admin's
        // journal, a player's last activity) can name the voter (D-541, D-835)
        if (user.UserId() is not { } userId)
        {
            return TypedResults.Forbid();
        }

        var anonymous = await db.Polls.AsNoTracking().Where(p => p.Id == pollId && p.SeasonId == seasonId).Select(p => p.Anonymous).SingleOrDefaultAsync(ct);
        return await SeasonEndpoints.SendAsync(seasonId, request.CommandId, new CastVote(pollId, userId, option), anonymous ? null : userId, bus, ct);
    }

    private static async Task<ActionResult> StartAsync(
        Guid seasonId, StartPollRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (request.Poll.ValueKind != JsonValueKind.Object)
        {
            return SeasonEndpoints.Invalid("poll", "A poll {question, options, voters, closesInHours} is required.");
        }

        PollDefinition poll;
        try
        {
            poll = ContentJson.Parse<PollDefinition>(request.Poll.GetRawText());
        }
        catch (JsonException e)
        {
            return SeasonEndpoints.Invalid("poll", e.Message);
        }

        return await SeasonEndpoints.PrecheckAndSendAsync(seasonId, request.CommandId, new StartPoll(poll), user.UserId(), db, bus, ct);
    }
}
