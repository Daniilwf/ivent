using GameEvent.Engine.Files;
using GameEvent.Engine.Gallery;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Queue;

public sealed partial class CommandProcessor
{
    private static async Task<PreparedGalleryFile?> CheckGalleryFileAsync(
        GameEventDbContext db, Guid seasonId, IWriteGalleryMoment command, PreparedGalleryFile? prepared, CancellationToken ct)
    {
        if (prepared is not { Bytes: > 0, Width: > 0, Height: > 0, Frames: 1 }
            || prepared.FileId == Guid.Empty
            || prepared.MediaType != FileNames.Webp
            || await db.Files.AnyAsync(file => file.Id == prepared.FileId, ct))
        {
            return null;
        }

        return command switch
        {
            PublishGalleryMoment standalone when string.Equals(prepared.ImageSha256, standalone.ImageSha256, StringComparison.OrdinalIgnoreCase)
                => prepared,
            PublishProofGalleryMoment proof when prepared.ProofApprovalCommandId is { } expected
                && (await GalleryProofSources.FindAsync(db, seasonId, proof.RunId, proof.FileId, proof.UserId, ct))?.ApprovalCommandId == expected
                => prepared,
            _ => null,
        };
    }

    private static void RecordGalleryFile(
        GameEventDbContext db, Guid seasonId, IGalleryMomentPublished published, PreparedGalleryFile prepared)
    {
        if (published.FileId != prepared.FileId)
        {
            throw new InvalidOperationException("A gallery publication named an image other than the one prepared for it.");
        }

        db.Files.Add(new FileRecord
        {
            Id = published.FileId,
            OwnerId = published.UserId,
            Kind = FileKind.GalleryImage,
            CreatedAt = published.CreatedAt,
            MediaType = prepared.MediaType,
            Bytes = prepared.Bytes,
            Width = prepared.Width,
            Height = prepared.Height,
            Frames = prepared.Frames,
        });
        db.GalleryMediaBindings.Add(new GalleryMediaBindingRecord
        {
            FileId = published.FileId,
            SeasonId = seasonId,
            ItemId = published.ItemId,
        });
    }
}
