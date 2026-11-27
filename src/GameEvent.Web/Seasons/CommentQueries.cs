using GameEvent.Engine.Comments;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

internal static class CommentQueries
{
    internal static IQueryable<CommentRecord> Visible(this IQueryable<CommentRecord> rows) =>
        rows.Where(row => row.Status == CommentStatus.Visible);

    internal static async Task<IReadOnlyDictionary<Guid, int>> ReadFeedCountsAsync(
        GameEventDbContext db, Guid seasonId, IReadOnlyCollection<Guid> eligible, CancellationToken ct)
    {
        if (eligible.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        return await db.Comments.AsNoTracking()
            .Where(row => row.SeasonId == seasonId && row.TargetKind == CommentTargetKind.FeedEntry && eligible.Contains(row.TargetId))
            .Visible()
            .Where(row => db.Users.Any(author => author.Id == row.UserId))
            .GroupBy(row => row.TargetId)
            .Select(group => new { TargetId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.TargetId, row => row.Count, ct);
    }
}
