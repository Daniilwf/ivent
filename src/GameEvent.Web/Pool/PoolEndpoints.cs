using System.Security.Claims;
using GameEvent.Engine.Pool;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Files;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Pool;

/// <summary>
/// A game of the pool as everyone sees it (D-119); <c>author</c> — the name of the account that added it, or the author as
/// the imported table names them (D-125); none for the seed.
/// </summary>
public sealed record PoolGameView(
    Guid Id,
    string Title,
    IReadOnlyList<string> Tags,
    decimal? Hours,
    int? Year,
    string? SteamAppId,
    FileLinkView? Cover,
    string? Note,
    bool IsCoop,
    string? Author,
    bool IsDeleted,
    string? CompletionCondition);

/// <summary>A category of the wheel with its weight and how many games in the pool carry its tag.</summary>
public sealed record CategoryView(string Name, int Weight, int Games);

/// <summary>A game in the pool whose title is the same or alike (SPEC «Дубли»).</summary>
public sealed record SimilarGameView(Guid Id, string Title, bool Same);

/// <summary>
/// A game card from a player or the admin (D-119). <c>force</c> — add although the pool has an alike title (the site showed
/// it); the same title is never added twice. <c>coverFileId</c> — the author's own upload or a found cover (D-118).
/// </summary>
public sealed record GameRequest(
    Guid CommandId,
    string? Title,
    IReadOnlyList<string?>? Tags,
    decimal? Hours = null,
    int? Year = null,
    string? SteamAppId = null,
    Guid? CoverFileId = null,
    string? Note = null,
    bool IsCoop = false,
    bool Force = false,
    string? CompletionCondition = null);

public sealed record PoolActionRequest(Guid CommandId);

public sealed record CategoryRequest(Guid CommandId, int Weight);

/// <summary>
/// The shared pool (SPEC «Пул игр», «Дубли», «Удаление мягкое»; D-119): everyone signed in reads it; players and the admin
/// add games; the admin changes, deletes and restores them and keeps the category wheel. Every change goes through the queue.
/// </summary>
public static class PoolEndpoints
{
    public const string PlayerOrAdmin = "player-or-admin";

    /// <summary>Games a user adds in a minute: each one holds the queue for the duplicate check.</summary>
    public const string AddRateLimit = "pool-add";
    public const int AddsPerMinute = 10;

    public static void AddPool(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(PlayerOrAdmin, p => p.RequireRole(nameof(Role.Player), nameof(Role.Admin)));
        builder.Services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(o =>
            o.AddPolicy(AddRateLimit, ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.UserId()?.ToString() ?? WebSecurity.ClientKey(ctx.Connection.RemoteIpAddress),
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = AddsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
    }

    public static void MapPool(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var pool = api.MapGroup("/pool").WithTags("Pool").RequireAuthorization();

        pool.MapGet("", ListAsync);
        pool.MapGet("/categories", CategoriesAsync);
        pool.MapGet("/similar", SimilarAsync).ProducesValidationProblem();
        pool.MapGet("/{gameId:guid}", GetAsync).Produces(StatusCodes.Status404NotFound);

        pool.MapPost("", (GameRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SendCardAsync(request, user, db, bus, card => new AddGame(card, user.UserId(), request.Force), null, ct))
            .RequireAuthorization(PlayerOrAdmin)
            .RequireRateLimiting(AddRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithPoolErrors();

        var admin = api.MapGroup("/admin/pool").WithTags("Admin").RequireAuthorization(Policies.Admin);
        admin.MapPut("/{gameId:guid}", (Guid gameId, GameRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SendCardAsync(request, user, db, bus, card => new ChangeGame(gameId, card, request.Force), gameId, ct))
            .WithPoolErrors();
        admin.MapPost("/{gameId:guid}/delete", (Guid gameId, PoolActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SendAsync(request.CommandId, new DeleteGame(gameId), gameId, user, db, bus, ct))
            .WithPoolErrors();
        admin.MapPost("/{gameId:guid}/restore", (Guid gameId, PoolActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SendAsync(request.CommandId, new RestoreGame(gameId), gameId, user, db, bus, ct))
            .WithPoolErrors();
        admin.MapPut("/categories/{name}", (string name, CategoryRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SendCategoryAsync(request.CommandId, new SetCategory(name, request.Weight), user, db, bus, ct))
            .WithCategoryErrors();
        admin.MapPost("/categories/{name}/remove", (string name, PoolActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SendCategoryAsync(request.CommandId, new RemoveCategory(name), user, db, bus, ct))
            .WithCategoryErrors();
    }

    /// <summary>The pool, by title words and a tag; deleted games only for the admin who asks for them.</summary>
    private static async Task<Ok<IReadOnlyList<PoolGameView>>> ListAsync(
        ClaimsPrincipal user, GameEventDbContext db, CancellationToken ct, string? query = null, string? tag = null, bool deleted = false)
    {
        var withDeleted = deleted && user.IsInRole(nameof(Role.Admin));
        var games = await db.Games.AsNoTracking().Where(g => withDeleted || !g.IsDeleted).OrderBy(g => g.Title).ToListAsync(ct);
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matching = games.Where(g =>
                words.All(w => g.Title.Contains(w, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(tag) || PoolReader.Tags(g.TagsJson).Any(t => string.Equals(t, tag.Trim(), StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var authors = await AuthorsAsync(db, matching, ct);
        IReadOnlyList<PoolGameView> views = [.. matching.Select(g => View(g, authors))];
        return TypedResults.Ok(views);
    }

    private static async Task<Results<Ok<PoolGameView>, NotFound>> GetAsync(Guid gameId, ClaimsPrincipal user, GameEventDbContext db, CancellationToken ct)
    {
        // Like the list: a deleted game is the admin's to see
        if (await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameId, ct) is not { } game || (game.IsDeleted && !user.IsInRole(nameof(Role.Admin))))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(View(game, await AuthorsAsync(db, [game], ct)));
    }

    private static async Task<Ok<IReadOnlyList<CategoryView>>> CategoriesAsync(GameEventDbContext db, CancellationToken ct)
    {
        var categories = await db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var tags = (await db.Games.AsNoTracking().Where(g => !g.IsDeleted).Select(g => g.TagsJson).ToListAsync(ct))
            .SelectMany(json => PoolReader.Tags(json))
            .ToList();
        IReadOnlyList<CategoryView> views = [.. categories.Select(c => new CategoryView(c.Name, c.Weight, tags.Count(t => string.Equals(t, c.Name, StringComparison.OrdinalIgnoreCase))))];
        return TypedResults.Ok(views);
    }

    /// <summary>What the pool already has under this title or an alike one, to warn before adding (SPEC «Дубли»).</summary>
    private static async Task<Results<Ok<IReadOnlyList<SimilarGameView>>, ValidationProblem>> SimilarAsync(string? title, GameEventDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > PoolRules.MaxTitleLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [$"A title is 1–{PoolRules.MaxTitleLength} characters."] });
        }

        var games = await db.Games.AsNoTracking().Where(g => !g.IsDeleted).Select(g => new { g.Id, g.Title }).ToListAsync(ct);
        IReadOnlyList<SimilarGameView> similar =
        [
            .. games.Where(g => PoolRules.IsSame(g.Title, title) || PoolRules.IsAlike(g.Title, title))
                .OrderBy(g => g.Title)
                .Select(g => new SimilarGameView(g.Id, g.Title, PoolRules.IsSame(g.Title, title))),
        ];
        return TypedResults.Ok(similar);
    }

    private static async Task<IResult> SendCardAsync(
        GameRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, Func<GameCard, IGlobalCommand> command, Guid? gameId, CancellationToken ct)
    {
        if (request.Title is null || request.Tags is null)
        {
            return Invalid("title", "A title and a list of tags are required.");
        }

        // A cover is the author's own upload (or, for the admin, his own found cover): nobody shows someone else's file
        if (request.CoverFileId is { } cover
            && !await db.Files.AsNoTracking().AnyAsync(f => f.Id == cover && !f.IsDeleted && f.Kind == Engine.Files.FileKind.Upload && f.OwnerId == user.UserId(), ct)
            && (gameId is null || !await db.Games.AsNoTracking().AnyAsync(g => g.Id == gameId && g.CoverFileId == cover, ct)))
        {
            return Rejected(PoolRules.CoverUnknown, "The cover is one of your own uploads.");
        }

        var card = new GameCard(
            request.Title, [.. request.Tags.Select(t => t ?? "")], request.Hours, request.Year, request.SteamAppId, request.CoverFileId, request.Note, request.IsCoop, request.CompletionCondition);
        return await SendAsync(request.CommandId, command(card), gameId, user, db, bus, ct);
    }

    private static async Task<IResult> SendAsync(Guid commandId, IGlobalCommand command, Guid? gameId, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (commandId == Guid.Empty)
        {
            return Invalid("commandId", "A command id is required.");
        }

        var outcome = await bus.SendAsync(new CommandEnvelope(commandId, Guid.Empty, command, user.UserId()), ct);
        if (!outcome.IsAccepted)
        {
            return outcome.Rejection!.Code == PoolRules.Unknown ? TypedResults.NotFound() : Rejected(outcome.Rejection.Code, outcome.Rejection.Detail);
        }

        var id = gameId ?? outcome.Events.Select(e => e.Event).OfType<GameAdded>().Single().GameId;
        var game = await db.Games.AsNoTracking().SingleAsync(g => g.Id == id, ct);
        return TypedResults.Ok(View(game, await AuthorsAsync(db, [game], ct)));
    }

    private static async Task<IResult> SendCategoryAsync(Guid commandId, IGlobalCommand command, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (commandId == Guid.Empty)
        {
            return Invalid("commandId", "A command id is required.");
        }

        var outcome = await bus.SendAsync(new CommandEnvelope(commandId, Guid.Empty, command, user.UserId()), ct);
        return outcome.IsAccepted
            ? (await CategoriesAsync(db, ct))
            : outcome.Rejection!.Code == PoolRules.CategoryUnknown ? TypedResults.NotFound() : Rejected(outcome.Rejection.Code, outcome.Rejection.Detail);
    }

    private static async Task<Dictionary<Guid, string>> AuthorsAsync(GameEventDbContext db, IReadOnlyCollection<GameRecord> games, CancellationToken ct)
    {
        return await db.NamesAsync(games.Select(g => g.AuthorId).OfType<Guid>(), ct);
    }

    private static PoolGameView View(GameRecord game, Dictionary<Guid, string> authors) =>
        new(
            game.Id,
            game.Title,
            [.. PoolReader.Tags(game.TagsJson)],
            game.Hours,
            game.Year,
            game.SteamAppId,
            game.CoverFileId is { } cover ? FileLinkView.Of(cover) : null,
            game.Note,
            game.IsCoop,
            (game.AuthorId is { } author ? authors.GetValueOrDefault(author) : null) ?? game.AuthorName,
            game.IsDeleted,
            game.CompletionCondition);

    private static ProblemHttpResult Rejected(string code, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The command was rejected.", detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static RouteHandlerBuilder WithPoolErrors(this RouteHandlerBuilder builder) =>
        builder
            .Produces<PoolGameView>()
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();

    private static RouteHandlerBuilder WithCategoryErrors(this RouteHandlerBuilder builder) =>
        builder
            .Produces<IReadOnlyList<CategoryView>>()
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
}
