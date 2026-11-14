using System.Security.Claims;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.EntityFrameworkCore;

using ActionResult = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<GameEvent.Web.Seasons.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
    Microsoft.AspNetCore.Http.HttpResults.ValidationProblem,
    Microsoft.AspNetCore.Http.HttpResults.NotFound,
    Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>;

namespace GameEvent.Web.Seasons;

/// <summary>The admin moves the season to its next status (SE1, D-101): closing early, finishing, archiving.</summary>
public sealed record SeasonStatusRequest(Guid CommandId, SeasonStatus? To);

/// <summary>The admin sets or removes (null) the deadline; only in draft or active (D-101).</summary>
public sealed record SeasonDeadlineRequest(Guid CommandId, DateTimeOffset? Deadline);

/// <summary>
/// A pending manual effect of any player, oldest first, for the admin to resolve (D-102); <c>irl</c> — an IRL one (D-509,
/// D-820): its proof is checked in the IRL proof queue, or the admin closes it «не применимо» here; always set, optional
/// for older clients.
/// </summary>
public sealed record AdminManualEffectView(
    Guid Id, Guid PlayerId, string PlayerName, Engine.Rulesets.EventKind DrawEvent, Engine.Effects.ManualEffectSource Source, Guid? RunId, string? ObjectId = null, bool Irl = false);

/// <summary>An admin action on the season with nothing else to say (D-113: recalculate the finish bonuses).</summary>
public sealed record SeasonActionRequest(Guid CommandId);

/// <summary>Undo a whole earlier command (D-104); <c>comment</c> is required, at most 500 characters.</summary>
public sealed record UndoRequest(Guid CommandId, Guid TargetCommandId, string? Comment = null);

/// <summary>
/// A command of the season log, newest first (D-104): what the admin picks to undo. <c>undone</c> — undone already;
/// <c>events</c> — the types of its events in order; <c>playerName</c>, <c>gameTitle</c>, <c>comment</c> — whom and what
/// the command touched, from its first event that names them, so identical rows are told apart before an undo (K1A;
/// null when none does).
/// </summary>
public sealed record AdminCommandView(
    Guid CommandId,
    string CommandType,
    string? AuthorName,
    DateTimeOffset OccurredAt,
    bool Undone,
    IReadOnlyList<string> Events,
    string? PlayerName = null,
    string? GameTitle = null,
    string? Comment = null);

/// <summary>The admin's season actions: the lifecycle (D-101) and resolving manual effects (D-102).</summary>
public static class AdminSeasonEndpoints
{
    public static void MapAdminSeasons(this RouteGroupBuilder api)
    {
        var season = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);

        season.MapPost("/status", (Guid seasonId, SeasonStatusRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            request.To is not { } to || !Enum.IsDefined(to)
                ? Task.FromResult<ActionResult>(
                    TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["A known season status is required."] }))
                : SendAsync(seasonId, request.CommandId, new ChangeSeasonStatus(to), user, db, bus, ct))
            .WithActionErrors();

        // A refusal with later commands that depend on it lists them in «related» (409 undo.dependents).
        season.MapPost("/undo", (Guid seasonId, UndoRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            request.TargetCommandId == Guid.Empty
                ? Task.FromResult<ActionResult>(TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["targetCommandId"] = ["The command to undo is required."],
                }))
                : string.IsNullOrWhiteSpace(request.Comment) || request.Comment.Length > SeasonEndpoints.MaxCommentLength
                    ? Task.FromResult<ActionResult>(TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["comment"] = [$"A comment of at most {SeasonEndpoints.MaxCommentLength} characters is required."],
                    }))
                    : SendAsync(seasonId, request.CommandId, new Engine.Undo.UndoCommand(request.TargetCommandId, request.Comment), user, db, bus, ct))
            .WithActionErrors();

        // The integrity check (L4, D-105): the log's state against the stored one, differences listed.
        season.MapGet("/integrity", async Task<Microsoft.AspNetCore.Http.HttpResults.Results<
            Microsoft.AspNetCore.Http.HttpResults.Ok<Infrastructure.Seasons.IntegrityReport>,
            Microsoft.AspNetCore.Http.HttpResults.NotFound>> (Guid seasonId, GameEventDbContext db, CancellationToken ct) =>
                await Infrastructure.Seasons.SeasonIntegrity.CheckAsync(db, seasonId, ct) is { } report
                    ? TypedResults.Ok(report)
                    : TypedResults.NotFound())
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // The season archive (D-32, D-105): the log, users by login, the pool; loaded by `npm run season:import`.
        season.MapGet("/export", async Task<IResult> (Guid seasonId, GameEventDbContext db, Engine.Kernel.IClock clock, CancellationToken ct) =>
            {
                var archive = await Infrastructure.Seasons.SeasonTransfer.ExportAsync(db, seasonId, clock.UtcNow, ct);
                if (archive is null)
                {
                    return TypedResults.NotFound();
                }

                var zip = new MemoryStream();
                await Infrastructure.Seasons.SeasonTransfer.WriteZipAsync(archive, zip, ct);
                zip.Position = 0;
                return TypedResults.File(zip, "application/zip", $"season-{seasonId:N}-{clock.UtcNow:yyyyMMdd-HHmm}.zip");
            })
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        season.MapGet("/commands", GetCommandsAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        season.MapGet("/effects", GetEffectsAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Any player's effect; the comment is required (D-89, D-102).
        season.MapPost("/effects/{effectId:guid}/resolve", (Guid seasonId, Guid effectId, ResolveEffectRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            SeasonEndpoints.ResolveInvalid(request) is { } invalid
                ? Task.FromResult<ActionResult>(invalid)
                : SendAsync(seasonId, request.CommandId, new Engine.Effects.ResolveManualEffect(effectId, request.Outcome!.Value, request.Comment, PlayerId: null), user, db, bus, ct))
            .WithActionErrors();

        season.MapPost("/deadline", (Guid seasonId, SeasonDeadlineRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            SendAsync(seasonId, request.CommandId, new SetSeasonDeadline(request.Deadline), user, db, bus, ct))
            .WithActionErrors();

        // D-113: «Пересчитать бонусы по текущим правилам» — the only way a finisher's bonus follows a change of the rules
        season.MapPost("/finish-bonuses/recalculate", (Guid seasonId, SeasonActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
            SendAsync(seasonId, request.CommandId, new Engine.Finish.RecalculateFinishBonuses(), user, db, bus, ct))
            .WithActionErrors();
    }

    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Results<
        Microsoft.AspNetCore.Http.HttpResults.Ok<IReadOnlyList<AdminManualEffectView>>,
        Microsoft.AspNetCore.Http.HttpResults.NotFound>> GetEffectsAsync(Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        // Effect ids are time-ordered (UUID v7), so their order is the order they were created in.
        IReadOnlyList<AdminManualEffectView> effects = await db.ManualEffects.AsNoTracking()
            .Where(x => x.SeasonId == seasonId)
            .Join(db.SeasonPlayers, x => x.PlayerId, p => p.Id, (x, p) => new { x, p.Name })
            .OrderBy(r => r.x.Id)
            .Select(r => new AdminManualEffectView(r.x.Id, r.x.PlayerId, r.Name, r.x.DrawEvent, r.x.Source, r.x.RunId, r.x.ObjectId, false))
            .ToListAsync(ct);

        // An IRL mark comes from the object's definition in the season's catalog (IrlProofs.IsIrl): only an object's text can be one
        if (effects.Any(e => e.ObjectId is not null))
        {
            var (state, _) = await Infrastructure.EventLog.EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
            effects = [.. effects.Select(e => e with
            {
                Irl = state.ManualEffects.TryGetValue(e.Id, out var pending) && Engine.Proofs.IrlProofs.IsIrl(state, pending),
            })];
        }

        return TypedResults.Ok(effects);
    }

    /// <summary>The newest commands of the season log, up to <paramref name="limit"/> (1–500, 100 by default).</summary>
    private static async Task<Microsoft.AspNetCore.Http.HttpResults.Results<
        Microsoft.AspNetCore.Http.HttpResults.Ok<IReadOnlyList<AdminCommandView>>,
        Microsoft.AspNetCore.Http.HttpResults.NotFound>> GetCommandsAsync(Guid seasonId, GameEventDbContext db, CancellationToken ct, int limit = 100)
    {
        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        limit = Math.Clamp(limit, 1, 500);
        var newest = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId)
            .GroupBy(e => e.CommandId)
            .Select(g => new { CommandId = g.Key, Last = g.Max(e => e.Sequence) })
            .OrderByDescending(g => g.Last)
            .Take(limit)
            .Select(g => g.CommandId)
            .ToListAsync(ct);
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && newest.Contains(e.CommandId))
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        var authors = await db.NamesAsync(rows.Select(r => r.AuthorId).OfType<Guid>(), ct);

        // Whom and what each command touched: the first player, run or game and comment its events name (K1A)
        var facts = rows.GroupBy(r => r.CommandId).ToDictionary(g => g.Key, g => CommandFacts.Of(g.Select(r => r.Data)));
        var players = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == seasonId).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var runIds = facts.Values.Select(f => f.RunId).OfType<Guid>().Distinct().ToList();
        var runGames = await db.Runs.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.GameId, ct);
        var gameIds = facts.Values.Select(f => f.GameId).OfType<Guid>().Concat(runGames.Values).Distinct().ToList();
        var titles = await db.Games.AsNoTracking().Where(g => gameIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Title, ct);
        IReadOnlyList<AdminCommandView> commands = [.. rows
            .GroupBy(r => r.CommandId)
            .OrderByDescending(g => g.Max(r => r.Sequence))
            .Select(g =>
            {
                var first = g.First();
                var fact = facts[g.Key];
                var gameId = fact.GameId ?? (fact.RunId is { } runId && runGames.TryGetValue(runId, out var game) ? game : null);
                return new AdminCommandView(
                    g.Key,
                    first.CommandType,
                    first.AuthorId is { } author ? authors.GetValueOrDefault(author) : null,
                    first.OccurredAt,
                    g.Any(r => r.UndoneByEventId is not null),
                    [.. g.Select(r => r.Type)],
                    fact.PlayerId is { } player ? players.GetValueOrDefault(player) : null,
                    gameId is { } id ? titles.GetValueOrDefault(id) : null,
                    fact.Comment);
            })];
        return TypedResults.Ok(commands);
    }

    private static async Task<ActionResult> SendAsync(
        Guid seasonId, Guid commandId, Engine.Kernel.ICommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        return await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct)
            ?? await SeasonEndpoints.SendAsync(seasonId, commandId, command, user.UserId(), bus, ct);
    }
}

/// <summary>The first player, run, game and comment the events of one command name, read from their stored JSON.</summary>
internal sealed record CommandFacts(Guid? PlayerId, Guid? RunId, Guid? GameId, string? Comment)
{
    public static CommandFacts Of(IEnumerable<string> events)
    {
        Guid? player = null, run = null, game = null;
        string? comment = null;
        foreach (var data in events)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            player ??= GuidOf(doc.RootElement, "playerId");
            run ??= GuidOf(doc.RootElement, "runId");
            game ??= GuidOf(doc.RootElement, "gameId");
            if (comment is null && doc.RootElement.TryGetProperty("comment", out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                comment = text.GetString();
            }
        }

        return new CommandFacts(player, run, game, comment);
    }

    private static Guid? GuidOf(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String && value.TryGetGuid(out var id)
            ? id
            : null;
}
