using System.Security.Claims;
using GameEvent.Engine.Content;
using GameEvent.Engine.Finish;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Kernel;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Testing;

/// <summary>
/// The site's clock as the test endpoints see it: now, whether it can be moved and how far it is from the real time
/// (<c>shiftMinutes</c>, negative — in the past; H9, D-221).
/// </summary>
public sealed record TestClockView(DateTimeOffset Now, bool Adjustable, double ShiftMinutes);

/// <summary>The site's randomness: the seed in force, or null while it is unpredictable; whether it can be seeded.</summary>
public sealed record TestRandomView(int? Seed, bool Seedable);

/// <summary>Everything the page of the test tools shows (H9, D-221): the clock, the randomness and the scenarios by name.</summary>
public sealed record TestToolsView(TestClockView Clock, TestRandomView Random, IReadOnlyList<string> Scenarios);

/// <summary>Move the clock: forward by minutes, to a moment, or back to the real time.</summary>
public sealed record TestClockRequest(double? AdvanceMinutes = null, DateTimeOffset? MoveTo = null, bool Reset = false);

/// <summary>Seed the randomness (the same rolls and dice every time), or null for unpredictable randomness again.</summary>
public sealed record TestRandomRequest(int? Seed);

/// <summary>
/// A scenario for a player of the season: by the player's id in the season (<c>playerId</c>, the page picks from the
/// season's players) or by login (<c>player</c>); the first player by name when neither.
/// </summary>
public sealed record TestScenarioRequest(string? Player = null, Guid? PlayerId = null);

/// <summary>
/// A content pack for a season of the tests (stage 5, D-534): <c>objects</c> and <c>wheels</c> — the JSON texts of content
/// files, as <c>content/</c> holds them; published by the admin's <c>PublishContent</c> with <c>comment</c>.
/// </summary>
public sealed record TestContentRequest(IReadOnlyList<string>? Objects, IReadOnlyList<string>? Wheels, string? Comment);

/// <summary>What a scenario did: the commands it sent, in order.</summary>
public sealed record TestScenarioView(string Scenario, IReadOnlyList<string> Commands);

/// <summary>
/// Test endpoints (SPEC «Тестовые эндпоинты», E4, A7, D-120): rewind the time, seed the randomness, load a scenario —
/// a finish, a deadline, manual effects or a stage 5 interaction. Mapped in Development, Test and on the test copy (Staging, D-220), admin
/// only; in Production the routes do not exist. Scenarios go through the queue like any admin action.
/// </summary>
public static class TestEndpoints
{
    public const string FinishSoon = "finish-soon";
    public const string DeadlineInHour = "deadline-in-hour";
    public const string FiveManualEffects = "five-manual-effects";
    public const string IrlProofPending = "irl-proof-pending";
    public const string CoopInvitation = "coop-invitation";
    public const string PollAndGlobalGoal = "poll-and-global-goal";

    /// <summary>How far one request moves the clock: ten years either way.</summary>
    public const double MaxAdvanceMinutes = 10 * 365 * 24 * 60;

    public const int MinYear = 2000;
    public const int MaxYear = 2100;

    private static readonly string[] s_scenarios = [FinishSoon, DeadlineInHour, FiveManualEffects, IrlProofPending, CoopInvitation, PollAndGlobalGoal];

    // The scenario uses the published repository event and two fixed choices so it works with any pool.
    private const string ScenarioGoalEvent = "tailwind";
    private const int ScenarioHours = 24;
    private static readonly LaunchGlobalEvent s_scenarioGoal = new(ScenarioGoalEvent, ScenarioHours, new GlobalEventGoal
    {
        Metric = GoalMetric.Runs,
        Target = 3,
    });
    private static readonly StartPoll s_scenarioPoll = new(new PollDefinition
    {
        Question = "Что сыграть после теста?",
        Options = new ChoiceOptionsSpec { List = ["Короткую игру", "Длинную игру"] },
        Voters = PollVoters.Players,
        ClosesInHours = ScenarioHours,
    });

    public static bool Available(IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        // The test copy on the server (Staging) has them too: loading scenarios there is what it is for (D-220, the
        // owner's decision); the live site (Production) never does
        return environment.IsDevelopment() || environment.IsEnvironment("Test") || environment.IsStaging();
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

        // In the API description where they exist (the client of the test tools' page is generated from it, D-221); the
        // live site has neither the routes nor the description
        var test = api.MapGroup("/test").WithTags("Test").RequireAuthorization(Policies.Admin);
        test.MapGet("", (IClock clock, IRandomSource random) =>
            TypedResults.Ok(new TestToolsView(ClockOf(clock), RandomOf(random), s_scenarios)));
        test.MapGet("/clock", (IClock clock) => TypedResults.Ok(ClockOf(clock)));
        test.MapPost("/clock", MoveClock).ProducesProblem(StatusCodes.Status409Conflict);
        test.MapPost("/random", SeedRandom).ProducesProblem(StatusCodes.Status409Conflict);
        test.MapPost("/seasons/{seasonId:guid}/scenarios/{name}", RunScenarioAsync).ProducesProblem(StatusCodes.Status409Conflict);
        test.MapPost("/seasons/{seasonId:guid}/content", PublishContentAsync).ProducesProblem(StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Publishes a pack of content files into a season (D-534): until the admin's content page (4.10), the E2E scenarios of
    /// events load their decks here. The engine checks the pack as for any publication.
    /// </summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> PublishContentAsync(
        Guid seasonId, TestContentRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (!await db.Seasons.AnyAsync(x => x.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        if ((request.Objects ?? []).Any(o => o is null) || (request.Wheels ?? []).Any(w => w is null))
        {
            return Invalid("objects", "Every content file is a JSON text.");
        }

        Engine.Content.ContentPack pack;
        try
        {
            pack = Engine.Content.ContentFiles.Pack(
                (request.Objects ?? []).Select((json, i) => ($"object-{i:D4}.json", json)),
                (request.Wheels ?? []).Select((json, i) => ($"wheel-{i:D4}.json", json)));
        }
        catch (System.Text.Json.JsonException e)
        {
            return Invalid("objects", e.Message);
        }

        var outcome = await bus.SendAsync(
            new CommandEnvelope(Guid.NewGuid(), seasonId, new Engine.Content.PublishContent(pack, request.Comment ?? ""), user.UserId()), ct);
        return outcome.IsAccepted ? TypedResults.NoContent() : Problem(outcome.Rejection!.Code, outcome.Rejection.Detail);
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

        return TypedResults.Ok(ClockOf(clock));
    }

    private static TestClockView ClockOf(IClock clock) =>
        new(clock.UtcNow, clock is IAdjustableClock, clock is IAdjustableClock adjustable ? adjustable.Offset.TotalMinutes : 0);

    private static TestRandomView RandomOf(IRandomSource random) =>
        new(random is IReseedableRandom reseedable ? reseedable.CurrentSeed : null, random is IReseedableRandom);

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

        SeasonPlayerRecord? player = null;
        if (name is not (DeadlineInHour or PollAndGlobalGoal))
        {
            var players = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == seasonId).ToListAsync(ct);
            var logins = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Login, ct);
            player = request.PlayerId is { } playerId
                ? players.FirstOrDefault(p => p.Id == playerId)
                : request.Player is { Length: > 0 } login
                    ? players.FirstOrDefault(p => string.Equals(logins.GetValueOrDefault(p.UserId), login, StringComparison.OrdinalIgnoreCase))
                    : players.OrderBy(p => p.Name).FirstOrDefault();
            if (player is null)
            {
                return Problem("test.playerUnknown", "No such player in the season.");
            }
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

            case IrlProofPending:
                if (await SubmitScenarioProofAsync(player!.Id) is { } proofProblem)
                {
                    return proofProblem;
                }

                break;

            case CoopInvitation:
                if (await InviteScenarioPartnerAsync(player!.Id) is { } coopProblem)
                {
                    return coopProblem;
                }

                break;

            case PollAndGlobalGoal:
                if (await StartScenarioPollAndGoalAsync() is { } pollProblem)
                {
                    return pollProblem;
                }

                break;

            default:
                throw new InvalidOperationException($"Scenario {name} has no steps.");
        }

        return TypedResults.Ok(new TestScenarioView(name, sent));

        async Task<ProblemHttpResult?> SubmitScenarioProofAsync(Guid playerId)
        {
            if (state.ManualEffects.Values.FirstOrDefault(effect => effect.PlayerId == playerId
                    && (effect.IrlProof is null or { Status: IrlProofStatus.Rejected })
                    && IrlProofs.IsIrl(state, effect)) is not { } pending)
            {
                return Problem("test.noIrlEffect", "The player needs an IRL effect that can take a proof.");
            }

            return await SendAsync(new SubmitIrlProof(playerId, pending.EffectId, [], "https://example.invalid/ivent-test-irl")) is { } refused
                ? Problem(refused, "The test proof could not be submitted.")
                : null;
        }

        async Task<ProblemHttpResult?> InviteScenarioPartnerAsync(Guid playerId)
        {
            if (!state.Players.TryGetValue(playerId, out var roller) || roller.ActiveRunId is not { } runId
                || !state.Runs.TryGetValue(runId, out var run) || run.Status != RunStatus.Playing || run.PlayerId != playerId)
            {
                return Problem(RejectionCodes.CoopRunNotActive, "The roller needs a run of their own in progress.");
            }

            if (run.Coop is not null)
            {
                return Problem(RejectionCodes.CoopAlreadyJoined, "The run already has a partner.");
            }

            if (!await db.Games.AsNoTracking().AnyAsync(game => game.Id == run.GameId && game.IsCoop, ct))
            {
                return Problem(RejectionCodes.CoopNotCoopGame, "The roller needs a coop game.");
            }

            var first = FinishLine.First(state);
            var candidates = state.Players.Values
                .Where(candidate => candidate.PlayerId != playerId && candidate.PlayerId != first && !candidate.IsInactive
                    && candidate.Phase == TurnPhase.Idle && candidate.ActiveRunId is null && candidate.Offer is null && candidate.Choice is null
                    && !candidate.Exclusions.Any(exclusion => exclusion.GameId == run.GameId)
                    && !candidate.Wallet.Bets.Any(bet => bet.Status == BetStatus.Open && bet.RunId == run.RunId))
                .ToList();
            var userIds = candidates.Select(candidate => candidate.UserId).ToList();
            var enabled = await db.Users.AsNoTracking()
                .Where(account => userIds.Contains(account.Id) && account.Role == Role.Player && !account.IsDeleted)
                .Select(account => account.Id).ToHashSetAsync(ct);
            var partner = candidates.Where(candidate => enabled.Contains(candidate.UserId))
                .OrderBy(candidate => candidate.Name, StringComparer.Ordinal).ThenBy(candidate => candidate.PlayerId).FirstOrDefault();
            if (partner is null)
            {
                return Problem("test.noCoopPartner", "The season needs another player who could accept this coop invitation.");
            }

            return await SendAsync(new InviteToCoop(playerId, partner.PlayerId)) is { } refused
                ? Problem(refused, "The invitation could not be sent.")
                : null;
        }

        async Task<ProblemHttpResult?> StartScenarioPollAndGoalAsync()
        {
            var now = clock.UtcNow;
            if (state.Status == SeasonStatus.Closing || state.Deadline is { } deadline && now >= deadline)
            {
                return Problem(RejectionCodes.SeasonDeadlinePassed, "The deadline has passed.");
            }

            if (state.Status != SeasonStatus.Active)
            {
                return Problem(RejectionCodes.SeasonNotActive, "Start the season before loading this scenario.");
            }

            if (state.Rules.Features is not { Events: true, Polls: true, GlobalEvents: true })
            {
                return Problem("test.pollGoalFeaturesOff", "Events, polls and global events are required.");
            }

            if (state.GlobalEvents.Values.Count(global => global.RunsAt(now)) >= (state.Rules.GlobalEvents?.MaxActive ?? 0))
            {
                return Problem(RejectionCodes.GlobalEventTooMany, "End a global event before loading this scenario.");
            }

            if (state.Catalog.Live(ScenarioGoalEvent) is not { Kind: ObjectKind.Event } definition
                || definition is { Manual: false, Effect.Actions: [RequestChoiceAction { Vote: not null }, ..] })
            {
                return Problem("test.noScenarioGoalEvent", "Publish the standard Tailwind event before loading this scenario.");
            }

            // The goal is the step most likely to be refused. Leave the poll untouched if the launch fails.
            if (await SendAsync(s_scenarioGoal) is { } goalRefused)
            {
                return Problem(goalRefused, "The goal could not be launched.");
            }

            // A concurrent admin action can change season status or flags between the two ordinary commands.
            return await SendAsync(s_scenarioPoll) is { } pollRefused
                ? Problem("test.goalStartedPollRefused", $"The goal was launched, but the poll was refused ({pollRefused}).")
                : null;
        }

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
