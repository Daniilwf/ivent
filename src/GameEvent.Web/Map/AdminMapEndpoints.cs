using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Map;

/// <summary>
/// A player on the map as the editor needs them: the cell they stand on (it cannot be removed, D-308), whether they
/// have finished (their cell stays a finish) and whether they are choosing a branch (publication waits, D-305).
/// </summary>
public sealed record MapPlayerView(Guid PlayerId, string Name, string CellId, bool Finished, bool ChoosingBranch);

/// <summary>
/// The season's map in the editor (2.11): the map in force, the players on it, the mode (the editor publishes only in
/// <c>graph</c>, D-301) and the status (only a draft or a running season takes a new map).
/// </summary>
public sealed record AdminMapView(MapMode Mode, SeasonStatus Status, MapGraphView Map, IReadOnlyList<MapPlayerView> Players);

/// <summary>
/// A problem of a map: the engine's stable code (<c>map.…</c>; the editor's dictionary words it), what it is about — a
/// cell id, an arrow <c>from→to</c>, a zone id or <c>map</c> — and the engine's English message for the log.
/// </summary>
public sealed record MapProblemView(string Code, string Subject, string Message);

/// <summary>A zone whose roll filter leaves fewer available games than the rules want (D-307); it never blocks publication.</summary>
public sealed record ZoneGamesWarningView(string ZoneId, int Available, int Wanted);

/// <summary>
/// The check of a map before publication (SPEC «Редактор»): every problem at once — the map's own (MapValidator) and
/// the ones of the season now (occupied cells, pending branch choices) — and the zone warnings. <c>canPublish</c> — no
/// problems and the season takes a map now.
/// </summary>
public sealed record MapCheckView(bool CanPublish, IReadOnlyList<MapProblemView> Problems, IReadOnlyList<ZoneGamesWarningView> Warnings);

/// <summary>Publish a new version of the map (D-300, D-308); <c>comment</c> goes to the public log, at most 500 characters.</summary>
public sealed record PublishMapRequest(Guid CommandId, MapGraphView? Map, string? Comment = null);

/// <summary>The map editor's API (2.11): read the map in force, check a draft, publish it through the queue.</summary>
public static class AdminMapEndpoints
{
    /// <summary>
    /// A map is the one API body bigger than small JSON: a few hundred cells with coordinates. Past this size the
    /// request is refused before it is parsed (the validator has its own ceilings on cells and arrows).
    /// </summary>
    public const long MaxMapRequestBytes = 1024 * 1024;

    /// <summary>Whether the request carries a whole map: the check and the publication.</summary>
    public static bool IsMapBody(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return HttpMethods.IsPost(request.Method)
            && request.Path.StartsWithSegments("/api/admin/seasons")
            && (request.Path.Value?.EndsWith("/map/check", StringComparison.Ordinal) == true
                || request.Path.Value?.EndsWith("/map/publish", StringComparison.Ordinal) == true);
    }

    public static void MapAdminMap(this RouteGroupBuilder api)
    {
        var map = api.MapGroup("/admin/seasons/{seasonId:guid}/map").WithTags("Admin").RequireAuthorization(Policies.Admin);

        map.MapGet("/", GetAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Each check folds the season log: the editor checks as the admin edits, so a limit of its own, per admin
        map.MapPost("/check", CheckAsync)
            .RequireRateLimiting(AppSetup.MapCheckRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        map.MapPost("/publish", (Guid seasonId, PublishMapRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                PublishInvalid(request, out var graph) is { } invalid
                    ? Task.FromResult<Results<Ok<CommandResponse>, ProblemHttpResult, ValidationProblem, NotFound, ForbidHttpResult>>(invalid)
                    : PublishAsync(seasonId, request.CommandId, new PublishMap(graph!, request.Comment!), user, db, bus, ct))
            .WithActionErrors();
    }

    private static async Task<Results<Ok<AdminMapView>, NotFound>> GetAsync(Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        var season = await db.Seasons.AsNoTracking().SingleOrDefaultAsync(s => s.Id == seasonId, ct);
        var current = await SeasonMaps.CurrentAsync(db, seasonId, ct);
        if (season is null || current is null)
        {
            return TypedResults.NotFound();
        }

        var rules = JsonSerializer.Deserialize<Ruleset>(season.RulesetJson, EngineJson.Options)!;
        var players = await db.SeasonPlayers.AsNoTracking()
            .Where(p => p.SeasonId == seasonId)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.CellId, p.FinishOrder, p.ChoiceJson })
            .ToListAsync(ct);
        return TypedResults.Ok(new AdminMapView(
            rules.Features.MapMode,
            season.Status,
            SeasonMaps.ToView(current),
            [.. players.Select(p => new MapPlayerView(
                p.Id,
                p.Name,
                p.CellId,
                p.FinishOrder is not null,
                p.ChoiceJson is { } json && JsonSerializer.Deserialize<Engine.Turns.PendingChoice>(json, EngineJson.Options)?.Kind == Engine.Turns.ChoiceKind.Branch))]));
    }

    private static async Task<Results<Ok<MapCheckView>, ValidationProblem, NotFound>> CheckAsync(
        Guid seasonId, MapGraphView request, GameEventDbContext db, CancellationToken ct)
    {
        if (SeasonMaps.FromView(request) is not { } graph)
        {
            return MapMissing();
        }

        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        // The state the engine would decide on: the fold of the log (a read, never a write — invariant 2)
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
        if (!state.IsCreated)
        {
            return TypedResults.NotFound();
        }

        IReadOnlyList<MapError> problems = [.. MapValidator.Validate(graph, state.Rules), .. MapPublicationChecks.For(state, graph)];
        var warnings = ZoneWarnings.For(graph, state.Rules, state, await PoolReader.LoadAsync(db, ct));
        var open = state.Rules.Features.MapMode == MapMode.Graph && state.Status is SeasonStatus.Draft or SeasonStatus.Active;
        return TypedResults.Ok(new MapCheckView(
            open && problems.Count == 0 && graph != state.Map,
            [.. problems.Select(p => new MapProblemView(p.Code, p.Subject, p.Message))],
            [.. warnings.Select(w => new ZoneGamesWarningView(w.ZoneId, w.Available, w.Wanted))]));
    }

    private static ValidationProblem? PublishInvalid(PublishMapRequest request, out MapGraph? graph)
    {
        graph = SeasonMaps.FromView(request.Map);
        return graph is null
            ? MapMissing()
            : string.IsNullOrWhiteSpace(request.Comment) || request.Comment.Length > Limits.MaxCommentLength
                ? TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["comment"] = [$"A comment of at most {Limits.MaxCommentLength} characters is required."],
                })
                : null;
    }

    private static ValidationProblem MapMissing() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["map"] = ["A map with cells and edges is required."] });

    private static async Task<Results<Ok<CommandResponse>, ProblemHttpResult, ValidationProblem, NotFound, ForbidHttpResult>> PublishAsync(
        Guid seasonId, Guid commandId, ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
        await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);
}
