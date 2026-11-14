using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Challenges;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
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

namespace GameEvent.Web.Challenges;

/// <summary>
/// The admin sets a weekly challenge (D-526, D-833): <c>challenge</c> is the challenge of CONTENT.md «челлендж недели», its
/// window starts at <c>startsAt</c> and lasts <c>weeklyChallenge.durationDays</c>.
/// </summary>
public sealed record SetWeeklyChallengeRequest(Guid CommandId, JsonElement Challenge, DateTimeOffset StartsAt);

/// <summary>The admin marks <c>playerId</c> as having completed a manual challenge with the run <c>runId</c> (D-526).</summary>
public sealed record MarkWeeklyChallengeRequest(Guid CommandId, Guid PlayerId, Guid RunId);

/// <summary>
/// A weekly challenge (D-833): its words, how it is checked, what it pays, its window and who completed it (the completions
/// in force: a rejected run's is gone).
/// </summary>
public sealed record WeeklyChallengeView(
    Guid Id,
    string Name,
    string Description,
    ChallengeCheck Check,
    int RewardCoins,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    IReadOnlyList<ChallengeCompletionView> CompletedBy);

/// <summary>A player who completed the challenge, in the order of completion.</summary>
public sealed record ChallengeCompletionView(Guid PlayerId, string Name);

/// <summary>
/// The weekly challenge over HTTP (task 5.19, D-833): the list to anyone signed in, the admin's set and mark. Reads come
/// from the projection's tables (WeeklyChallenge, WeeklyChallengeCompletion); every change is a command of the queue.
/// </summary>
public static class WeeklyChallengeEndpoints
{
    public static void MapWeeklyChallenges(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGroup("/seasons/{seasonId:guid}").WithTags("WeeklyChallenges").RequireAuthorization()
            .MapGet("/weekly-challenges", ListAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .WithReadErrors();

        var admin = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);
        admin.MapPost("/weekly-challenges", SetAsync).WithActionErrors();
        admin.MapPost("/weekly-challenges/{challengeId:guid}/mark", (Guid seasonId, Guid challengeId, MarkWeeklyChallengeRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                request.PlayerId == Guid.Empty
                    ? Task.FromResult<ActionResult>(Invalid("playerId", "A player is required."))
                    : request.RunId == Guid.Empty
                        ? Task.FromResult<ActionResult>(Invalid("runId", "A run is required."))
                        : SendAsync(seasonId, request.CommandId, new MarkWeeklyChallenge(challengeId, request.PlayerId, request.RunId), user, db, bus, ct))
            .WithActionErrors();
    }

    private static async Task<Results<Ok<IReadOnlyList<WeeklyChallengeView>>, NotFound, ProblemHttpResult>> ListAsync(
        Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        if (await SeasonRules.OfAsync(db, seasonId, ct) is not { } rules)
        {
            return TypedResults.NotFound();
        }

        if (!rules.Features.WeeklyChallenge)
        {
            return SeasonRules.FeatureOff("The weekly challenge is off in this season.");
        }

        var rows = await db.WeeklyChallenges.AsNoTracking().Where(x => x.SeasonId == seasonId).OrderByDescending(x => x.StartsAt).ToListAsync(ct);
        var ids = rows.Select(x => x.Id).ToList();
        var completions = (await db.WeeklyChallengeCompletions.AsNoTracking().Where(c => ids.Contains(c.ChallengeId)).ToListAsync(ct))
            .ToLookup(c => c.ChallengeId);
        var names = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == seasonId).ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        IReadOnlyList<WeeklyChallengeView> challenges = [.. rows.Select(row =>
        {
            var definition = JsonSerializer.Deserialize<WeeklyChallengeDefinition>(row.DefinitionJson, EngineJson.Options)!;
            return new WeeklyChallengeView(
                row.Id,
                definition.Name,
                definition.Description,
                definition.Check,
                row.RewardCoins,
                row.StartsAt,
                row.EndsAt,
                [.. completions[row.Id].OrderBy(c => c.Position).Select(c => new ChallengeCompletionView(c.PlayerId, names.GetValueOrDefault(c.PlayerId, "")))]);
        })];
        return TypedResults.Ok(challenges);
    }

    private static async Task<ActionResult> SetAsync(
        Guid seasonId, SetWeeklyChallengeRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (request.Challenge.ValueKind != JsonValueKind.Object)
        {
            return Invalid("challenge", "A challenge {id, name, description, condition, reward, check} is required.");
        }

        WeeklyChallengeDefinition challenge;
        try
        {
            challenge = ContentJson.Parse<WeeklyChallengeDefinition>(request.Challenge.GetRawText());
        }
        catch (JsonException e)
        {
            return Invalid("challenge", e.Message);
        }

        return await SendAsync(seasonId, request.CommandId, new SetWeeklyChallenge(challenge, request.StartsAt), user, db, bus, ct);
    }

    private static async Task<ActionResult> SendAsync(Guid seasonId, Guid commandId, ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
        await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
