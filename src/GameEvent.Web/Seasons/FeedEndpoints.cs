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

/// <summary>A page of the feed, newest first; <c>nextBefore</c> — the sequence to ask before for the next page, none at the start of the log.</summary>
public sealed record FeedView(IReadOnlyList<FeedEntryView> Entries, long? NextBefore);

/// <summary>A season a user took part in, with their points and, once the season finished, their place.</summary>
public sealed record ProfileSeasonView(Guid SeasonId, string SeasonName, SeasonStatus Status, Guid PlayerId, int Points, int? Place);

/// <summary>A review the user left: the game, the season, the rating and the text (SPEC «Отзыв»).</summary>
public sealed record ProfileReviewView(Guid RunId, Guid GameId, string GameTitle, Guid SeasonId, string SeasonName, int Rating, string? Text, DateTimeOffset? CompletedAt);

/// <summary>
/// A user's profile (D-124): the name, the avatar, the seasons they played and their reviews. <c>completed</c> — games
/// completed and not rejected, over all seasons.
/// </summary>
public sealed record ProfileView(
    Guid Id, string Name, FileLinkView? Avatar, IReadOnlyList<ProfileSeasonView> Seasons, IReadOnlyList<ProfileReviewView> Reviews, int Completed);

/// <summary>A run of a game in any season, with its review if there is one (the game page, D-124).</summary>
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
    string? ReviewText);

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
        return TypedResults.Ok(new FeedView(entries, nextBefore));
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
            .Select(p => new ProfileSeasonView(p.SeasonId, seasons[p.SeasonId].Name, seasons[p.SeasonId].Status, p.Id, p.Points, places.TryGetValue(p.Id, out var place) ? place : null))];
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
                    review?.Text);
            })];
        return TypedResults.Ok(views);
    }
}
