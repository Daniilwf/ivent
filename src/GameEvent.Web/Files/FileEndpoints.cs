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
    public const int UploadsPerMinute = 20;

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
                _ => new FixedWindowRateLimiterOptions { PermitLimit = UploadsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
    }

    /// <summary>The body limit of an upload request (the rest of the API stays at <see cref="WebSecurity.ApiBodyLimitBytes"/>).</summary>
    public static bool IsUpload(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && request.Path.Equals("/api/files", StringComparison.OrdinalIgnoreCase);

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
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesValidationProblem();

        files.MapGet("/{fileId:guid}", (Guid fileId, GameEventDbContext db, FileStorage storage, HttpContext http, CancellationToken ct) =>
                ServeAsync(fileId, thumbnail: false, db, storage, http, ct))
            .Produces(StatusCodes.Status200OK, contentType: FileNames.Webp, additionalContentTypes: [FileNames.Gif])
            .Produces(StatusCodes.Status404NotFound);

        files.MapGet("/{fileId:guid}/thumbnail", (Guid fileId, GameEventDbContext db, FileStorage storage, HttpContext http, CancellationToken ct) =>
                ServeAsync(fileId, thumbnail: true, db, storage, http, ct))
            .Produces(StatusCodes.Status200OK, contentType: FileNames.Webp, additionalContentTypes: [FileNames.Gif])
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request, ClaimsPrincipal principal, GameEventDbContext db, FileStorage storage, FileLimits limits, IIdGenerator ids, CommandBus bus, CancellationToken ct)
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
        if (await db.Events.AsNoTracking().Where(e => e.SeasonId == Guid.Empty && e.CommandId == commandId).ToListAsync(ct) is { Count: > 0 } earlier)
        {
            return earlier.Count == 1 && earlier[0].AuthorId == ownerId
                && EventCodec.Decode(new StoredEvent(earlier[0].Type, earlier[0].Version, earlier[0].Data)) is FileStored stored
                ? await ViewAsync(stored.FileId, duplicate: true, db, ct)
                : Rejected(RejectionCodes.CommandIdReused, $"Command id {commandId} was used by another command.");
        }

        if (form.Files.GetFile("file") is not { } file || form.Files.Count != 1)
        {
            return Invalid("file", "Exactly one picture is required.");
        }

        if (file.Length > limits.MaxUploadBytes)
        {
            return TooLarge(limits);
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        {
            content = new byte[file.Length];
            await stream.ReadExactlyAsync(content, ct);
        }

        var (processed, rejection) = await Task.Run(() => ImageProcessor.Process(content, limits), ct);
        if (processed is null)
        {
            return rejection!.Code == ImageProcessor.TooLarge
                ? TooLarge(limits)
                : TypedResults.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "The picture was refused.",
                    detail: rejection.Detail,
                    extensions: new Dictionary<string, object?> { ["code"] = rejection.Code });
        }

        // The bytes go to disk first; the queue then records whose they are. A refused or failed record takes them back.
        var fileId = ids.NewId();
        var names = new[] { FileNames.Main(fileId, processed.MediaType), FileNames.Thumbnail(fileId, processed.MediaType) };
        var recorded = false;
        try
        {
            await storage.WriteAsync(names[0], processed.Main, ct);
            await storage.WriteAsync(names[1], processed.Thumbnail, ct);
            var outcome = await bus.SendAsync(
                new CommandEnvelope(
                    commandId,
                    Guid.Empty,
                    new RecordFile(fileId, ownerId, processed.MediaType, processed.Main.LongLength, processed.Width, processed.Height, processed.Frames, limits.UploadsPerDay),
                    ownerId),
                ct);
            if (!outcome.IsAccepted)
            {
                return outcome.Rejection!.Code == FileRules.DailyLimit
                    ? TypedResults.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many uploads today.",
                        detail: outcome.Rejection.Detail,
                        extensions: new Dictionary<string, object?> { ["code"] = FileRules.DailyLimit })
                    : Rejected(outcome.Rejection.Code, outcome.Rejection.Detail);
            }

            // Two retries at once: the queue kept the first one's file
            var stored = outcome.Events.Select(e => e.Event).OfType<FileStored>().Single().FileId;
            recorded = stored == fileId;
            return await ViewAsync(stored, outcome.IsDuplicate, db, ct);
        }
        finally
        {
            if (!recorded)
            {
                foreach (var name in names)
                {
                    storage.Delete(name);
                }
            }
        }
    }

    private static async Task<IResult> ViewAsync(Guid fileId, bool duplicate, GameEventDbContext db, CancellationToken ct)
    {
        var record = await db.Files.AsNoTracking().SingleAsync(f => f.Id == fileId, ct);
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
        headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox";
        headers.CacheControl = "private, max-age=31536000, immutable";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        return TypedResults.File(stream, record.MediaType, fileDownloadName: null, enableRangeProcessing: false);
    }

    private static ProblemHttpResult TooLarge(FileLimits limits) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "The file is too large.",
            detail: $"A picture is at most {limits.MaxUploadBytes / 1024 / 1024} MB, a GIF at most {limits.MaxGifBytes / 1024 / 1024} MB.",
            extensions: new Dictionary<string, object?> { ["code"] = ImageProcessor.TooLarge });

    private static ProblemHttpResult Rejected(string code, string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The command was rejected.",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
