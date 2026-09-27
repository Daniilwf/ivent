using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>
/// A player's token colour in a season (D-150, D-202): their place in the season's list of players by name. Every view
/// that shows a player's sticker — the season screen, the feed, a profile, a game page — takes <c>token</c> from here, so
/// one player has one colour everywhere in a season.
/// </summary>
public static class PlayerTokens
{
    /// <summary>The season's players in token order.</summary>
    public static IQueryable<SeasonPlayerRecord> InTokenOrder(this IQueryable<SeasonPlayerRecord> players) =>
        players.OrderBy(p => p.Name).ThenBy(p => p.Id);

    /// <summary>The token of every player of the given seasons, by the season player's id.</summary>
    public static async Task<Dictionary<Guid, int>> TokensAsync(this GameEventDbContext db, IReadOnlyCollection<Guid> seasonIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var players = await db.SeasonPlayers.AsNoTracking()
            .Where(p => seasonIds.Contains(p.SeasonId))
            .InTokenOrder()
            .Select(p => new { p.Id, p.SeasonId })
            .ToListAsync(ct);
        return players.GroupBy(p => p.SeasonId).SelectMany(g => g.Select((p, i) => (p.Id, Token: i))).ToDictionary(x => x.Id, x => x.Token);
    }

    /// <summary>The user's token in the latest season they play (the profile's first season), or none.</summary>
    public static async Task<int?> LatestTokenAsync(this GameEventDbContext db, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var latest = await db.SeasonPlayers.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Join(db.Seasons.AsNoTracking(), p => p.SeasonId, s => s.Id, (p, s) => new { p.Id, p.SeasonId, s.CreatedAt })
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (latest is null)
        {
            return null;
        }

        var tokens = await db.TokensAsync([latest.SeasonId], ct);
        return tokens.TryGetValue(latest.Id, out var token) ? token : null;
    }
}
