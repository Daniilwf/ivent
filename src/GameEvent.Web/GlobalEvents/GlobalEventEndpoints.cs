using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Content;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Database;
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

namespace GameEvent.Web.GlobalEvents;

/// <summary>
/// The admin launches the event <c>objectId</c> of the season's content on everyone for <c>durationHours</c> (D-517, D-832),
/// with a goal or without.
/// </summary>
public sealed record LaunchGlobalEventRequest(Guid CommandId, string ObjectId, int DurationHours, GlobalGoalRequest? Goal = null);

/// <summary>
/// The goal of a global event: <c>target</c> of <c>metric</c> together; <c>reward</c> — the effect (CONTENT.md «эффект»)
/// everyone gets when it is reached.
/// </summary>
public sealed record GlobalGoalRequest(GoalMetric Metric, int Target, JsonElement? Reward = null);

/// <summary>
/// A global event (D-832): the name and the description of its snapshot, its status and term, its goal. Also the banner of
/// the season view (<c>globalEvents</c>), where only the running ones are.
/// </summary>
public sealed record GlobalEventView(
    Guid Id, string ObjectId, string Name, string Description, GlobalEventStatus Status, DateTimeOffset StartsAt, DateTimeOffset EndsAt, GlobalGoalView? Goal);

/// <summary>
/// A goal's progress (D-518, D-800): <c>progress</c> — the sum of the contributions in force (a rejected run's is gone);
/// <c>reached</c> — the goal was reached once, which stays.
/// </summary>
public sealed record GlobalGoalView(GoalMetric Metric, int Target, decimal Progress, bool Reached);

/// <summary>
/// Global events over HTTP (task 5.19, D-832): the list to anyone signed in, the banner in the season view, the admin's
/// launch and early end. Reads come from the projection's table (GlobalEvent); every change is a command of the queue.
/// </summary>
public static class GlobalEventEndpoints
{
    /// <summary>The longest term of a global event, as of a poll: a season is shorter.</summary>
    public const int MaxDurationHours = ContentValidator.MaxPollHours;

    public static void MapGlobalEvents(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGroup("/seasons/{seasonId:guid}").WithTags("GlobalEvents").RequireAuthorization()
            .MapGet("/global-events", ListAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .WithReadErrors();

        var admin = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);
        admin.MapPost("/global-events", LaunchAsync).WithActionErrors();
        admin.MapPost("/global-events/{globalEventId:guid}/end", (Guid seasonId, Guid globalEventId, SeasonActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SendAsync(seasonId, request.CommandId, new EndGlobalEvent(globalEventId), user, db, bus, ct))
            .WithActionErrors();
    }

    /// <summary>The banner of the season view: the events running now, oldest first; empty while the mechanic is off.</summary>
    internal static async Task<IReadOnlyList<GlobalEventView>> BannerAsync(GameEventDbContext db, Guid seasonId, bool on, DateTimeOffset now, CancellationToken ct) =>
        on
            ? [.. (await db.GlobalEvents.AsNoTracking()
                    .Where(x => x.SeasonId == seasonId && x.Status == GlobalEventStatus.Active)
                    .ToListAsync(ct))
                .Where(x => now < x.EndsAt)
                .OrderBy(x => x.StartedAt)
                .Select(View)]
            : [];

    private static async Task<Results<Ok<IReadOnlyList<GlobalEventView>>, NotFound, ProblemHttpResult>> ListAsync(
        Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        if (await SeasonRules.OfAsync(db, seasonId, ct) is not { } rules)
        {
            return TypedResults.NotFound();
        }

        if (!rules.Features.GlobalEvents)
        {
            return SeasonRules.FeatureOff("Global events are off in this season.");
        }

        IReadOnlyList<GlobalEventView> events = [.. (await db.GlobalEvents.AsNoTracking().Where(x => x.SeasonId == seasonId).ToListAsync(ct))
            .OrderByDescending(x => x.StartedAt)
            .Select(View)];
        return TypedResults.Ok(events);
    }

    // The columns win over the JSON, as the projection reads them back (D-811)
    private static GlobalEventView View(GlobalEventRecord row)
    {
        var state = JsonSerializer.Deserialize<GlobalEventState>(row.DataJson, EngineJson.Options)!;
        return new GlobalEventView(
            row.Id,
            row.ObjectId,
            state.Definition.Name,
            state.Definition.Description,
            row.Status,
            row.StartedAt,
            row.EndsAt,
            state.Goal is { } goal ? new GlobalGoalView(goal.Metric, goal.Target, state.Progress, row.Reached) : null);
    }

    private static async Task<ActionResult> LaunchAsync(
        Guid seasonId, LaunchGlobalEventRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (request.ObjectId is not { Length: > 0 and <= ContentValidator.MaxIdLength } objectId || objectId.Any(char.IsControl))
        {
            return Invalid("objectId", $"An event's id is 1–{ContentValidator.MaxIdLength} characters.");
        }

        if (request.DurationHours is < 1 or > MaxDurationHours)
        {
            return Invalid("durationHours", $"A term is 1–{MaxDurationHours} hours.");
        }

        GlobalEventGoal? goal = null;
        if (request.Goal is { } asked)
        {
            if (!Enum.IsDefined(asked.Metric))
            {
                return Invalid("goal.metric", "«hours» or «runs».");
            }

            if (asked.Target < 1)
            {
                return Invalid("goal.target", "A goal's target is at least 1.");
            }

            EffectSpec? reward = null;
            if (asked.Reward is { ValueKind: not JsonValueKind.Null } json)
            {
                if (json.ValueKind != JsonValueKind.Object)
                {
                    return Invalid("goal.reward", "A reward is an effect {target, actions}.");
                }

                try
                {
                    reward = ContentJson.Parse<EffectSpec>(json.GetRawText());
                }
                catch (JsonException e)
                {
                    return Invalid("goal.reward", e.Message);
                }
            }

            goal = new GlobalEventGoal { Metric = asked.Metric, Target = asked.Target, Reward = reward };
        }

        return await SendAsync(seasonId, request.CommandId, new LaunchGlobalEvent(objectId, request.DurationHours, goal), user, db, bus, ct);
    }

    private static async Task<ActionResult> SendAsync(Guid seasonId, Guid commandId, ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
        await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
