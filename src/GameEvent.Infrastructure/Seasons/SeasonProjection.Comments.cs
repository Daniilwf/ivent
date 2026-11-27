using System.Collections.Immutable;
using GameEvent.Engine.Comments;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Seasons;

internal static partial class SeasonProjection
{
    // Edits and moderation retain the original debit. Undoing a post removes the row and refunds its debit.
    private static async Task WriteCommentsAsync(
        GameEventDbContext db, Guid seasonId, ImmutableSortedDictionary<Guid, Comment> before,
        ImmutableSortedDictionary<Guid, Comment> after, CancellationToken ct)
    {
        if (ReferenceEquals(before, after))
        {
            return;
        }

        foreach (var id in Changed(before, after))
        {
            if (!after.TryGetValue(id, out var comment))
            {
                db.Comments.Remove(await db.Comments.FindAsync([seasonId, id], ct) ?? throw Missing("comment", id));
                continue;
            }

            if (!before.ContainsKey(id))
            {
                db.Comments.Add(new CommentRecord
                {
                    SeasonId = seasonId,
                    CommentId = comment.CommentId,
                    TargetKind = comment.Target.Kind,
                    TargetId = comment.Target.Id,
                    UserId = comment.UserId,
                    CreatedAt = comment.CreatedAt,
                    CreatedDay = comment.CreatedDay,
                    Text = comment.Text,
                    EditedAt = comment.EditedAt,
                    Status = comment.Status,
                });
                continue;
            }

            var row = await db.Comments.FindAsync([seasonId, id], ct) ?? throw Missing("comment", id);
            row.Text = comment.Text;
            row.EditedAt = comment.EditedAt;
            row.Status = comment.Status;
        }
    }

    private static async Task<ImmutableSortedDictionary<Guid, Comment>> ReadCommentsAsync(
        GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        var rows = await db.Comments.AsNoTracking().Where(row => row.SeasonId == seasonId).ToListAsync(ct);
        return rows.ToImmutableSortedDictionary(row => row.CommentId, row => new Comment(
            row.CommentId, new CommentTarget(row.TargetKind, row.TargetId), row.UserId,
            row.Text, row.CreatedAt, row.CreatedDay, row.EditedAt, row.Status));
    }
}
