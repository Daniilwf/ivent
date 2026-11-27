using System.Collections.Immutable;
using GameEvent.Engine.Gallery;
using GameEvent.Engine.Reactions;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Seasons;

internal static partial class SeasonProjection
{
    private static async Task WriteGalleryAsync(
        GameEventDbContext db, Guid seasonId, ImmutableSortedDictionary<Guid, GalleryItem> before,
        ImmutableSortedDictionary<Guid, GalleryItem> after, CancellationToken ct)
    {
        if (ReferenceEquals(before, after))
        {
            return;
        }

        foreach (var id in Changed(before, after))
        {
            if (!after.TryGetValue(id, out var item))
            {
                // An undo removes the moment. The protected local file and its binding are kept for provenance.
                db.GalleryMoments.Remove(await db.GalleryMoments.FindAsync([seasonId, id], ct) ?? throw Missing("gallery moment", id));
            }
            else if (before.ContainsKey(id))
            {
                var row = await db.GalleryMoments.FindAsync([seasonId, id], ct) ?? throw Missing("gallery moment", id);
                row.Hidden = item.Hidden;
                row.ConsentRevoked = item.ConsentRevoked;
            }
            else
            {
                db.GalleryMoments.Add(new GalleryMomentRecord
                {
                    SeasonId = seasonId,
                    ItemId = item.ItemId,
                    UserId = item.UserId,
                    PlayerId = item.PlayerId,
                    FileId = item.FileId,
                    Caption = item.Caption,
                    CreatedAt = item.CreatedAt,
                    CreatedDay = item.CreatedDay,
                    Hidden = item.Hidden,
                    ProofRunId = item.ProofSource?.RunId,
                    ProofFileId = item.ProofSource?.FileId,
                    ProofApprovalCommandId = item.ProofSource?.ApprovalCommandId,
                    ConsentRevoked = item.ConsentRevoked,
                });
            }
        }
    }

    private static async Task<ImmutableSortedDictionary<Guid, GalleryItem>> ReadGalleryAsync(
        GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        var rows = await db.GalleryMoments.AsNoTracking().Where(row => row.SeasonId == seasonId).ToListAsync(ct);
        return rows.ToImmutableSortedDictionary(row => row.ItemId, row => new GalleryItem(
            row.ItemId, row.UserId, row.PlayerId, row.FileId, row.Caption, row.CreatedAt, row.CreatedDay, row.Hidden,
            row is { ProofRunId: { } runId, ProofFileId: { } fileId, ProofApprovalCommandId: { } approval }
                ? new GalleryProofSource(runId, fileId, approval)
                : null,
            row.ConsentRevoked));
    }

    private static async Task WriteGalleryReactionsAsync(GameEventDbContext db, SeasonState before, SeasonState after, CancellationToken ct)
    {
        if (ReferenceEquals(before.GalleryReactions, after.GalleryReactions))
        {
            return;
        }

        foreach (var added in after.GalleryReactions.Except(before.GalleryReactions))
        {
            db.GalleryReactions.Add(new GalleryReactionRecord
            {
                SeasonId = after.SeasonId,
                ItemId = added.ItemId,
                UserId = added.UserId,
                Emoji = added.Emoji,
            });
        }

        foreach (var removed in before.GalleryReactions.Except(after.GalleryReactions))
        {
            var row = await db.GalleryReactions.FindAsync([before.SeasonId, removed.ItemId, removed.UserId, removed.Emoji], ct)
                ?? throw Missing("gallery reaction", removed.ItemId);
            db.GalleryReactions.Remove(row);
        }
    }

    private static async Task<ImmutableHashSet<GalleryReaction>> ReadGalleryReactionsAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        var rows = await db.GalleryReactions.AsNoTracking().Where(row => row.SeasonId == seasonId).ToListAsync(ct);
        return [.. rows.Select(row => new GalleryReaction(row.ItemId, row.UserId, row.Emoji))];
    }
}
