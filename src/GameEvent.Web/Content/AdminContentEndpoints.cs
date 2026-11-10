using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Content;

/// <summary>A definition that an earlier pack had and the one in force does not (soft deletion, invariant 11).</summary>
public sealed record DeletedObjectView(string Id, ObjectKind Kind, string Name, int DeletedIn);

/// <summary>
/// One published version of the season's content: who published it, when and why, its size, and which objects and
/// wheels it added, changed or removed against the version before it.
/// </summary>
public sealed record ContentVersionInfo(
    int Version,
    DateTimeOffset At,
    string? AuthorName,
    string Comment,
    int Objects,
    int Wheels,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Removed);

/// <summary>
/// The season's content in the editor (4.12, D-490): whether items are on (the editor works either way, a pack is
/// published only with <c>features.items</c>), the season's status, the version in force, the pack in force as content
/// JSON (CONTENT.md; null fields left out), the definitions deleted on the way and the versions, newest first.
/// </summary>
public sealed record AdminContentView(
    bool ItemsEnabled,
    SeasonStatus Status,
    int Version,
    JsonElement Pack,
    IReadOnlyList<DeletedObjectView> Deleted,
    IReadOnlyList<ContentVersionInfo> History);

/// <summary>One version's pack, for the history and the export of an older version.</summary>
public sealed record ContentVersionView(int Version, JsonElement Pack);

/// <summary>
/// A problem of a pack: the JSON path of its field (<c>objects[2].effect.actions[0].steps</c>, <c>pack</c> for the
/// whole) and the engine's English message, shown as it is next to the field (D-181, D-492).
/// </summary>
public sealed record ContentProblemView(string Path, string Message);

/// <summary>The check of a pack: every problem at once, the types' and the content rules'.</summary>
public sealed record ContentCheckView(bool Valid, IReadOnlyList<ContentProblemView> Problems);

/// <summary>An invalid pack refused at publication, with the same problems as the check.</summary>
public sealed record ContentProblem(string Title, int Status, IReadOnlyList<ContentProblemView> Errors);

/// <summary>
/// Publish a new version of the season's content (D-400) made from <c>expectedVersion</c> (D-491); <c>comment</c> goes
/// to the public log, at most 500 characters.
/// </summary>
public sealed record PublishContentRequest(Guid CommandId, int ExpectedVersion, JsonElement Pack, string Comment);

/// <summary>
/// The content editor's API (4.12): read the content in force with its versions, one older version, the schema, check a
/// pack and publish it through the queue (a command, invariant 2). Admin only.
/// </summary>
public static class AdminContentEndpoints
{
    /// <summary>
    /// A pack is bigger than the API's small JSON: the imported pack of the old season is about 300 KB. Past this size the
    /// request is refused before it is read.
    /// </summary>
    public const long MaxContentRequestBytes = 1024 * 1024;

    /// <summary>At most this many object definitions in one pack (the old season had under 400).</summary>
    public const int MaxObjects = 1000;

    /// <summary>At most this many wheels in one pack.</summary>
    public const int MaxWheels = 100;

    private const string Published = "content-published";

    private static readonly Lazy<string> s_schema = new(ContentSchema.Generate);

    /// <summary>Whether the request carries a whole pack: the check and the publication.</summary>
    public static bool IsContentBody(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return HttpMethods.IsPost(request.Method)
            && request.Path.StartsWithSegments("/api/admin/seasons")
            && (request.Path.Value?.EndsWith("/content/check", StringComparison.Ordinal) == true
                || request.Path.Value?.EndsWith("/content/publish", StringComparison.Ordinal) == true);
    }

    public static void MapAdminContent(this RouteGroupBuilder api)
    {
        // The JSON schema of a pack (docs/content.schema.json) for the editor to check against as the admin types
        api.MapGet("/admin/content/schema", () => TypedResults.Text(s_schema.Value, "application/schema+json"))
            .WithTags("Admin")
            .RequireAuthorization(Policies.Admin)
            .Produces<JsonObject>(StatusCodes.Status200OK, "application/schema+json")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var content = api.MapGroup("/admin/seasons/{seasonId:guid}/content").WithTags("Admin").RequireAuthorization(Policies.Admin);

        content.MapGet("/", GetAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        content.MapGet("/versions/{version:int}", GetVersionAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // The body is read by hand (strict parse with field paths); these document its shape for the client.
        content.MapPost("/check", CheckAsync)
            .RequireRateLimiting(AppSetup.ContentCheckRateLimit)
            .Accepts<JsonObject>("application/json")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        content.MapPost("/publish", PublishAsync)
            .Accepts<PublishContentRequest>("application/json")
            .Produces<ContentProblem>(StatusCodes.Status400BadRequest, "application/problem+json")
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .WithActionErrors();
    }

    private static async Task<Results<Ok<AdminContentView>, NotFound>> GetAsync(Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        var season = await db.Seasons.AsNoTracking().SingleOrDefaultAsync(s => s.Id == seasonId, ct);
        if (season is null)
        {
            return TypedResults.NotFound();
        }

        var rules = JsonSerializer.Deserialize<Ruleset>(season.RulesetJson, EngineJson.Options)!;
        var versions = await VersionsAsync(db, seasonId, ct);
        var authors = await db.NamesAsync(versions.Select(v => v.AuthorId).OfType<Guid>(), ct);

        var history = new List<ContentVersionInfo>();
        var deleted = new Dictionary<string, DeletedObjectView>(StringComparer.Ordinal);
        ContentPack previous = new();
        foreach (var v in versions)
        {
            var pack = v.Published.Pack;
            var before = previous.Objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
            var now = pack.Objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
            var wheelsBefore = previous.Wheels.ToDictionary(w => w.Id, StringComparer.Ordinal);
            var wheelsNow = pack.Wheels.ToDictionary(w => w.Id, StringComparer.Ordinal);
            List<string> added = [.. now.Keys.Where(id => !before.ContainsKey(id)), .. wheelsNow.Keys.Where(id => !wheelsBefore.ContainsKey(id))];
            List<string> changed = [
                .. now.Where(p => before.TryGetValue(p.Key, out var old) && old != p.Value).Select(p => p.Key),
                .. wheelsNow.Where(p => wheelsBefore.TryGetValue(p.Key, out var old) && old != p.Value).Select(p => p.Key)];
            List<string> removed = [.. before.Keys.Where(id => !now.ContainsKey(id)), .. wheelsBefore.Keys.Where(id => !wheelsNow.ContainsKey(id))];
            foreach (var id in before.Keys.Where(id => !now.ContainsKey(id)))
            {
                deleted[id] = new DeletedObjectView(id, before[id].Kind, before[id].Name, v.Published.Version);
            }

            foreach (var id in now.Keys)
            {
                deleted.Remove(id);
            }

            history.Add(new ContentVersionInfo(
                v.Published.Version,
                v.At,
                v.AuthorId is { } author ? authors.GetValueOrDefault(author) : null,
                v.Published.Comment,
                pack.Objects.Count,
                pack.Wheels.Count,
                added,
                changed,
                removed));
            previous = pack;
        }

        history.Reverse(); // newest first
        return TypedResults.Ok(new AdminContentView(
            rules.Features.Items,
            season.Status,
            versions.Count == 0 ? 0 : versions[^1].Published.Version,
            Compact(previous),
            [.. deleted.Values.OrderBy(d => d.Id, StringComparer.Ordinal)],
            history));
    }

    private static async Task<Results<Ok<ContentVersionView>, NotFound>> GetVersionAsync(
        Guid seasonId, int version, GameEventDbContext db, CancellationToken ct)
    {
        var found = (await VersionsAsync(db, seasonId, ct)).FirstOrDefault(v => v.Published.Version == version);
        return found is null ? TypedResults.NotFound() : TypedResults.Ok(new ContentVersionView(version, Compact(found.Published.Pack)));
    }

    private static async Task<Results<Ok<ContentCheckView>, ValidationProblem, NotFound>> CheckAsync(
        Guid seasonId, HttpRequest http, GameEventDbContext db, CancellationToken ct)
    {
        if (await ReadObjectAsync(http, ct) is not { } body)
        {
            return BodyInvalid("A content pack {objects, wheels} is required.");
        }

        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        var problems = Problems(body, out _);
        return TypedResults.Ok(new ContentCheckView(problems.Count == 0, problems));
    }

    private static async Task<Results<Ok<CommandResponse>, JsonHttpResult<ContentProblem>, ProblemHttpResult, ValidationProblem, NotFound, ForbidHttpResult>> PublishAsync(
        Guid seasonId, HttpRequest http, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        var body = await ReadObjectAsync(http, ct);
        if (body is null
            || body["commandId"]?.GetValueKind() != JsonValueKind.String
            || !Guid.TryParse(body["commandId"]!.GetValue<string>(), out var commandId)
            || body["expectedVersion"]?.GetValueKind() != JsonValueKind.Number
            || !int.TryParse(body["expectedVersion"]!.ToJsonString(), out var expectedVersion)
            || body["pack"] is not JsonObject packJson)
        {
            return BodyInvalid("Expected {commandId, expectedVersion, pack, comment}.");
        }

        var comment = body["comment"]?.GetValueKind() == JsonValueKind.String ? body["comment"]!.GetValue<string>() : null;
        if (string.IsNullOrWhiteSpace(comment) || comment.Length > Limits.MaxCommentLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["comment"] = [$"A comment of at most {Limits.MaxCommentLength} characters is required."],
            });
        }

        if (await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct) is { } refused)
        {
            return refused.Result switch
            {
                ValidationProblem invalid => invalid,
                NotFound notFound => notFound,
                _ => throw new InvalidOperationException("The precheck answers 400 or 404."),
            };
        }

        var problems = Problems(packJson, out var pack);
        if (problems.Count > 0)
        {
            return TypedResults.Json(
                new ContentProblem("The content pack is invalid.", StatusCodes.Status400BadRequest, problems),
                statusCode: StatusCodes.Status400BadRequest,
                contentType: "application/problem+json");
        }

        var sent = await SeasonEndpoints.SendAsync(seasonId, commandId, new PublishContent(pack!, comment.Trim(), expectedVersion), user.UserId(), bus, ct);
        return sent.Result switch
        {
            Ok<CommandResponse> ok => ok,
            ProblemHttpResult problem => problem,
            _ => throw new InvalidOperationException("A sent command answers 200 or 409."),
        };
    }

    /// <summary>The problems of a pack: its types (the first one the strict reader meets), then every content rule.</summary>
    private static List<ContentProblemView> Problems(JsonObject json, out ContentPack? pack)
    {
        try
        {
            pack = ContentJson.Parse<ContentPack>(json.ToJsonString());
        }
        catch (JsonException e)
        {
            pack = null;
            var (path, message) = Locate(e);
            return [new ContentProblemView(path, message)];
        }

        var problems = new List<ContentProblemView>();
        if (pack.Objects.Count > MaxObjects)
        {
            problems.Add(new ContentProblemView("objects", $"At most {MaxObjects} objects in one pack."));
        }

        if (pack.Wheels.Count > MaxWheels)
        {
            problems.Add(new ContentProblemView("wheels", $"At most {MaxWheels} wheels in one pack."));
        }

        if (problems.Count == 0)
        {
            problems.AddRange(ContentPublishing.Check(pack).Select(e => new ContentProblemView(Path(e.Path), e.Message)));
        }

        return problems;
    }

    private static async Task<JsonObject?> ReadObjectAsync(HttpRequest http, CancellationToken ct)
    {
        try
        {
            return await JsonNode.ParseAsync(http.Body, cancellationToken: ct) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ValidationProblem BodyInvalid(string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["body"] = [message] });

    /// <summary>
    /// The field a parse error is about and what is wrong with it. A list reports its item's path relative to itself
    /// («$[0].kind» inside «$.objects»), wrapped in the list's error: the paths are joined down the chain, and the
    /// innermost message, without its path, says what is wrong.
    /// </summary>
    private static (string Path, string Message) Locate(JsonException e)
    {
        var path = e.Path ?? "$";
        Exception current = e;
        while (current.InnerException is JsonException inner)
        {
            if (inner.Path is { } relative && relative.StartsWith("$[", StringComparison.Ordinal))
            {
                path += relative[1..];
            }

            current = inner;
        }

        var message = current.Message;
        var at = message.IndexOf(" Path: $", StringComparison.Ordinal);
        message = at >= 0 ? message[..at] : message;
        const string Wrapped = "Invalid list item: ";
        while (message.StartsWith(Wrapped, StringComparison.Ordinal))
        {
            message = message[Wrapped.Length..];
        }

        if (current is JsonException { Path: { } own } && message.StartsWith(own + ": ", StringComparison.Ordinal))
        {
            message = message[(own.Length + 2)..];
        }

        return (Path(path), message);
    }

    // "$.objects[0].name" → "objects[0].name"; no path (the document itself) → "pack".
    private static string Path(string? jsonPath) =>
        jsonPath is null or "$" or "" ? "pack" : jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath.TrimStart('$');

    private sealed record PublishedVersion(ContentPublished Published, DateTimeOffset At, Guid? AuthorId);

    // Found by the events' type, never by their data (invariant 9); a publication is never undone (Undo refuses it)
    private static async Task<List<PublishedVersion>> VersionsAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.Type == Published)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        return [.. rows.Select(r => new PublishedVersion(
            (ContentPublished)EventCodec.Decode(new StoredEvent(r.Type, r.Version, r.Data)), r.OccurredAt, r.AuthorId))];
    }

    /// <summary>The pack as content JSON (CONTENT.md) without the null fields the log writes out (D-48).</summary>
    private static JsonElement Compact(ContentPack pack)
    {
        var node = JsonSerializer.SerializeToNode(pack, ContentJson.Options)!;
        Prune(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Prune(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Where(p => p.Value is null).Select(p => p.Key).ToList())
                {
                    o.Remove(key);
                }

                foreach (var (_, value) in o)
                {
                    Prune(value);
                }

                break;
            case JsonArray a:
                foreach (var item in a)
                {
                    Prune(item);
                }

                break;
            default:
                break;
        }
    }
}
