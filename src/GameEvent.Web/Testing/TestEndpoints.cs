using System.Security.Claims;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Kernel;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Testing;

/// <summary>The site's clock as the test endpoints see it: now, and whether it can be moved.</summary>
public sealed record TestClockView(DateTimeOffset Now, bool Adjustable);

/// <summary>Move the clock: forward by minutes, to a moment, or back to the real time.</summary>
public sealed record TestClockRequest(double? AdvanceMinutes = null, DateTimeOffset? MoveTo = null, bool Reset = false);

/// <summary>Seed the randomness (the same rolls and dice every time), or null for unpredictable randomness again.</summary>
public sealed record TestRandomRequest(int? Seed);

/// <summary>A scenario for a player of the season, by login (the first player when none).</summary>
public sealed record TestScenarioRequest(string? Player = null);

/// <summary>What a scenario did: the commands it sent, in order.</summary>
public sealed record TestScenarioView(string Scenario, IReadOnlyList<string> Commands);

/// <summary>
/// Test endpoints (SPEC «Тестовые эндпоинты», E4, A7, D-120): rewind the time, seed the randomness, load a scenario —
/// «финиш на носу», «дедлайн через час», «на игроке пять ручных эффектов». Mapped only in Development and Test, admin
/// only; in Production the routes do not exist. Scenarios go through the queue like any admin action.
/// </summary>
public static class TestEndpoints
{
    public const string FinishSoon = "finish-soon";
    public const string DeadlineInHour = "deadline-in-hour";
    public const string FiveManualEffects = "five-manual-effects";

    /// <summary>How far one request moves the clock: ten years either way.</summary>
    public const double MaxAdvanceMinutes = 10 * 365 * 24 * 60;

    public const int MinYear = 2000;
    public const int MaxYear = 2100;

    private static readonly string[] s_scenarios = [FinishSoon, DeadlineInHour, FiveManualEffects];

    public static bool Available(IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsDevelopment() || environment.IsEnvironment("Test");
    }

    /// <summary>A movable clock and a seedable randomness for the test endpoints; production keeps the real ones.</summary>
    public static void AddTestSupport(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (Available(builder.Environment))
        {
            builder.Services.AddSingleton<IClock, ShiftableClock>();
            builder.Services.AddSingleton<IRandomSource, ReseedableRandom>();
        }
    }

    public static void MapTestEndpoints(this WebApplication app, RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(api);
        if (!Available(app.Environment))
        {
            return;
        }

        var test = api.MapGroup("/test").WithTags("Test").RequireAuthorization(Policies.Admin).ExcludeFromDescription();
        test.MapGet("/clock", (IClock clock) => TypedResults.Ok(new TestClockView(clock.UtcNow, clock is IAdjustableClock)));
        test.MapPost("/clock", MoveClock);
        test.MapPost("/random", SeedRandom);
        test.MapPost("/seasons/{seasonId:guid}/scenarios/{name}", RunScenarioAsync);
    }

    private static Results<Ok<TestClockView>, ProblemHttpResult, ValidationProblem> MoveClock(TestClockRequest? request, IClock clock)
    {
        // A guard, not a path: where the routes exist the site's clock is adjustable (AddTestSupport, the tests' clock)
        if (clock is not IAdjustableClock adjustable)
        {
            return Problem("test.clockFixed", "This site's clock cannot be moved.");
        }

        if (request is null || (!request.Reset && request.MoveTo is null && request.AdvanceMinutes is null))
        {
            return Invalid("request", "Give advanceMinutes, moveTo or reset.");
        }

        if (request.AdvanceMinutes is { } minutes && !(double.IsFinite(minutes) && Math.Abs(minutes) <= MaxAdvanceMinutes))
        {
            return Invalid("advanceMinutes", $"At most {MaxAdvanceMinutes} minutes either way.");
        }

        if (request.MoveTo is { } moment && moment.UtcDateTime.Year is < MinYear or > MaxYear)
        {
            return Invalid("moveTo", $"A moment between {MinYear} and {MaxYear}.");
        }

        try
        {
            if (request.Reset)
            {
                adjustable.Reset();
            }

            if (request.MoveTo is { } at)
            {
                adjustable.MoveTo(at);
            }

            if (request.AdvanceMinutes is { } by)
            {
                adjustable.Advance(TimeSpan.FromMinutes(by));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return Invalid("moveTo", "The clock goes at most 100 years from the real time.");
        }

        return TypedResults.Ok(new TestClockView(clock.UtcNow, true));
    }

    private static Results<NoContent, ProblemHttpResult> SeedRandom(TestRandomRequest request, IRandomSource random)
    {
        // A guard, like the clock's
        if (random is not IReseedableRandom reseedable)
        {
            return Problem("test.randomFixed", "This site's randomness cannot be seeded.");
        }

        reseedable.Seed(request.Seed);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<TestScenarioView>, NotFound, ProblemHttpResult>> RunScenarioAsync(
        Guid seasonId, string name, TestScenarioRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, IClock clock, CancellationToken ct)
    {
        if (!s_scenarios.Contains(name))
        {
            return TypedResults.NotFound();
        }

        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
        if (!state.IsCreated)
        {
            return TypedResults.NotFound();
        }

        var players = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == seasonId).ToListAsync(ct);
        var logins = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Login, ct);
        var player = request.Player is { Length: > 0 } login
            ? players.FirstOrDefault(p => string.Equals(logins.GetValueOrDefault(p.UserId), login, StringComparison.OrdinalIgnoreCase))
            : players.OrderBy(p => p.Name).FirstOrDefault();
        if (player is null && name != DeadlineInHour)
        {
            return Problem("test.playerUnknown", "No such player in the season.");
        }

        var sent = new List<string>();
        async Task<string?> SendAsync(ICommand command)
        {
            var outcome = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), seasonId, command, user.UserId()), ct);
            sent.Add(command.GetType().Name);
            return outcome.IsAccepted ? null : outcome.Rejection!.Code;
        }

        switch (name)
        {
            case FinishSoon:
                {
                    // One cell before the finish, on its primary incoming edge: the next completion reaches it
                    var map = state.Map;
                    var finish = map.Cells.FirstOrDefault(c => c.Type == CellType.Finish)?.Id;
                    var incoming = map.Edges.Where(e => e.To == finish).ToList();
                    if ((incoming.FirstOrDefault(e => e.IsPrimaryBackward) ?? incoming.FirstOrDefault())?.From is not { } before)
                    {
                        return Problem("test.noFinish", "The season's map has no way into a finish.");
                    }

                    if (await SendAsync(new AdjustPlayer(player!.Id, "Сценарий «финиш на носу»", CellId: before)) is { } refused)
                    {
                        return Problem(refused, "The player could not be moved.");
                    }

                    break;
                }

            case DeadlineInHour:
                if (await SendAsync(new SetSeasonDeadline(clock.UtcNow.AddHours(1))) is { } deadlineRefused)
                {
                    return Problem(deadlineRefused, "The deadline could not be set.");
                }

                break;

            case FiveManualEffects:
                {
                    // Five extreme completions in a row: each grants a good event to play by hand
                    for (var i = 0; i < 5; i++)
                    {
                        if (await CompleteExtremeAsync(player!.Id) is { } refused)
                        {
                            return Problem(refused, $"Completion {i + 1} of 5 was refused.");
                        }
                    }

                    break;
                }

            default:
                throw new InvalidOperationException($"Scenario {name} has no steps.");
        }

        return TypedResults.Ok(new TestScenarioView(name, sent));

        async Task<string?> CompleteExtremeAsync(Guid playerId)
        {
            if (await SendAsync(new RollGame(playerId)) is { } rollRefused)
            {
                return rollRefused;
            }

            var (now, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
            if (now.Players[playerId].Choice is { } choice)
            {
                if (choice.Options.FirstOrDefault(o => o.Game is not null) is not { } option)
                {
                    return "test.noGameToChoose";
                }

                if (await SendAsync(new MakeChoice(playerId, choice.ChoiceId, option.Id)) is { } choiceRefused)
                {
                    return choiceRefused;
                }
            }

            if (await SendAsync(new StartRun(playerId)) is { } startRefused)
            {
                return startRefused;
            }

            var (playing, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
            if (playing.Players[playerId].ActiveRunId is not { } runId)
            {
                return "test.noActiveRun";
            }

            var run = playing.Runs[runId];
            if (await SendAsync(run.Snapshot.Hours is null
                    ? new CompleteRun(playerId, Difficulty.Extreme, 1m, "Сценарий")
                    : new CompleteRun(playerId, Difficulty.Extreme)) is { } completeRefused)
            {
                return completeRefused;
            }

            // The admin checks each run at once, so the next roll is not held by the unchecked limit (D-134); the
            // good event of the extreme difficulty stays to be played
            return await SendAsync(new ApproveProof(runId, Comment: "Сценарий: проверено сразу"));
        }
    }

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static ProblemHttpResult Problem(string code, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The test action was refused.", detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
