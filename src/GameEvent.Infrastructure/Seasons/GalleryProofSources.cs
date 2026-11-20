using System.Text.Json;
using GameEvent.Engine.Files;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Files;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Seasons;

public sealed record ApprovedGalleryProof(Guid ApprovalCommandId, string MediaType);

public static class GalleryProofSources
{
    public static IQueryable<FileRecord> StillUploads(GameEventDbContext db, Guid userId) =>
        db.Files.AsNoTracking().Where(file => file.OwnerId == userId
            && file.Kind == FileKind.Upload && file.Frames == 1 && !file.IsDeleted);

    public static async Task<ApprovedGalleryProof?> FindAsync(
        GameEventDbContext db, Guid seasonId, Guid runId, Guid fileId, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var proof = await (
            from candidate in db.Proofs.AsNoTracking()
            join player in db.SeasonPlayers.AsNoTracking() on candidate.PlayerId equals player.Id
            join run in db.Runs.AsNoTracking() on candidate.RunId equals run.Id
            where candidate.SeasonId == seasonId && candidate.RunId == runId && candidate.Status == ProofStatus.Approved
                && player.SeasonId == seasonId && player.UserId == userId && !player.IsInactive
                && run.SeasonId == seasonId && run.PlayerId == candidate.PlayerId && run.Status == RunStatus.Completed
            select new { candidate.FilesJson, candidate.PlayerId }).SingleOrDefaultAsync(ct);
        if (proof is null || !JsonSerializer.Deserialize<EquatableArray<Guid>>(proof.FilesJson, EngineJson.Options).Contains(fileId))
        {
            return null;
        }

        var mediaType = await StillUploads(db, userId).Where(file => file.Id == fileId)
            .Select(file => file.MediaType).SingleOrDefaultAsync(ct);
        if (mediaType is null)
        {
            return null;
        }

        // Old approvals already have an exact identity in the journal. The timestamp can be shared by a reapproval.
        var approvals = db.Events.AsNoTracking()
            .Where(row => row.SeasonId == seasonId && row.Type == "proof-approved" && row.UndoneByEventId == null)
            .OrderByDescending(row => row.Sequence)
            .Select(row => new { row.CommandId, row.Type, row.Version, row.Data });
        await foreach (var row in approvals.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)) is ProofApproved approval
                && approval.RunId == runId && approval.PlayerId == proof.PlayerId)
            {
                return new ApprovedGalleryProof(row.CommandId, mediaType);
            }
        }

        return null;
    }
}
