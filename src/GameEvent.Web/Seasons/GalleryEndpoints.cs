using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using GameEvent.Engine.Files;
using GameEvent.Engine.Gallery;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Reactions;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Kernel;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Files;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

public sealed record GalleryUploadForm(Guid CommandId, Guid ItemId, string Caption, IFormFile File);
public sealed record GalleryHideRequest(Guid CommandId);
public sealed record GalleryMomentView(
    Guid Id, Guid PlayerId, string PlayerName, string Caption, DateTimeOffset CreatedAt, string ImageUrl, string ThumbnailUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ReactionView>? Reactions = null);
public sealed record GallerySeasonView(Guid Id, string Name);
public sealed record GalleryPlayerView(Guid Id, string Name, int Token);
public sealed record GalleryView(
    IReadOnlyList<GalleryMomentView> Items, Guid? NextBefore, IReadOnlyList<GalleryPlayerView> Players, bool CanPublish);

internal static class GalleryEndpoints
{
    private const int PageSize = 50;
    private const string SeasonPrefix = "/api/seasons/";
    private const string GallerySuffix = "/gallery";

    internal static bool IsUpload(string path) =>
        path.Length > SeasonPrefix.Length + GallerySuffix.Length
        && path.StartsWith(SeasonPrefix, StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(GallerySuffix, StringComparison.OrdinalIgnoreCase)
        && Guid.TryParse(path.AsSpan(SeasonPrefix.Length, path.Length - SeasonPrefix.Length - GallerySuffix.Length), out _);

    public static void MapGallery(this RouteGroupBuilder api)
    {
        api.MapGet("/seasons/gallery", ListSeasonsAsync)
            .WithTags("Seasons")
            .RequireAuthorization()
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit);
        var gallery = api.MapGroup("/seasons/{seasonId:guid}/gallery").WithTags("Seasons").RequireAuthorization();
        gallery.MapGet("", ListAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .WithActionErrors();
        gallery.MapPost("", PublishAsync)
            .DisableAntiforgery()
            .RequireAuthorization(Policies.Player)
            .RequireRateLimiting(FileEndpoints.UploadRateLimit)
            .Accepts<GalleryUploadForm>("multipart/form-data")
            .Produces<GalleryMomentView>(StatusCodes.Status201Created)
            .Produces<GalleryMomentView>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithActionErrors();
        gallery.MapGet("/{itemId:guid}/image", (Guid seasonId, Guid itemId, GameEventDbContext db, FileStorage storage, HttpContext http, CancellationToken ct) =>
                ReadPictureAsync(seasonId, itemId, thumbnail: false, db, storage, http, ct))
            .RequireRateLimiting(AppSetup.GalleryMediaRateLimit)
            .Produces<byte[]>(StatusCodes.Status200OK, FileNames.Webp)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);
        gallery.MapGet("/{itemId:guid}/thumbnail", (Guid seasonId, Guid itemId, GameEventDbContext db, FileStorage storage, HttpContext http, CancellationToken ct) =>
                ReadPictureAsync(seasonId, itemId, thumbnail: true, db, storage, http, ct))
            .RequireRateLimiting(AppSetup.GalleryMediaRateLimit)
            .Produces<byte[]>(StatusCodes.Status200OK, FileNames.Webp)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);

        gallery.MapPut("/{itemId:guid}/reactions", SetReactionAsync)
            .RequireRateLimiting(AppSetup.ReviewReactionRateLimit)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapPost("/admin/seasons/{seasonId:guid}/gallery/{itemId:guid}/hide", HideAsync)
            .WithTags("Admin")
            .RequireAuthorization(Policies.Admin)
            .RequireRateLimiting(AppSetup.InteractionRateLimit)
            .WithActionErrors();
    }

    private static async Task<Ok<IReadOnlyList<GallerySeasonView>>> ListSeasonsAsync(
        GameEventDbContext db, HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        var seasons = await db.Seasons.AsNoTracking().OrderByDescending(season => season.CreatedAt)
            .Select(season => new { season.Id, season.Name, season.RulesetJson }).ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<GallerySeasonView>>([
            .. seasons.Where(season => JsonSerializer.Deserialize<GalleryRulesFlags>(
                    season.RulesetJson, EngineJson.Options)?.Features?.Gallery == true)
                .Select(season => new GallerySeasonView(season.Id, season.Name)),
        ]);
    }

    private sealed record GalleryRulesFlags(GalleryFeatures? Features = null);
    private sealed record GalleryFeatures(bool Gallery = false);

    private static async Task<Results<Ok<GalleryView>, NotFound, ProblemHttpResult>> ListAsync(
        Guid seasonId, Guid? playerId, Guid? before, Guid? itemId, ClaimsPrincipal user, HttpResponse response, GameEventDbContext db, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        if (await SeasonAsync(seasonId, db, ct) is not { } season)
        {
            return TypedResults.NotFound();
        }

        if (!season.Rules.Features.Gallery)
        {
            return Disabled();
        }

        var moments = db.GalleryMoments.AsNoTracking()
            .Where(item => item.SeasonId == seasonId && (playerId == null || item.PlayerId == playerId)
                && (itemId == null || item.ItemId == itemId));
        var visible = moments.Where(item => !item.Hidden);
        if (before is { } cursor)
        {
            var anchor = await moments.Where(item => item.ItemId == cursor)
                .Select(item => new { item.CreatedAt, item.ItemId }).SingleOrDefaultAsync(ct);
            if (anchor is null)
            {
                return TypedResults.NotFound();
            }

            visible = visible.Where(item => item.CreatedAt < anchor.CreatedAt
                || (item.CreatedAt == anchor.CreatedAt && item.ItemId.CompareTo(anchor.ItemId) < 0));
        }

        var rows = await visible.Join(db.SeasonPlayers.AsNoTracking(), item => item.PlayerId, player => player.Id,
                (item, player) => new { item, player.Name })
            .OrderByDescending(row => row.item.CreatedAt).ThenByDescending(row => row.item.ItemId)
            .Take(PageSize + 1).ToListAsync(ct);
        var nextBefore = rows.Count > PageSize ? (Guid?)rows[PageSize - 1].item.ItemId : null;
        var page = rows.Take(PageSize).ToArray();
        var reactions = season.Rules.Features.Reactions && page.Length > 0
            ? await ReadReactionsAsync(db, seasonId, page.Select(row => row.item.ItemId).ToArray(), season.Rules.Social.Reactions, user.UserId(), ct)
            : null;
        var players = await db.SeasonPlayers.AsNoTracking().Where(player => player.SeasonId == seasonId)
            .Select(player => new { player.Id, player.Name, player.UserId, player.IsInactive }).ToListAsync(ct);
        var actor = user.UserId();
        var canPublish = season.Status is SeasonStatus.Active or SeasonStatus.Closing
            && user.IsInRole(nameof(Role.Player))
            && players.Any(player => player.UserId == actor && !player.IsInactive);
        return TypedResults.Ok(new GalleryView(
            [.. page.Select(row => View(seasonId, row.item.ItemId, row.item.PlayerId, row.Name, row.item.Caption, row.item.CreatedAt,
                reactions?.GetValueOrDefault(row.item.ItemId)))],
            nextBefore,
            [.. players.InTokenOrder(player => player.Name, player => player.Id)
                .Select((player, token) => new GalleryPlayerView(player.Id, player.Name, token))],
            canPublish));
    }

    private static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult, ValidationProblem, ProblemHttpResult>> SetReactionAsync(
        Guid seasonId, Guid itemId, ReactionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (user.UserId() is not { } viewer)
        {
            return TypedResults.Unauthorized();
        }

        if (request.Invalid() is { } invalid)
        {
            return invalid;
        }

        var command = new SetGalleryReaction(itemId, viewer, request.Emoji!, request.Selected.GetValueOrDefault());
        var sent = await SeasonEndpoints.PrecheckAndSendAsync(seasonId, request.CommandId, command, viewer, db, bus, ct);
        return sent.Result switch
        {
            Ok<CommandResponse> => TypedResults.NoContent(),
            ProblemHttpResult problem when problem.ProblemDetails.Extensions.TryGetValue("code", out var code)
                && Equals(code, GalleryRejectionCodes.TargetUnknown) => TypedResults.NotFound(),
            ProblemHttpResult problem => problem,
            NotFound missing => missing,
            ValidationProblem validation => validation,
            _ => throw new InvalidOperationException("An unexpected result came from the gallery reaction command."),
        };
    }

    private static async Task<Dictionary<Guid, ReactionView[]>> ReadReactionsAsync(
        GameEventDbContext db, Guid seasonId, Guid[] itemIds, EquatableArray<string> emojis, Guid? viewer, CancellationToken ct)
    {
        var counts = (await db.GalleryReactions.AsNoTracking().Where(reaction => reaction.SeasonId == seasonId && itemIds.Contains(reaction.ItemId))
            .GroupBy(reaction => new { reaction.ItemId, reaction.Emoji })
            .Select(group => new GalleryReactionCount(group.Key.ItemId, group.Key.Emoji, group.Count(), group.Count(reaction => reaction.UserId == viewer) > 0))
            .ToListAsync(ct)).ToDictionary(count => (count.ItemId, count.Emoji));
        return itemIds.ToDictionary(id => id, id => emojis.Select(emoji =>
        {
            var count = counts.GetValueOrDefault((id, emoji));
            return new ReactionView(emoji, count?.Total ?? 0, count?.Selected ?? false);
        }).ToArray());
    }

    private sealed record GalleryReactionCount(Guid ItemId, string Emoji, int Total, bool Selected);

    private static async Task<IResult> PublishAsync(
        Guid seasonId, HttpRequest request, ClaimsPrincipal user, [AsParameters] FileServices services, CancellationToken ct)
    {
        if (user.UserId() is not { } actor)
        {
            return TypedResults.Forbid();
        }

        if (await SeasonAsync(seasonId, services.Db, ct) is not { } season)
        {
            return TypedResults.NotFound();
        }

        if (!season.Rules.Features.Gallery)
        {
            return Disabled();
        }

        var player = await services.Db.SeasonPlayers.AsNoTracking()
            .Where(p => p.SeasonId == seasonId && p.UserId == actor && !p.IsInactive)
            .Select(p => new { p.Name }).SingleOrDefaultAsync(ct);
        if (player is null)
        {
            return TypedResults.Forbid();
        }

        if (!request.HasFormContentType)
        {
            return SeasonEndpoints.Invalid("file", "Send the picture as multipart/form-data.");
        }

        var form = await request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["commandId"], out var commandId) || commandId == Guid.Empty)
        {
            return SeasonEndpoints.Invalid("commandId", "A command id is required.");
        }

        if (!Guid.TryParse(form["itemId"], out var itemId) || itemId == Guid.Empty)
        {
            return SeasonEndpoints.Invalid("itemId", "A moment id is required.");
        }

        var caption = form["caption"].ToString().Trim();
        if (form["caption"].Count != 1 || caption.Length is 0 or > Limits.MaxCommentLength)
        {
            return SeasonEndpoints.Invalid("caption", $"Supply one caption of up to {Limits.MaxCommentLength} characters.");
        }

        if (form.Files.GetFile("file") is not { } file || form.Files.Count != 1)
        {
            return SeasonEndpoints.Invalid("file", "Exactly one picture is required.");
        }

        if (file.Length > services.Limits.MaxUploadBytes)
        {
            return FileEndpoints.TooLarge(services.Limits);
        }

        // Read the debit first so a just-committed retry is still checked as a retry before an early quota refusal.
        var limited = season.Status is SeasonStatus.Active or SeasonStatus.Closing
            && await AtDailyLimitAsync(season.Rules, seasonId, actor, services, ct);
        var earlier = await services.Db.Events.AsNoTracking().AnyAsync(row => row.CommandId == commandId, ct);
        if (!earlier)
        {
            if (season.Status is SeasonStatus.Finished or SeasonStatus.Archived)
            {
                return Problem(StatusCodes.Status409Conflict, RejectionCodes.SeasonClosed, "The season is closed to new gallery moments.");
            }

            if (season.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
            {
                return Problem(StatusCodes.Status409Conflict, RejectionCodes.SeasonNotActive, "The season has not started.");
            }

            // The projected debit may have been read after a rules update. The serialized command uses the new rules.
            if (limited && await services.Db.Seasons.AsNoTracking().AnyAsync(s => s.Id == seasonId
                && s.Status == season.Status && s.RulesetVersion == season.RulesetVersion, ct))
            {
                return DailyLimit();
            }
        }

        string fingerprint;
        await using (var stream = file.OpenReadStream())
        {
            fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        }

        var command = new PublishGalleryMoment(itemId, actor, caption, fingerprint);
        if (earlier)
        {
            // The queue verifies the author, command fingerprint and undo status against the committed log.
            var envelope = new CommandEnvelope(commandId, seasonId, command, actor);
            return PublicationResult(seasonId, player.Name, await services.Bus.SendAsync(envelope, ct));
        }

        var (picture, failure) = await FileEndpoints.ProcessPictureAsync(file, FileKind.GalleryImage, services.Limits, ct);
        if (picture is null)
        {
            return failure!;
        }

        var outcome = await StoreAndPublishAsync(commandId, seasonId, command, picture, services);
        return PublicationResult(seasonId, player.Name, outcome);
    }

    internal static async Task<CommandOutcome> StoreAndPublishAsync(
        Guid commandId, Guid seasonId, PublishGalleryMoment command, ProcessedFile picture, FileServices services)
    {
        var fileId = services.Ids.NewId();
        var main = FileNames.Main(fileId, picture.MediaType);
        var thumbnail = FileNames.Thumbnail(fileId, picture.MediaType);
        await services.Storage.WriteAsync(main, picture.Main, CancellationToken.None);
        try
        {
            await services.Storage.WriteAsync(thumbnail, picture.Thumbnail, CancellationToken.None);
        }
        catch
        {
            services.Storage.Delete(main);
            throw;
        }

        // Once queued, the command must be able to finish even if the request is gone (D-108).
        var prepared = new PreparedGalleryFile(
            fileId, picture.MediaType, picture.Main.LongLength, picture.Width, picture.Height, picture.Frames, command.ImageSha256);
        var envelope = new CommandEnvelope(commandId, seasonId, command, command.UserId) { PreparedGalleryFile = prepared };
        CommandOutcome outcome;
        try
        {
            outcome = await services.Bus.SendAsync(envelope, CancellationToken.None);
        }
        catch (ChannelClosedException)
        {
            services.Storage.Delete(main);
            services.Storage.Delete(thumbnail);
            throw;
        }

        if (Published(outcome)?.FileId != fileId)
        {
            services.Storage.Delete(main);
            services.Storage.Delete(thumbnail);
        }

        return outcome;
    }

    private static async Task<bool> AtDailyLimitAsync(Ruleset rules, Guid seasonId, Guid actor, FileServices services, CancellationToken ct)
    {
        var limit = rules.Social.GalleryUploadsPerDay;
        if (limit == 0)
        {
            return true;
        }

        var now = services.Clock.UtcNow;
        var zone = rules.Season.Timezone;
        var zones = SystemTimeZones.Instance;
        if (SeasonCalendar.Day(zones, zone, now) is not { } day)
        {
            return false;
        }

        // A conservative UTC range bounds the query. The season calendar decides the exact day, including offset changes.
        var since = now.AddDays(-2);
        var until = now.AddDays(2);
        var recent = await services.Db.GalleryMoments.AsNoTracking()
            .Where(item => item.SeasonId == seasonId && item.UserId == actor && item.CreatedAt >= since && item.CreatedAt <= until)
            .Select(item => item.CreatedAt).ToListAsync(ct);
        return recent.Count(created => SeasonCalendar.Day(zones, zone, created) == day) >= limit;
    }

    private static GalleryMomentPublished? Published(CommandOutcome outcome) =>
        outcome.Events.Select(record => record.Event).OfType<GalleryMomentPublished>().SingleOrDefault();

    private static IResult PublicationResult(Guid seasonId, string playerName, CommandOutcome outcome)
    {
        if (!outcome.IsAccepted)
        {
            var rejection = outcome.Rejection!;
            return rejection.Code switch
            {
                GalleryRejectionCodes.NotParticipant => TypedResults.Forbid(),
                GalleryRejectionCodes.DailyLimit => DailyLimit(),
                _ => Problem(StatusCodes.Status409Conflict, rejection.Code, rejection.Detail),
            };
        }

        var recorded = Published(outcome) ?? throw new InvalidOperationException("A successful gallery publication must record the moment.");
        var view = View(seasonId, recorded.ItemId, recorded.PlayerId, playerName, recorded.Caption, recorded.CreatedAt);
        return outcome.IsDuplicate ? TypedResults.Ok(view) : TypedResults.Created(view.ImageUrl, view);
    }

    private static async Task<IResult> ReadPictureAsync(
        Guid seasonId, Guid itemId, bool thumbnail, GameEventDbContext db, FileStorage storage, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        if ((await SeasonAsync(seasonId, db, ct))?.Rules.Features.Gallery != true)
        {
            return TypedResults.NotFound();
        }

        var file = await (
            from item in db.GalleryMoments.AsNoTracking()
            join binding in db.GalleryMediaBindings.AsNoTracking() on
                new { item.FileId, item.SeasonId, item.ItemId } equals new { binding.FileId, binding.SeasonId, binding.ItemId }
            join record in db.Files.AsNoTracking() on binding.FileId equals record.Id
            where item.SeasonId == seasonId && item.ItemId == itemId && !item.Hidden
                && record.Kind == FileKind.GalleryImage && !record.IsDeleted && record.OwnerId == item.UserId
            select new { record.Id, record.MediaType }).SingleOrDefaultAsync(ct);
        return file is null
            ? TypedResults.NotFound()
            : FileEndpoints.ServePicture(file.Id, file.MediaType, thumbnail, storage, http, immutable: false);
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult, NotFound, ValidationProblem, ProblemHttpResult>> HideAsync(
        Guid seasonId, Guid itemId, GalleryHideRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (user.UserId() is not { } actor)
        {
            return TypedResults.Unauthorized();
        }

        var sent = await SeasonEndpoints.PrecheckAndSendAsync(seasonId, request.CommandId, new HideGalleryMoment(itemId), actor, db, bus, ct);
        return sent.Result switch
        {
            Ok<CommandResponse> => TypedResults.NoContent(),
            ProblemHttpResult problem when problem.ProblemDetails.Extensions.TryGetValue("code", out var code)
                && Equals(code, GalleryRejectionCodes.TargetUnknown) => TypedResults.NotFound(),
            ProblemHttpResult problem => problem,
            NotFound missing => missing,
            ValidationProblem invalid => invalid,
            _ => throw new InvalidOperationException("An unexpected result came from gallery moderation."),
        };
    }

    private static async Task<GallerySeason?> SeasonAsync(Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        var season = await db.Seasons.AsNoTracking().Where(s => s.Id == seasonId)
            .Select(s => new { s.Status, s.RulesetVersion, s.RulesetJson }).SingleOrDefaultAsync(ct);
        return season is not null && JsonSerializer.Deserialize<Ruleset>(season.RulesetJson, EngineJson.Options) is { } rules
            ? new GallerySeason(season.Status, season.RulesetVersion, rules)
            : null;
    }

    private sealed record GallerySeason(SeasonStatus Status, int RulesetVersion, Ruleset Rules);

    private static GalleryMomentView View(Guid seasonId, Guid id, Guid playerId, string name, string caption, DateTimeOffset createdAt,
        IReadOnlyList<ReactionView>? reactions = null)
    {
        var url = $"/api/seasons/{seasonId}/gallery/{id}";
        return new GalleryMomentView(id, playerId, name, caption, createdAt, $"{url}/image", $"{url}/thumbnail", reactions);
    }

    private static ProblemHttpResult Disabled() =>
        Problem(StatusCodes.Status409Conflict, RejectionCodes.FeatureDisabled, "The gallery is disabled for this season.");

    private static ProblemHttpResult DailyLimit() =>
        Problem(StatusCodes.Status429TooManyRequests, GalleryRejectionCodes.DailyLimit, "This account has reached the daily gallery upload limit.");

    private static ProblemHttpResult Problem(int status, string code, string detail) =>
        TypedResults.Problem(statusCode: status, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
}
