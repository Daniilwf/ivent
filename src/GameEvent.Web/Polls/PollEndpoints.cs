using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
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

namespace GameEvent.Web.Polls;

/// <summary>A vote of the signed-in user (D-513, D-830): the option's id from the poll's snapshot. The voter is never in the body.</summary>
public sealed record VoteRequest(Guid CommandId, string OptionId);

/// <summary>The admin starts a poll (D-512): <c>poll</c> is the poll of CONTENT.md «голосование».</summary>
public sealed record StartPollRequest(Guid CommandId, JsonElement Poll);

/// <summary>
/// A poll as everyone sees it (D-830, D-831): the snapshot of its options, who votes, whether it is anonymous, its status
/// and term. <c>counts</c> — the votes per option, only while the poll shows live counts or once it is closed (D-541);
/// <c>result</c> — the option that won, after the close; <c>myVote</c> — the signed-in user's own vote, null to anyone
/// else. No answer names a voter.
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
    PollSource Source);

/// <summary>An option of the poll: the id a vote names, the words the screen shows.</summary>
public sealed record PollOptionView(string Id, string Label);

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
        seasons.MapPost("/polls/{pollId:guid}/vote", VoteAsync).WithActionErrors();

        var admin = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);
        admin.MapPost("/polls", StartAsync).WithActionErrors();
        admin.MapPost("/polls/{pollId:guid}/close", (Guid seasonId, Guid pollId, SeasonActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SendAsync(seasonId, request.CommandId, new ClosePoll(pollId), user.UserId(), db, bus, ct))
            .WithActionErrors();
    }

    /// <summary>Error answers of a read, described so the generated client has their types.</summary>
    internal static RouteHandlerBuilder WithReadErrors(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<RejectionProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    private static async Task<Results<Ok<IReadOnlyList<PollView>>, NotFound, ProblemHttpResult>> ListAsync(
        Guid seasonId, ClaimsPrincipal user, GameEventDbContext db, CancellationToken ct)
    {
        if (await SeasonRules.OfAsync(db, seasonId, ct) is not { } rules)
        {
            return TypedResults.NotFound();
        }

        if (!rules.Features.Polls)
        {
            return SeasonRules.FeatureOff("Polls are off in this season.");
        }

        var rows = await db.Polls.AsNoTracking().Where(p => p.SeasonId == seasonId).OrderByDescending(p => p.StartedAt).ToListAsync(ct);
        return TypedResults.Ok(await ViewsAsync(db, rows, user.UserId(), ct));
    }

    private static async Task<Results<Ok<PollView>, NotFound, ProblemHttpResult>> CardAsync(
        Guid seasonId, Guid pollId, ClaimsPrincipal user, GameEventDbContext db, CancellationToken ct)
    {
        if (await SeasonRules.OfAsync(db, seasonId, ct) is not { } rules)
        {
            return TypedResults.NotFound();
        }

        if (!rules.Features.Polls)
        {
            return SeasonRules.FeatureOff("Polls are off in this season.");
        }

        var row = await db.Polls.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pollId && p.SeasonId == seasonId, ct);
        return row is null ? TypedResults.NotFound() : TypedResults.Ok((await ViewsAsync(db, [row], user.UserId(), ct))[0]);
    }

    private static async Task<IReadOnlyList<PollView>> ViewsAsync(GameEventDbContext db, IReadOnlyList<PollRecord> rows, Guid? userId, CancellationToken ct)
    {
        var ids = rows.Select(p => p.Id).ToList();
        var options = (await db.PollOptions.AsNoTracking().Where(o => ids.Contains(o.PollId)).ToListAsync(ct)).ToLookup(o => o.PollId);
        var counts = (await db.Votes.AsNoTracking()
                .Where(v => ids.Contains(v.PollId))
                .GroupBy(v => new { v.PollId, v.OptionId })
                .Select(g => new { g.Key.PollId, g.Key.OptionId, Votes = g.Count() })
                .ToListAsync(ct))
            .ToLookup(c => c.PollId);
        var mine = await db.Votes.AsNoTracking()
            .Where(v => ids.Contains(v.PollId) && v.UserId == userId)
            .ToDictionaryAsync(v => v.PollId, v => v.OptionId, ct);

        return [.. rows.Select(row =>
        {
            var poll = JsonSerializer.Deserialize<Poll>(row.DataJson, EngineJson.Options)!;
            var snapshot = options[row.Id].OrderBy(o => o.Position).ToList();
            var byOption = counts[row.Id].ToDictionary(c => c.OptionId, c => c.Votes, StringComparer.Ordinal);

            // D-541: the counts by option are public only in the result, unless the poll shows them live
            var showCounts = row.Status == PollStatus.Closed || (row.Status == PollStatus.Open && poll.ShowLiveCounts && !row.Anonymous);
            return new PollView(
                row.Id,
                poll.Question,
                [.. snapshot.Select(o => new PollOptionView(o.OptionId, o.Label))],
                poll.Voters,
                row.Anonymous,
                row.Status,
                row.StartedAt,
                row.ClosesAt,
                showCounts ? [.. snapshot.Select(o => new PollCountView(o.OptionId, byOption.GetValueOrDefault(o.OptionId)))] : null,
                row.Status == PollStatus.Closed ? poll.Result : null,
                mine.GetValueOrDefault(row.Id),
                poll.Source);
        })];
    }

    private static async Task<ActionResult> VoteAsync(
        Guid seasonId, Guid pollId, VoteRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (request.OptionId is not { Length: > 0 and <= MaxOptionLength } option || option.Any(char.IsControl))
        {
            return Invalid("optionId", $"An option of the poll is 1–{MaxOptionLength} characters.");
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
            return Invalid("poll", "A poll {question, options, voters, closesInHours} is required.");
        }

        PollDefinition poll;
        try
        {
            poll = ContentJson.Parse<PollDefinition>(request.Poll.GetRawText());
        }
        catch (JsonException e)
        {
            return Invalid("poll", e.Message);
        }

        return await SendAsync(seasonId, request.CommandId, new StartPoll(poll), user.UserId(), db, bus, ct);
    }

    private static async Task<ActionResult> SendAsync(Guid seasonId, Guid commandId, ICommand command, Guid? authorId, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
        await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, authorId, bus, ct);

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
