using GameEvent.Engine.Files;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Files;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Queue;

/// <summary>
/// File commands (D-108): the bytes are already on disk, the queue records whose they are and keeps the daily limit —
/// counted here, one command at a time, so two uploads at once cannot both pass the last free place.
/// </summary>
public sealed partial class CommandProcessor
{
    private static async Task<(IReadOnlyList<IGameEvent> Events, Action? Apply, string? Secret, Rejection? Rejection)> DecideFileAsync(
        GameEventDbContext db, RecordFile command, DateTimeOffset now, CancellationToken ct)
    {
        static (IReadOnlyList<IGameEvent>, Action?, string?, Rejection?) Reject(string code, string detail) => ([], null, null, new Rejection(code, detail));

        if (FileRules.Check(command) is { } invalid)
        {
            return ([], null, null, invalid);
        }

        if (!await db.Users.AnyAsync(u => u.Id == command.OwnerId && !u.IsDeleted, ct))
        {
            return Reject(FileRules.OwnerUnknown, "The owner is not an active account.");
        }

        if (await db.Files.AnyAsync(f => f.Id == command.FileId, ct))
        {
            return Reject(FileRules.AlreadyStored, $"File {command.FileId} is already stored.");
        }

        var since = now - FileRules.Day;
        if (await db.Files.CountAsync(f => f.OwnerId == command.OwnerId && f.CreatedAt > since, ct) >= command.DailyLimit)
        {
            return Reject(FileRules.DailyLimit, $"At most {command.DailyLimit} uploads in 24 hours.");
        }

        var record = new FileRecord
        {
            Id = command.FileId,
            OwnerId = command.OwnerId,
            MediaType = command.MediaType,
            Bytes = command.Bytes,
            Width = command.Width,
            Height = command.Height,
            Frames = command.Frames,
            CreatedAt = now,
        };
        return (
            [new FileStored(command.FileId, command.OwnerId, command.MediaType, command.Bytes, command.Width, command.Height, command.Frames)],
            () => db.Files.Add(record),
            null,
            null);
    }
}
