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
        builder.Services.Configure<FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = limits.MaxUploadBytes + FormOverheadBytes;
            o.ValueCountLimit = 16;
        });
        builder.Services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(o =>
            o.AddPolicy(UploadRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.UserId()?.ToString() ?? WebSecurity.ClientKey(ctx.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.UploadsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
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

        files.MapGet("/{fileId:guid}", (Guid fileId, GameEventDbContext db, FileStorage storage, HttpContext http, CancellationToken ct) =>
                ServeAsync(fileId, thumbnail: false, db, storage, http, ct))
            .Produces<byte[]>(StatusCodes.Status200OK, FileNames.Webp, FileNames.Gif)
            .Produces(StatusCodes.Status404NotFound);

        files.MapGet("/{fileId:guid}/thumbnail", (Guid fileId, GameEventDbContext db, FileStorage storage, HttpContext http, CancellationToken ct) =>
                ServeAsync(fileId, thumbnail: true, db, storage, http, ct))
            .Produces<byte[]>(StatusCodes.Status200OK, FileNames.Webp, FileNames.Gif)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request, ClaimsPrincipal principal, GameEventDbContext db, FileStorage storage, FileLimits limits, IIdGenerator ids, IClock clock, CommandBus bus, CancellationToken ct)
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
        if (await EarlierAsync(commandId, ownerId, db, ct) is { } earlier)
        {
            return earlier;
        }

        if (form.Files.GetFile("file") is not { } file || form.Files.Count != 1)
        {
            return Invalid("file", "Exactly one picture is required.");
        }

        if (file.Length > limits.MaxUploadBytes)
        {
            return TooLarge(limits);
        }

        // Before any decoding: a user at the daily limit costs a count, not a picture (the queue decides for sure)
        var since = clock.UtcNow - FileRules.Day;
        if (await db.Files.CountAsync(f => f.OwnerId == ownerId && f.CreatedAt > since, ct) >= limits.UploadsPerDay)
        {
            return DailyLimit($"At most {limits.UploadsPerDay} uploads in 24 hours.");
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        {
            content = new byte[file.Length];
            await stream.ReadExactlyAsync(content, ct);
        }

        if (!await s_processing.WaitAsync(s_processingWait, ct))
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "The server is busy with other pictures.", "Try again in a minute.", "file.busy");
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
            return rejection!.Code == ImageProcessor.TooLarge
                ? TooLarge(limits)
                : Problem(StatusCodes.Status422UnprocessableEntity, "The picture was refused.", rejection.Detail, rejection.Code);
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
                new RecordFile(fileId, ownerId, processed.MediaType, processed.Main.LongLength, processed.Width, processed.Height, processed.Frames, limits.UploadsPerDay),
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
            return await ViewAsync(stored!.Value, outcome.IsDuplicate, db, ct);
        }

        // Two retries at once: the other one was recorded first with a file of its own — answer with that one
        if (outcome.Rejection!.Code == RejectionCodes.CommandIdReused && await EarlierAsync(commandId, ownerId, db, ct) is { } first)
        {
            return first;
        }

        return outcome.Rejection.Code == FileRules.DailyLimit
            ? DailyLimit(outcome.Rejection.Detail)
            : Problem(StatusCodes.Status409Conflict, "The command was rejected.", outcome.Rejection.Detail, outcome.Rejection.Code);
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
