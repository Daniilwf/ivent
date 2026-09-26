using System.Security.Claims;
using System.Threading.RateLimiting;
using GameEvent.Engine.Files;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Files;

/// <summary>
/// A stored file (D-108): <c>url</c> serves the file, <c>thumbnailUrl</c> the small one for the map and lists.
/// <c>duplicate</c> — the same upload was already stored (a retry with the same command id).
/// </summary>
public sealed record StoredFileView(Guid Id, string MediaType, int Width, int Height, int Frames, string Url, string ThumbnailUrl, bool Duplicate);

/// <summary>The services a stored upload goes through (D-108, D-117).</summary>
public sealed record FileServices(GameEventDbContext Db, FileStorage Storage, FileLimits Limits, IIdGenerator Ids, IClock Clock, CommandBus Bus);

/// <summary>A picture by link (D-117): https from Tenor, Giphy or Klipy; the server downloads it and stores it as an upload.</summary>
public sealed record FileFromUrlRequest(Guid CommandId, string? Url);

/// <summary>A stored file where a proof or a page shows it (D-116): the picture and its thumbnail.</summary>
public sealed record FileLinkView(Guid Id, string Url, string ThumbnailUrl)
{
    public static FileLinkView Of(Guid id) => new(id, $"/api/files/{id}", $"/api/files/{id}/thumbnail");
}

/// <summary>
/// An upload: <c>commandId</c> makes a retry safe, <c>file</c> is one JPEG, PNG, WebP or GIF. A refusal carries a
/// <c>code</c>: <c>file.typeInvalid</c>, <c>file.broken</c>, <c>file.tooManyPixels</c> (422), <c>file.tooLarge</c> (413),
/// <c>file.dailyLimit</c> (429), <c>command.idReused</c> (409).
/// </summary>
public sealed record FileUploadForm(Guid CommandId, IFormFile File);

/// <summary>
/// Files (SPEC «Файлы», «Трудности реализации» — загрузка файлов, D-25, D-108): upload a picture, get it and its
/// thumbnail back. Only signed-in users; what is served is what the server wrote, under headers that keep a browser
/// from reading it as anything but a picture.
/// </summary>
public static class FileEndpoints
{
    public const string UploadRateLimit = "upload";

    /// <summary>Downloads by link per user in an hour, failed ones included (D-117): each costs the server traffic.</summary>
    public const string DownloadRateLimit = "download";

    /// <summary>One picture is decoded at a time (D-108): a small server keeps its memory for the site.</summary>
    private static readonly SemaphoreSlim s_processing = new(1, 1);

    /// <summary>How long an upload waits for the picture before it in line.</summary>
    private static readonly TimeSpan s_processingWait = TimeSpan.FromSeconds(30);

    /// <summary>Room for the form around the file itself.</summary>
    public const long FormOverheadBytes = 64 * 1024;

    public static void AddFiles(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var limits = builder.Configuration.GetSection("Files").Get<FileLimits>() ?? new FileLimits();
        var root = Path.GetFullPath(Path.Combine(
            builder.Environment.ContentRootPath, builder.Configuration["Files:Path"] ?? Path.Combine("var", "files")));
        builder.Services.AddSingleton(limits);
        builder.Services.AddSingleton(new FileStorage(root));
        builder.Services.AddSingleton(DownloadSettingsFrom(builder.Configuration));
        builder.Services.AddSingleton<IHostResolver, DnsHostResolver>();
        builder.Services.AddSingleton<SafeDownloader>();
        builder.Services.Configure<FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = limits.MaxUploadBytes + FormOverheadBytes;
            o.ValueCountLimit = 16;
        });
        builder.Services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(o =>
            o.AddPolicy(UploadRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.UserId()?.ToString() ?? WebSecurity.ClientKey(ctx.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.UploadsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        builder.Services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(o =>
            o.AddPolicy(DownloadRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.UserId()?.ToString() ?? WebSecurity.ClientKey(ctx.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.DownloadsPerHour, Window = TimeSpan.FromHours(1), QueueLimit = 0 })));
    }

    /// <summary>
    /// <c>Files:Download</c> (D-117). A list in the configuration replaces the default hosts rather than adding to them:
    /// a host found compromised is taken out without a rebuild.
    /// </summary>
    internal static DownloadSettings DownloadSettingsFrom(IConfiguration configuration)
    {
        var section = configuration.GetSection("Files:Download");
        var settings = section.Get<DownloadSettings>() ?? new DownloadSettings();
        return section.GetSection("AllowedHosts").Get<string[]>() is { Length: > 0 } hosts ? settings with { AllowedHosts = hosts } : new DownloadSettings
        {
            TimeoutSeconds = settings.TimeoutSeconds,
            MaxRedirects = settings.MaxRedirects,
            MaxConcurrent = settings.MaxConcurrent,
            QueueWaitSeconds = settings.QueueWaitSeconds,
        };
    }

    /// <summary>The body limit of an upload request (the rest of the API stays at <see cref="WebSecurity.ApiBodyLimitBytes"/>).</summary>
    public static bool IsUpload(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && string.Equals(request.Path.Value?.TrimEnd('/'), "/api/files", StringComparison.OrdinalIgnoreCase);

    public static void MapFiles(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var files = api.MapGroup("/files").WithTags("Files").RequireAuthorization();

        files.MapPost("", UploadAsync)
            .DisableAntiforgery() // the group's CSRF filter checks the header token (D-26)
            .RequireRateLimiting(UploadRateLimit)
            .Accepts<FileUploadForm>("multipart/form-data")
            .Produces<StoredFileView>()
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .ProducesValidationProblem();

        files.MapPost("/from-url", FromUrlAsync)
            .RequireRateLimiting(DownloadRateLimit)
            .Produces<StoredFileView>()
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")
            .ProducesValidationProblem();

        files.MapGet("/{fileId:guid}", (Guid fileId, GameEventDbContext db, FileStorage storage, HttpContext http, CancellationToken ct) =>
                ServeAsync(fileId, thumbnail: false, db, storage, http, ct))
            .Produces<byte[]>(StatusCodes.Status200OK, FileNames.Webp, FileNames.Gif)
            .Produces(StatusCodes.Status404NotFound);

        files.MapGet("/{fileId:guid}/thumbnail", (Guid fileId, GameEventDbContext db, FileStorage storage, HttpContext http, CancellationToken ct) =>
                ServeAsync(fileId, thumbnail: true, db, storage, http, ct))
            .Produces<byte[]>(StatusCodes.Status200OK, FileNames.Webp, FileNames.Gif)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, ClaimsPrincipal principal, [AsParameters] FileServices services, CancellationToken ct)
    {
        if (principal.UserId() is not { } ownerId)
        {
            return TypedResults.Forbid();
        }

        if (!request.HasFormContentType)
        {
            return Invalid("file", "Send the picture as multipart/form-data with a command id.");
        }

        var form = await request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["commandId"], out var commandId) || commandId == Guid.Empty)
        {
            return Invalid("commandId", "A command id is required.");
        }

        // A retry of an upload already stored gets the same file, without storing the picture again
        if (await EarlierAsync(commandId, ownerId, services.Db, ct) is { } earlier)
        {
            return earlier;
        }

        if (form.Files.GetFile("file") is not { } file || form.Files.Count != 1)
        {
            return Invalid("file", "Exactly one picture is required.");
        }

        if (file.Length > services.Limits.MaxUploadBytes)
        {
            return TooLarge(services.Limits);
        }

        if (await OverDailyLimitAsync(ownerId, services, ct) is { } limited)
        {
            return limited;
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        {
            content = new byte[file.Length];
            await stream.ReadExactlyAsync(content, ct);
        }

        return await StoreAsync(content, commandId, ownerId, services, ct);
    }

    /// <summary>
    /// A picture by link (D-117): the link is checked and downloaded by <see cref="SafeDownloader"/>, then stored exactly
    /// as an upload — the type by the content, WebP or GIF, the same limits and the same retry by command id.
    /// </summary>
    private static async Task<IResult> FromUrlAsync(
        FileFromUrlRequest request, ClaimsPrincipal principal, SafeDownloader downloader, [AsParameters] FileServices services, CancellationToken ct)
    {
        if (principal.UserId() is not { } ownerId)
        {
            return TypedResults.Forbid();
        }

        if (request.CommandId == Guid.Empty)
        {
            return Invalid("commandId", "A command id is required.");
        }

        if (await EarlierAsync(request.CommandId, ownerId, services.Db, ct) is { } earlier)
        {
            return earlier;
        }

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return Invalid("url", "A link is required.");
        }

        if (await OverDailyLimitAsync(ownerId, services, ct) is { } limited)
        {
            return limited;
        }

        var (content, refused) = await downloader.DownloadAsync(request.Url.Trim(), ct);
        if (content is null)
        {
            return refused!.Code switch
            {
                ImageProcessor.TooLarge => TooLarge(services.Limits),
                SafeDownloader.Busy => Problem(StatusCodes.Status503ServiceUnavailable, "The server is busy.", refused.Detail, refused.Code),
                _ => Problem(StatusCodes.Status422UnprocessableEntity, "The link was refused.", refused.Detail, refused.Code),
            };
        }

        return await StoreAsync(content, request.CommandId, ownerId, services, ct);
    }

    // Before any decoding or download: a user at the daily limit costs a count, not a picture (the queue decides for sure)
    private static async Task<IResult?> OverDailyLimitAsync(Guid ownerId, FileServices services, CancellationToken ct)
    {
        var since = services.Clock.UtcNow - FileRules.Day;
        return await services.Db.Files.CountAsync(f => f.OwnerId == ownerId && f.CreatedAt > since, ct) >= services.Limits.UploadsPerDay
            ? DailyLimit($"At most {services.Limits.UploadsPerDay} uploads in 24 hours.")
            : null;
    }

    private static async Task<IResult> StoreAsync(byte[] content, Guid commandId, Guid ownerId, FileServices services, CancellationToken ct) =>
        (await StoreFileAsync(content, commandId, ownerId, services, ct)).Answer;

    /// <summary>
    /// Stores a picture as an upload of <paramref name="ownerId"/> (D-108): the answer an endpoint gives, and the stored
    /// file's id when there is one (a cover found for the pool, D-118, uses it).
    /// </summary>
    internal static async Task<(IResult Answer, Guid? FileId)> StoreFileAsync(
        byte[] content, Guid commandId, Guid ownerId, FileServices services, CancellationToken ct, bool countTowardsLimit = true)
    {
        var (db, storage, limits, ids, _, bus) = services;
        if (!await s_processing.WaitAsync(s_processingWait, ct))
        {
            return (Problem(StatusCodes.Status503ServiceUnavailable, "The server is busy with other pictures.", "Try again in a minute.", SafeDownloader.Busy), null);
        }

        ProcessedFile? processed;
        Engine.Kernel.Rejection? rejection;
        try
        {
            (processed, rejection) = await Task.Run(() => ImageProcessor.Process(content, limits), CancellationToken.None);
        }
        finally
        {
            s_processing.Release();
        }

        if (processed is null)
        {
            return (rejection!.Code == ImageProcessor.TooLarge
                ? TooLarge(limits)
                : Problem(StatusCodes.Status422UnprocessableEntity, "The picture was refused.", rejection.Detail, rejection.Code), null);
        }

        // The bytes go to disk first; the queue then records whose they are. From here the request is not cancelled: a
        // command in the queue is executed anyway, and its files must be there. Only a clear "no" takes them back; after a
        // failure they stay, an orphan at worst, never a record without its file.
        var fileId = ids.NewId();
        var names = new[] { FileNames.Main(fileId, processed.MediaType), FileNames.Thumbnail(fileId, processed.MediaType) };
        await storage.WriteAsync(names[0], processed.Main, CancellationToken.None);
        await storage.WriteAsync(names[1], processed.Thumbnail, CancellationToken.None);
        var outcome = await bus.SendAsync(
            new CommandEnvelope(
                commandId,
                Guid.Empty,
                new RecordFile(fileId, ownerId, processed.MediaType, processed.Main.LongLength, processed.Width, processed.Height, processed.Frames, countTowardsLimit ? limits.UploadsPerDay : int.MaxValue),
                ownerId),
            CancellationToken.None);
        var stored = outcome.Events.Select(e => e.Event).OfType<FileStored>().SingleOrDefault()?.FileId;
        if (stored != fileId)
        {
            foreach (var name in names)
            {
                storage.Delete(name);
            }
        }

        if (outcome.IsAccepted)
        {
            return (await ViewAsync(stored!.Value, outcome.IsDuplicate, db, ct), stored);
        }

        // Two retries at once: the other one was recorded first with a file of its own — answer with that one
        if (outcome.Rejection!.Code == RejectionCodes.CommandIdReused && await EarlierAsync(commandId, ownerId, db, ct) is { } first)
        {
            return (first, null);
        }

        return (outcome.Rejection.Code == FileRules.DailyLimit
            ? DailyLimit(outcome.Rejection.Detail)
            : Problem(StatusCodes.Status409Conflict, "The command was rejected.", outcome.Rejection.Detail, outcome.Rejection.Code), null);
    }

    /// <summary>The answer to a command id seen before: the same user's stored file, or a refusal of someone else's id.</summary>
    private static async Task<IResult?> EarlierAsync(Guid commandId, Guid ownerId, GameEventDbContext db, CancellationToken ct)
    {
        var earlier = await db.Events.AsNoTracking().Where(e => e.SeasonId == Guid.Empty && e.CommandId == commandId).ToListAsync(ct);
        if (earlier.Count == 0)
        {
            return null;
        }

        return earlier.Count == 1 && earlier[0].AuthorId == ownerId
            && EventCodec.Decode(new StoredEvent(earlier[0].Type, earlier[0].Version, earlier[0].Data)) is FileStored stored
            ? await ViewAsync(stored.FileId, duplicate: true, db, ct)
            : Problem(StatusCodes.Status409Conflict, "The command was rejected.", $"Command id {commandId} was used by another command.", RejectionCodes.CommandIdReused);
    }

    private static async Task<IResult> ViewAsync(Guid fileId, bool duplicate, GameEventDbContext db, CancellationToken ct)
    {
        if (await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && !f.IsDeleted, ct) is not { } record)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new StoredFileView(
            record.Id, record.MediaType, record.Width, record.Height, record.Frames, $"/api/files/{record.Id}", $"/api/files/{record.Id}/thumbnail", duplicate));
    }

    private static async Task<IResult> ServeAsync(Guid fileId, bool thumbnail, GameEventDbContext db, FileStorage storage, HttpContext http, CancellationToken ct)
    {
        var record = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && !f.IsDeleted, ct);
        var name = record is null ? null : thumbnail ? FileNames.Thumbnail(fileId, record.MediaType) : FileNames.Main(fileId, record.MediaType);
        if (record is null || storage.OpenRead(name!) is not { } stream)
        {
            return TypedResults.NotFound();
        }

        // A picture and nothing else: no sniffing, no scripts even if opened directly, cached by this browser only
        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'none'; sandbox";
        headers.CacheControl = "private, max-age=31536000, immutable";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        return TypedResults.File(stream, record.MediaType, fileDownloadName: null, enableRangeProcessing: false);
    }

    private static ProblemHttpResult TooLarge(FileLimits limits) =>
        Problem(
            StatusCodes.Status413PayloadTooLarge,
            "The file is too large.",
            $"A picture is at most {limits.MaxUploadBytes / 1024 / 1024} MB, a GIF at most {limits.MaxGifBytes / 1024 / 1024} MB.",
            ImageProcessor.TooLarge);

    private static ProblemHttpResult DailyLimit(string detail) =>
        Problem(StatusCodes.Status429TooManyRequests, "Too many uploads today.", detail, FileRules.DailyLimit);

    private static ProblemHttpResult Problem(int status, string title, string detail, string code) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
