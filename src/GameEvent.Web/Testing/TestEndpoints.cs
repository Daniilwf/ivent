using System.Security.Claims;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
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

    private static Results<Ok<TestClockView>, ProblemHttpResult> MoveClock(TestClockRequest request, IClock clock)
    {
        if (clock is not IAdjustableClock adjustable)
        {
            return Problem("test.clockFixed", "This site's clock cannot be moved.");
        }

        if (request.Reset)
        {
            adjustable.Reset();
        }

        if (request.MoveTo is { } at)
        {
            adjustable.MoveTo(at);
        }

        if (request.AdvanceMinutes is { } minutes)
        {
            adjustable.Advance(TimeSpan.FromMinutes(minutes));
        }

        return TypedResults.Ok(new TestClockView(clock.UtcNow, true));
    }

    private static Results<NoContent, ProblemHttpResult> SeedRandom(TestRandomRequest request, IRandomSource random)
    {
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
                    var finish = map.Cells.Single(c => c.Type == CellType.Finish).Id;
                    var incoming = map.Edges.Where(e => e.To == finish).ToList();
                    var before = (incoming.FirstOrDefault(e => e.IsPrimaryBackward) ?? incoming.First()).From;
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
                return TypedResults.NotFound();
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
                var option = choice.Options.First(o => o.Game is not null);
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
            var run = playing.Runs[playing.Players[playerId].ActiveRunId!.Value];
            return await SendAsync(run.Snapshot.Hours is null
                ? new CompleteRun(playerId, Difficulty.Extreme, 1m, "Сценарий")
                : new CompleteRun(playerId, Difficulty.Extreme));
        }
    }

    private static ProblemHttpResult Problem(string code, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The test action was refused.", detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
