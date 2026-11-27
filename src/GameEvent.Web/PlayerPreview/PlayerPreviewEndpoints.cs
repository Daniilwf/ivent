using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Files;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Accounts;
using GameEvent.Web.Economy;
using GameEvent.Web.Files;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.PlayerPreview;

/// <summary>The selected participant's game, read by the real administrator without impersonating that participant.</summary>
public sealed record PlayerPreviewView(Guid PlayerId, string PlayerName, SeasonView Season, EconomyView? Economy);

/// <summary>Explicitly scoped, audited game reads; the marker limits a caller and never grants a permission.</summary>
public static partial class PlayerPreviewEndpoints
{
    public const string FeatureKey = "Site:PlayerPreview";
    public const string ContextHeader = "X-GameEvent-Player-Preview";
    public const string ReadOnlyCode = "playerPreview.readOnly";

    private sealed record ReadScope;
    private static readonly ReadScope s_readScope = new();

    /// <summary>Marks even refused preview responses as private before authentication may short-circuit the pipeline.</summary>
    public static void UsePlayerPreviewPrivacyHeaders(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey(ContextHeader) || IsPreviewReadPath(http.Request.Path))
            {
                http.Response.OnStarting(() =>
                {
                    NoStore(http.Response);
                    return Task.CompletedTask;
                });
            }

            await next(http);
        });
    }

    public static void MapPlayerPreview(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin/seasons/{seasonId:guid}/players/{playerId:guid}/preview")
            .WithTags("Admin")
            .RequireAuthorization(Policies.Admin)
            .WithMetadata(s_readScope);

        group.MapGet("/", ReadAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        group.MapGet("/files/{fileId:guid}", (Guid seasonId, Guid playerId, Guid fileId, ClaimsPrincipal user,
                IConfiguration configuration, GameEventDbContext db, FileStorage storage, HttpContext http,
                ILoggerFactory loggers, CancellationToken ct) =>
            ReadFileAsync(seasonId, playerId, fileId, false, user, configuration, db, storage, http, loggers, ct))
            .Produces<byte[]>(StatusCodes.Status200OK, FileNames.Webp, FileNames.Gif)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        group.MapGet("/files/{fileId:guid}/thumbnail", (Guid seasonId, Guid playerId, Guid fileId, ClaimsPrincipal user,
                IConfiguration configuration, GameEventDbContext db, FileStorage storage, HttpContext http,
                ILoggerFactory loggers, CancellationToken ct) =>
            ReadFileAsync(seasonId, playerId, fileId, true, user, configuration, db, storage, http, loggers, ct))
            .Produces<byte[]>(StatusCodes.Status200OK, FileNames.Webp, FileNames.Gif)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>Runs after authorization and before binding or CSRF filters, so a scoped request cannot reach a write.</summary>
    public static void UsePlayerPreview(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            var allowedRead = http.GetEndpoint()?.Metadata.GetMetadata<ReadScope>() is not null || IsPreviewReadPath(http.Request.Path);
            if (allowedRead)
            {
                NoStore(http.Response);
            }

            if (http.Request.Headers.ContainsKey(ContextHeader)
                && (!(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method))
                    || !allowedRead))
            {
                NoStore(http.Response);
                await TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Player preview only reads the selected game.",
                    extensions: new Dictionary<string, object?> { ["code"] = ReadOnlyCode }).ExecuteAsync(http);
                return;
            }

            await next(http);
        });
    }

    private static async Task<Results<Ok<PlayerPreviewView>, NotFound>> ReadAsync(
        Guid seasonId, Guid playerId, ClaimsPrincipal user, IConfiguration configuration, GameEventDbContext db,
        ContentCards contentCards, SeasonStateReader seasonStates, IClock clock, HttpResponse response,
        ILoggerFactory loggers, CancellationToken ct)
    {
        NoStore(response);
        if (await PlayerAsync(seasonId, playerId, configuration, db, ct) is not { } player)
        {
            return TypedResults.NotFound();
        }

        var result = await SeasonEndpoints.ReadForUserAsync(seasonId, player.UserId, true, db, contentCards, seasonStates, clock, ct);
        if (result.Result is not Ok<SeasonView> { Value: { Me: not null } season })
        {
            return TypedResults.NotFound();
        }

        EconomyView? economy = null;
        if (season.Economy is { } features && (features.Items || features.Shop || features.Bets))
        {
            var economyResult = await EconomyEndpoints.ReadForUserAsync(seasonId, player.UserId, db, clock, ct);
            if (economyResult.Result is not Ok<EconomyView> { Value: { } value })
            {
                return TypedResults.NotFound();
            }

            economy = value;
        }

        var logger = loggers.CreateLogger(typeof(PlayerPreviewEndpoints));
        if (logger.IsEnabled(LogLevel.Information))
        {
            var actorId = user.UserId();
            var sessionId = user.SessionId();
            LogRead(logger, actorId, sessionId, playerId, seasonId);
        }
        return TypedResults.Ok(new PlayerPreviewView(playerId, player.Name, ScopedFiles(season, seasonId, playerId), economy));
    }

    private static async Task<IResult> ReadFileAsync(
        Guid seasonId, Guid playerId, Guid fileId, bool thumbnail, ClaimsPrincipal user,
        IConfiguration configuration, GameEventDbContext db, FileStorage storage, HttpContext http,
        ILoggerFactory loggers, CancellationToken ct)
    {
        NoStore(http.Response);
        if (await PlayerAsync(seasonId, playerId, configuration, db, ct) is null
            || !await IsProofFileAsync(seasonId, playerId, fileId, db, ct))
        {
            return TypedResults.NotFound();
        }

        var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && !f.IsDeleted && f.Kind == FileKind.Upload, ct);
        if (file is null)
        {
            return TypedResults.NotFound();
        }

        var result = FileEndpoints.ServePicture(fileId, file.MediaType, thumbnail, storage, http, immutable: false);
        if (result is not NotFound)
        {
            var logger = loggers.CreateLogger(typeof(PlayerPreviewEndpoints));
            if (logger.IsEnabled(LogLevel.Information))
            {
                var actorId = user.UserId();
                var sessionId = user.SessionId();
                LogFileRead(logger, actorId, sessionId, playerId, seasonId, fileId, thumbnail);
            }
        }

        return result;
    }

    private static Task<SeasonPlayerRecord?> PlayerAsync(
        Guid seasonId, Guid playerId, IConfiguration configuration, GameEventDbContext db, CancellationToken ct) =>
        configuration.GetValue<bool>(FeatureKey) && seasonId != Guid.Empty && playerId != Guid.Empty
            ? db.SeasonPlayers.AsNoTracking().SingleOrDefaultAsync(p => p.SeasonId == seasonId && p.Id == playerId, ct)
            : Task.FromResult<SeasonPlayerRecord?>(null);

    private static async Task<bool> IsProofFileAsync(Guid seasonId, Guid playerId, Guid fileId, GameEventDbContext db, CancellationToken ct)
    {
        var runs = db.Runs.Where(run => run.SeasonId == seasonId && (run.PlayerId == playerId || run.PartnerId == playerId));
        var proofFiles = await db.Proofs.AsNoTracking()
            .Where(proof => proof.SeasonId == seasonId && runs.Any(run => run.Id == proof.RunId))
            .Select(proof => proof.FilesJson).ToListAsync(ct);
        if (proofFiles.Any(json => JsonSerializer.Deserialize<Guid[]>(json, EngineJson.Options)!.Contains(fileId)))
        {
            return true;
        }

        var irlProofs = await db.ManualEffects.AsNoTracking()
            .Where(effect => effect.SeasonId == seasonId && effect.PlayerId == playerId && effect.IrlProofJson != null)
            .Select(effect => effect.IrlProofJson!).ToListAsync(ct);
        return irlProofs.Any(json => JsonSerializer.Deserialize<IrlProofState>(json, EngineJson.Options)!.Files.Contains(fileId));
    }

    private static SeasonView ScopedFiles(SeasonView season, Guid seasonId, Guid playerId)
    {
        FileLinkView File(Guid id)
        {
            var url = $"/api/admin/seasons/{seasonId}/players/{playerId}/preview/files/{id}";
            return new FileLinkView(id, url, $"{url}/thumbnail");
        }

        CompletedRunView Completed(CompletedRunView run) => run with
        {
            Proof = run.Proof is { } proof ? proof with { Files = [.. proof.Files.Select(file => File(file.Id))] } : null,
        };

        var me = season.Me!;
        return season with
        {
            Me = me with
            {
                LastCompleted = me.LastCompleted is { } last ? Completed(last) : null,
                Checks = me.Checks is { } checks ? [.. checks.Select(Completed)] : null,
                ManualEffects = [.. me.ManualEffects.Select(effect => effect with
                {
                    IrlProof = effect.IrlProof is { } proof ? proof with { Files = [.. proof.Files.Select(file => File(file.Id))] } : null,
                })],
            },
        };
    }

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "private, no-store";
        response.Headers.Vary = "Cookie";
    }

    // Keep malformed route identifiers as ordinary 404s while refusing every other read surface. This recognizes
    // only the shape, not an authorization: the mapped endpoints still require the real administrator and scope.
    private static bool IsPreviewReadPath(PathString path)
    {
        var parts = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: 7 or 9 or 10 }
            && string.Equals(parts[0], "api", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "admin", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[2], "seasons", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[4], "players", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[6], "preview", StringComparison.OrdinalIgnoreCase)
            && (parts.Length == 7
                || (string.Equals(parts[7], "files", StringComparison.OrdinalIgnoreCase)
                    && (parts.Length == 9 || string.Equals(parts[9], "thumbnail", StringComparison.OrdinalIgnoreCase))));
    }

    [LoggerMessage(EventName = "PlayerPreviewRead", Level = LogLevel.Information,
        Message = "Player preview read by {ActorId} in session {SessionId}: player {PlayerId}, season {SeasonId}")]
    private static partial void LogRead(ILogger logger, Guid? actorId, Guid? sessionId, Guid playerId, Guid seasonId);

    [LoggerMessage(EventName = "PlayerPreviewFileRead", Level = LogLevel.Information,
        Message = "Player preview file read by {ActorId} in session {SessionId}: player {PlayerId}, season {SeasonId}, file {FileId}, thumbnail {Thumbnail}")]
    private static partial void LogFileRead(ILogger logger, Guid? actorId, Guid? sessionId, Guid playerId, Guid seasonId, Guid fileId, bool thumbnail);
}
