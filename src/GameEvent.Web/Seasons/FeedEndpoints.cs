using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Web.Accounts;
using GameEvent.Web.Files;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>
/// One event of the season's feed (GLOSSARY «Лента», D-124): its place in the log, the command it came from, when, the
/// event type and its data as the current format reads it (older versions upcast), who sent the command (none for the
/// scheduler) and whether the admin undid it. The screen turns a type and its data into a line of text.
/// </summary>
public sealed record FeedEntryView(
    long Sequence, Guid CommandId, DateTimeOffset OccurredAt, string Type, JsonElement Data, string? Author, bool Undone);

/// <summary>
/// A player of the season, for the feed's lines (H5, D-150): the name, the account for the profile link (<c>hasProfile</c> —
/// false once the account is deleted) and the avatar. The list is in the season screen's order, so a player's token colour
/// is their place in it.
/// </summary>
public sealed record FeedPlayerView(Guid Id, Guid UserId, string Name, FileLinkView? Avatar, bool HasProfile);

/// <summary>A game an event of the page names; <c>hasPage</c> — the viewer may open its page (a deleted game is the admin's).</summary>
public sealed record FeedGameView(Guid Id, string Title, bool HasPage);

/// <summary>A run an event of the page names, with its game: most events of a run carry only its id.</summary>
public sealed record FeedRunView(Guid Id, Guid GameId);

/// <summary>
/// A page of the feed, newest first; <c>nextBefore</c> — the sequence to ask before for the next page, none at the start of
/// the log. <c>players</c> — every player of the season; <c>games</c> and <c>runs</c> — those the page's events name (D-150).
/// </summary>
public sealed record FeedView(
    IReadOnlyList<FeedEntryView> Entries,
    long? NextBefore,
    IReadOnlyList<FeedPlayerView> Players,
    IReadOnlyList<FeedGameView> Games,
    IReadOnlyList<FeedRunView> Runs);

/// <summary>
/// A season a user took part in, with their points and, once the season finished, their place. <c>token</c> — their place in
/// the season's list of players (by name), which the screens colour their token by (D-150).
/// </summary>
public sealed record ProfileSeasonView(Guid SeasonId, string SeasonName, SeasonStatus Status, Guid PlayerId, int Points, int? Place, int Token);

/// <summary>A review the user left: the game, the season, the rating and the text (SPEC «Отзыв»).</summary>
public sealed record ProfileReviewView(Guid RunId, Guid GameId, string GameTitle, Guid SeasonId, string SeasonName, int Rating, string? Text, DateTimeOffset? CompletedAt);

/// <summary>
/// A user's profile (D-124): the name, the avatar, the seasons they played and their reviews. <c>completed</c> — games
/// completed and not rejected, over all seasons.
/// </summary>
public sealed record ProfileView(
    Guid Id, string Name, FileLinkView? Avatar, IReadOnlyList<ProfileSeasonView> Seasons, IReadOnlyList<ProfileReviewView> Reviews, int Completed);

/// <summary>
/// A run of a game in any season, with its review if there is one (the game page, D-124). <c>token</c> — the player's place
/// in that season's list of players, their token colour there (D-150).
/// </summary>
public sealed record GameRunView(
    Guid RunId,
    Guid SeasonId,
    string SeasonName,
    Guid PlayerId,
    Guid UserId,
    string PlayerName,
    RunStatus Status,
    Difficulty? Difficulty,
    decimal? Hours,
    DateTimeOffset? CompletedAt,
    int? Rating,
    string? ReviewText,
    int Token);

/// <summary>
/// The public reads built from the log and its projection (SPEC «Лента», «Отзыв … виден в ленте, профиле и на странице
/// игры»; E1, D-124): the season's feed page by page, a user's profile and the runs of a game. Everyone signed in reads them.
/// </summary>
public static class FeedEndpoints
{
    public const int MaxFeedPage = 100;

    public static void MapFeed(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/seasons/{seasonId:guid}/feed", FeedAsync).WithTags("Seasons").RequireAuthorization().Produces(StatusCodes.Status404NotFound);
        api.MapGet("/users/{userId:guid}", ProfileAsync).WithTags("Accounts").RequireAuthorization().Produces(StatusCodes.Status404NotFound);
        api.MapGet("/pool/{gameId:guid}/runs", GameRunsAsync).WithTags("Pool").RequireAuthorization().Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<Results<Ok<FeedView>, NotFound, ValidationProblem>> FeedAsync(
        Guid seasonId, System.Security.Claims.ClaimsPrincipal user, GameEventDbContext db, CancellationToken ct, long? before = null, int limit = 50)
    {
        if (limit is < 1 or > MaxFeedPage)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["limit"] = [$"A page is 1–{MaxFeedPage} events."] });
        }

        if (before < 1)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["before"] = ["A sequence starts from 1."] });
        }

        // The global log (accounts, files, bug reports) is nobody's season, whatever the tables hold
        if (seasonId == Guid.Empty || !await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        var admin = user.IsInRole(nameof(Infrastructure.Accounts.Role.Admin));
        var viewer = user.UserId();
        var viewerPlayerId = await db.SeasonPlayers.AsNoTracking()
            .Where(p => p.SeasonId == seasonId && p.UserId == viewer)
            .Select(p => (Guid?)p.Id)
            .SingleOrDefaultAsync(ct);

        var rows = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && (before == null || e.Sequence < before))
            .OrderByDescending(e => e.Sequence)
            .Take(limit)
            .ToListAsync(ct);
        var authorIds = rows.Select(r => r.AuthorId).OfType<Guid>().Distinct().ToList();
        var authors = await db.Users.AsNoTracking().Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        IReadOnlyList<FeedEntryView> entries = [.. rows.Select(r =>
        {
            var decoded = EventCodec.Decode(new StoredEvent(r.Type, r.Version, r.Data));
            var data = (JsonObject)JsonSerializer.SerializeToNode(decoded, decoded.GetType(), EngineJson.Options)!;
            return FeedVisibility.Shown(r.Type, data, admin, viewerPlayerId) is not { } shown
                ? null
                : new FeedEntryView(
                    r.Sequence,
                    r.CommandId,
                    r.OccurredAt,
                    r.Type,
                    JsonSerializer.SerializeToElement(shown),
                    r.AuthorId is { } author ? authors.GetValueOrDefault(author) : null,
                    r.UndoneByEventId is not null);
        }).OfType<FeedEntryView>()];
        var nextBefore = rows.Count == limit && rows[^1].Sequence > 1 ? rows[^1].Sequence : (long?)null;
        var (players, games, runs) = await ReferencesAsync(db, seasonId, entries, admin, ct);
        return TypedResults.Ok(new FeedView(entries, nextBefore, players, games, runs));
    }

    /// <summary>
    /// The names the page's events point to by id (D-150): the season's players in the season screen's order, the runs the
    /// events name with their games, and the titles of those games.
    /// </summary>
    private static async Task<(IReadOnlyList<FeedPlayerView> Players, IReadOnlyList<FeedGameView> Games, IReadOnlyList<FeedRunView> Runs)> ReferencesAsync(
        GameEventDbContext db, Guid seasonId, IReadOnlyList<FeedEntryView> entries, bool admin, CancellationToken ct)
    {
        var seasonPlayers = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == seasonId).OrderBy(p => p.Name).ToListAsync(ct);
        var userIds = seasonPlayers.Select(p => p.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        IReadOnlyList<FeedPlayerView> players = [.. seasonPlayers.Select(p =>
        {
            var user = users.GetValueOrDefault(p.UserId);
            return new FeedPlayerView(
                p.Id,
                p.UserId,
                p.Name,
                user?.AvatarFileId is { } file ? FileLinkView.Of(file) : null,
                user is { IsDeleted: false });
        })];

        var runIds = new HashSet<Guid>();
        var gameIds = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            Collect(entry.Data, runIds, gameIds);
        }

        var runs = await db.Runs.AsNoTracking().Where(r => runIds.Contains(r.Id)).Select(r => new FeedRunView(r.Id, r.GameId)).ToListAsync(ct);
        gameIds.UnionWith(runs.Select(r => r.GameId));
        var games = await db.Games.AsNoTracking()
            .Where(g => gameIds.Contains(g.Id))
            .Select(g => new FeedGameView(g.Id, g.Title, admin || !g.IsDeleted))
            .ToListAsync(ct);
        return (players, games, runs);
    }

    /// <summary>
    /// Each player's place in their season's list of players by name — the order of the season screen, whose token colours
    /// follow it (D-150).
    /// </summary>
    private static async Task<Dictionary<Guid, int>> TokensAsync(GameEventDbContext db, IReadOnlyCollection<Guid> seasonIds, CancellationToken ct)
    {
        var players = await db.SeasonPlayers.AsNoTracking()
            .Where(p => seasonIds.Contains(p.SeasonId))
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.SeasonId })
            .ToListAsync(ct);
        return players.GroupBy(p => p.SeasonId).SelectMany(g => g.Select((p, i) => (p.Id, Token: i))).ToDictionary(x => x.Id, x => x.Token);
    }

    /// <summary>Every <c>runId</c>, <c>gameId</c> and <c>gameIds</c> in an event's data, at any depth (misses, offers).</summary>
    private static void Collect(JsonElement data, HashSet<Guid> runIds, HashSet<Guid> gameIds)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                Collect(item, runIds, gameIds);
            }

            return;
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in data.EnumerateObject())
        {
            if (property.Name == "runId" && property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var run))
            {
                runIds.Add(run);
            }
            else if (property.Name == "gameId" && property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var game))
            {
                gameIds.Add(game);
            }
            else if (property.Name == "gameIds" && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.TryGetGuid(out var listed))
                    {
                        gameIds.Add(listed);
                    }
                }
            }
            else
            {
                Collect(property.Value, runIds, gameIds);
            }
        }
    }

    private static async Task<Results<Ok<ProfileView>, NotFound>> ProfileAsync(
        Guid userId, System.Security.Claims.ClaimsPrincipal viewer, GameEventDbContext db, CancellationToken ct)
    {
        // A deleted account keeps its name in the seasons it played, but has no page of its own
        if (await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct) is not { } user)
        {
            return TypedResults.NotFound();
        }

        var players = await db.SeasonPlayers.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct);
        var playerIds = players.Select(p => p.Id).ToList();
        var seasonIds = players.Select(p => p.SeasonId).Distinct().ToList();
        var seasons = await db.Seasons.AsNoTracking().Where(s => seasonIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var tokens = await TokensAsync(db, seasonIds, ct);
        var places = await db.SeasonResults.AsNoTracking().Where(r => playerIds.Contains(r.PlayerId)).ToDictionaryAsync(r => r.PlayerId, r => r.Place, ct);
        var reviews = await db.Reviews.AsNoTracking().Where(r => playerIds.Contains(r.PlayerId)).ToListAsync(ct);
        var runs = await db.Runs.AsNoTracking().Where(r => playerIds.Contains(r.PlayerId)).ToListAsync(ct);
        var gameIds = reviews.Select(r => r.GameId).Distinct().ToList();
        // Like the game's own page: a deleted game is the admin's to see
        var withDeleted = viewer.IsInRole(nameof(Infrastructure.Accounts.Role.Admin));
        var games = await db.Games.AsNoTracking().Where(g => gameIds.Contains(g.Id) && (withDeleted || !g.IsDeleted)).ToDictionaryAsync(g => g.Id, g => g.Title, ct);
        reviews = [.. reviews.Where(r => games.ContainsKey(r.GameId))];

        IReadOnlyList<ProfileSeasonView> seasonViews = [.. players
            .Where(p => seasons.ContainsKey(p.SeasonId))
            .OrderByDescending(p => seasons[p.SeasonId].CreatedAt)
            .Select(p => new ProfileSeasonView(
                p.SeasonId,
                seasons[p.SeasonId].Name,
                seasons[p.SeasonId].Status,
                p.Id,
                p.Points,
                places.TryGetValue(p.Id, out var place) ? place : null,
                tokens.GetValueOrDefault(p.Id)))];
        var completedAt = runs.ToDictionary(r => r.Id, r => r.CompletedAt);
        IReadOnlyList<ProfileReviewView> reviewViews = [.. reviews
            .Select(r => new ProfileReviewView(
                r.RunId,
                r.GameId,
                games[r.GameId],
                r.SeasonId,
                seasons.TryGetValue(r.SeasonId, out var season) ? season.Name : "?",
                r.Rating,
                r.Text,
                completedAt.GetValueOrDefault(r.RunId)))
            .OrderByDescending(r => r.CompletedAt)];
        var avatar = user.AvatarFileId is { } file ? FileLinkView.Of(file) : null;
        return TypedResults.Ok(new ProfileView(
            user.Id, user.Name, avatar, seasonViews, reviewViews, runs.Count(r => r.Status == RunStatus.Completed)));
    }

    private static async Task<Results<Ok<IReadOnlyList<GameRunView>>, NotFound>> GameRunsAsync(
        Guid gameId, System.Security.Claims.ClaimsPrincipal user, GameEventDbContext db, CancellationToken ct)
    {
        // Like the game's card: a deleted game is the admin's to see
        if (await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameId, ct) is not { } game
            || (game.IsDeleted && !user.IsInRole(nameof(Infrastructure.Accounts.Role.Admin))))
        {
            return TypedResults.NotFound();
        }

        var runs = await db.Runs.AsNoTracking().Where(r => r.GameId == gameId).ToListAsync(ct);
        var playerIds = runs.Select(r => r.PlayerId).Distinct().ToList();
        var players = await db.SeasonPlayers.AsNoTracking().Where(p => playerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var seasonIds = runs.Select(r => r.SeasonId).Distinct().ToList();
        var seasons = await db.Seasons.AsNoTracking().Where(s => seasonIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var tokens = await TokensAsync(db, seasonIds, ct);
        var runIds = runs.Select(r => r.Id).ToList();
        var reviews = await db.Reviews.AsNoTracking().Where(r => runIds.Contains(r.RunId)).ToDictionaryAsync(r => r.RunId, ct);
        IReadOnlyList<GameRunView> views = [.. runs
            .OrderByDescending(r => r.StartedAt)
            .Select(r =>
            {
                var player = players.GetValueOrDefault(r.PlayerId);
                var review = reviews.GetValueOrDefault(r.Id);
                return new GameRunView(
                    r.Id,
                    r.SeasonId,
                    seasons.GetValueOrDefault(r.SeasonId) ?? "?",
                    r.PlayerId,
                    player?.UserId ?? Guid.Empty,
                    player?.Name ?? "?",
                    r.Status,
                    r.Difficulty,
                    r.Hours,
                    r.CompletedAt,
                    review?.Rating,
                    review?.Text,
                    tokens.GetValueOrDefault(r.PlayerId));
            })];
        return TypedResults.Ok(views);
    }
}
