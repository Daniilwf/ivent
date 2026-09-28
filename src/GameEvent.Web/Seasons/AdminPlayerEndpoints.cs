using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using ActionResult = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<GameEvent.Web.Seasons.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
    Microsoft.AspNetCore.Http.HttpResults.ValidationProblem,
    Microsoft.AspNetCore.Http.HttpResults.NotFound,
    Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>;

namespace GameEvent.Web.Seasons;

/// <summary>A season in the list everyone signed in sees: newest first.</summary>
public sealed record SeasonListItemView(Guid Id, string Name, SeasonStatus Status, DateTimeOffset? Deadline, DateTimeOffset CreatedAt);

/// <summary>
/// The admin creates a season (SE1, D-123) with the default rules (<c>docs/ruleset.default.json</c>), changed later on the
/// rules page. <c>seasonId</c> is chosen by the client like the command id, so a retry creates the same season once.
/// </summary>
public sealed record CreateSeasonRequest(Guid CommandId, Guid SeasonId, string? Name, DateTimeOffset? Deadline = null);

/// <summary>
/// The admin adds an account with the player role to the season (SE4): by default on the start with zero points and
/// coins; mid-season the admin sets them.
/// </summary>
public sealed record AddPlayerRequest(Guid CommandId, Guid UserId, string? CellId = null, int Points = 0, int Coins = 0);

public sealed record ResourceDeltaRequest(string? Resource, int Delta);

/// <summary>The admin corrects a player (D-21): position, points, coins, resources, dropping an offered game; always with a comment.</summary>
public sealed record AdjustPlayerRequest(
    Guid CommandId,
    string? Comment,
    string? CellId = null,
    int PointsDelta = 0,
    int CoinsDelta = 0,
    IReadOnlyList<ResourceDeltaRequest>? ResourceDeltas = null,
    bool DiscardOffer = false);

/// <summary>The admin marks a player inactive (out of the active game) or brings them back (SE5).</summary>
public sealed record InactivityRequest(Guid CommandId, bool IsInactive);

/// <summary>
/// A player as the admin sees them (SE5, D-123): the balance and the place in the turn, the inactivity flag, when they
/// last acted themselves and the hint — no action of their own for the rules' <c>inactiveHintDays</c> while not marked
/// inactive. <c>playing</c> — a run is going on: a long game is a reason to be quiet, not a sign of leaving.
/// <c>choosingBranch</c> — the player's throw waits at a fork (D-305): the admin may discard the choice, the steps left burn.
/// </summary>
public sealed record AdminPlayerView(
    Guid Id,
    Guid UserId,
    string Name,
    string CellId,
    int Points,
    int Coins,
    IReadOnlyDictionary<string, int> Resources,
    TurnPhase Phase,
    bool Playing,
    bool IsInactive,
    DateTimeOffset? LastActionAt,
    bool InactiveHint,
    bool ChoosingBranch = false);

/// <summary>
/// Seasons and their players for the admin (SPEC «Админка», E2, D-123): create a season, add and correct players, the
/// inactivity flag and its hint. Every change is a command of the season log.
/// </summary>
public static class AdminPlayerEndpoints
{
    public static void MapSeasonsAndPlayers(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/seasons", ListAsync).WithTags("Seasons").RequireAuthorization();

        var admin = api.MapGroup("/admin/seasons").WithTags("Admin").RequireAuthorization(Policies.Admin);
        admin.MapPost("", CreateAsync).WithActionErrors();

        var players = admin.MapGroup("/{seasonId:guid}/players");
        players.MapGet("", PlayersAsync).RequireRateLimiting(AppSetup.AdminReadRateLimit).Produces(StatusCodes.Status404NotFound);
        players.MapPost("", AddAsync).WithActionErrors();
        players.MapPost("/{playerId:guid}/adjust", AdjustAsync).WithActionErrors();
        players.MapPost("/{playerId:guid}/inactive", (Guid seasonId, Guid playerId, InactivityRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SendAsync(seasonId, request.CommandId, new SetPlayerInactive(playerId, request.IsInactive), user, db, bus, ct))
            .WithActionErrors();
    }

    /// <summary>A player's id in a season: one per account and season, the same on every retry (D-123).</summary>
    public static Guid PlayerIdFor(Guid seasonId, Guid userId)
    {
        Span<byte> source = stackalloc byte[32];
        seasonId.TryWriteBytes(source[..16]);
        userId.TryWriteBytes(source[16..]);
        return new Guid(SHA256.HashData(source)[..16]);
    }

    private static async Task<Ok<IReadOnlyList<SeasonListItemView>>> ListAsync(GameEventDbContext db, CancellationToken ct)
    {
        var seasons = await db.Seasons.AsNoTracking().ToListAsync(ct);
        IReadOnlyList<SeasonListItemView> views = [.. seasons
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new SeasonListItemView(s.Id, s.Name, s.Status, s.Deadline, s.CreatedAt))];
        return TypedResults.Ok(views);
    }

    private static async Task<ActionResult> CreateAsync(CreateSeasonRequest request, ClaimsPrincipal user, CommandBus bus, CancellationToken ct)
    {
        if (request.CommandId == Guid.Empty || request.SeasonId == Guid.Empty)
        {
            return Invalid("seasonId", "A command id and a season id are required.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Invalid("name", "A season has a name.");
        }

        // The engine checks the name, the rules and the deadline; a season id taken is refused by the log itself
        return await SeasonEndpoints.SendAsync(
            request.SeasonId, request.CommandId, new CreateSeason(request.SeasonId, request.Name.Trim(), RulesetJson.Default(), request.Deadline), user.UserId(), bus, ct);
    }

    private static async Task<ActionResult> AddAsync(
        Guid seasonId, AddPlayerRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (await SeasonEndpoints.PrecheckAsync(seasonId, request.CommandId, db, ct) is { } refused)
        {
            return refused;
        }

        if (request.UserId == Guid.Empty)
        {
            return Invalid("userId", "An account id is required.");
        }

        if (await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.UserId && !u.IsDeleted, ct) is not { } account)
        {
            return Rejected("account.unknown", $"Account {request.UserId} does not exist.");
        }

        // Only a player acts in a season (the season's actions need the role): an admin or a spectator would be a stone
        if (account.Role != Role.Player)
        {
            return Rejected("account.notAPlayer", "Only an account with the player role takes part in a season.");
        }

        var command = new AddSeasonPlayer(PlayerIdFor(seasonId, account.Id), account.Id, account.Name, request.CellId, request.Points, request.Coins);
        return await SeasonEndpoints.SendAsync(seasonId, request.CommandId, command, user.UserId(), bus, ct);
    }

    private static async Task<ActionResult> AdjustAsync(
        Guid seasonId, Guid playerId, AdjustPlayerRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (request.Comment is null)
        {
            return Invalid("comment", "A comment is required.");
        }

        if ((request.ResourceDeltas ?? []).Any(r => r is null || r.Resource is null))
        {
            return Invalid("resourceDeltas", "Every resource change names its resource.");
        }

        var command = new AdjustPlayer(
            playerId,
            request.Comment,
            request.CellId,
            request.PointsDelta,
            request.CoinsDelta,
            [.. (request.ResourceDeltas ?? []).Select(r => new ResourceDelta(r.Resource!, r.Delta))],
            request.DiscardOffer);
        return await SendAsync(seasonId, request.CommandId, command, user, db, bus, ct);
    }

    private static async Task<Results<Ok<IReadOnlyList<AdminPlayerView>>, NotFound>> PlayersAsync(
        Guid seasonId, GameEventDbContext db, IClock clock, CancellationToken ct)
    {
        if (await db.Seasons.AsNoTracking().SingleOrDefaultAsync(s => s.Id == seasonId, ct) is not { } season)
        {
            return TypedResults.NotFound();
        }

        var hintDays = RulesetJson.Parse(season.RulesetJson).Season.InactiveHintDays;
        var players = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == seasonId).ToListAsync(ct);
        var userIds = players.Select(p => (Guid?)p.UserId).ToList();

        // A player's own actions are the commands they authored in this season; the admin's corrections do not count
        var lastSequences = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && userIds.Contains(e.AuthorId))
            .GroupBy(e => e.AuthorId)
            .Select(g => g.Max(e => e.Sequence))
            .ToListAsync(ct);
        var lastActions = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && lastSequences.Contains(e.Sequence))
            .ToDictionaryAsync(e => e.AuthorId!.Value, e => e.OccurredAt, ct);

        // The quiet time counts from the start of the season, or from the player's joining when later (SE4)
        var milestones = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && (e.Type == "season-status-changed" || e.Type == "season-player-added"))
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        var decoded = milestones.Select(e => (e.OccurredAt, Event: EventCodec.Decode(new StoredEvent(e.Type, e.Version, e.Data)))).ToList();
        var started = decoded.FirstOrDefault(e => e.Event is SeasonStatusChanged { To: SeasonStatus.Active }).OccurredAt;
        var joined = decoded.Where(e => e.Event is SeasonPlayerAdded).GroupBy(e => ((SeasonPlayerAdded)e.Event).PlayerId).ToDictionary(g => g.Key, g => g.Max(e => e.OccurredAt));
        var now = clock.UtcNow;
        IReadOnlyList<AdminPlayerView> views = [.. players
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p =>
            {
                DateTimeOffset? last = lastActions.TryGetValue(p.UserId, out var at) ? at : null;
                var quietSince = new[] { started, joined.GetValueOrDefault(p.Id), last ?? default }.Max();
                return new AdminPlayerView(
                    p.Id,
                    p.UserId,
                    p.Name,
                    p.CellId,
                    p.Points,
                    p.Coins,
                    JsonSerializer.Deserialize<Dictionary<string, int>>(p.ResourcesJson) ?? [],
                    p.Phase,
                    p.Phase == TurnPhase.Playing,
                    p.IsInactive,
                    last,
                    season.Status == SeasonStatus.Active && !p.IsInactive && !p.Frozen && now - quietSince >= TimeSpan.FromDays(hintDays),
                    p.ChoiceJson is { } choice
                        && JsonSerializer.Deserialize<Engine.Turns.PendingChoice>(choice, EngineJson.Options)?.Kind == Engine.Turns.ChoiceKind.Branch);
            })];
        return TypedResults.Ok(views);
    }

    private static async Task<ActionResult> SendAsync(
        Guid seasonId, Guid commandId, ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
        await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);

    private static ProblemHttpResult Rejected(string code, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The command was rejected.", detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
