using GameEvent.Engine.Files;
using GameEvent.Engine.Kernel;

namespace GameEvent.Infrastructure.Files;

/// <summary>
/// A stored file (D-108): the bytes are on disk under <see cref="FileNames"/>, the row says whose and what they are. Soft
/// deletion keeps the row (CLAUDE.md, invariant 11).
/// </summary>
public sealed class FileRecord
{
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    /// <summary><c>image/webp</c> or <c>image/gif</c>: what the file is served as.</summary>
    public required string MediaType { get; set; }

    public long Bytes { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>1 for a still image, more for an animated GIF.</summary>
    public int Frames { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>An upload the site shows, or a bug report's screenshot only the admin sees (D-121).</summary>
    public FileKind Kind { get; set; }
}

/// <summary>
/// The server stored an uploaded file already written to disk (D-108): the queue records whose it is. The id is given
/// by the endpoint that wrote the file. <see cref="DailyLimit"/> counts the owner's files of the same <see cref="Kind"/>.
/// </summary>
public sealed record RecordFile(
    Guid FileId, Guid OwnerId, string MediaType, long Bytes, int Width, int Height, int Frames, int DailyLimit, FileKind Kind = FileKind.Upload)
    : Queue.IGlobalCommand;

/// <summary>File names on disk (D-108): the id in lower-case hex, never anything the user sent.</summary>
public static class FileNames
{
    public const string Webp = "image/webp";
    public const string Gif = "image/gif";

    public static string Main(Guid id, string mediaType) => $"{id:N}{Extension(mediaType)}";

    public static string Thumbnail(Guid id, string mediaType) => $"{id:N}.thumb{Extension(mediaType)}";

    public static string Extension(string mediaType) => mediaType switch
    {
        Webp => ".webp",
        Gif => ".gif",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Only WebP and GIF are stored."),
    };
}

public static class FileRules
{
    public const string OwnerUnknown = "file.ownerUnknown";
    public const string AlreadyStored = "file.alreadyStored";
    public const string DailyLimit = "file.dailyLimit";
    public const string MediaTypeInvalid = "file.mediaTypeInvalid";
    public const string KindInvalid = "file.kindInvalid";
    public const string NotStill = "file.notStill";

    /// <summary>The window of the daily upload limit: the last 24 hours, not a calendar day.</summary>
    public static readonly TimeSpan Day = TimeSpan.FromDays(1);

    public static Rejection? Check(RecordFile command) =>
        command.MediaType is not (FileNames.Webp or FileNames.Gif)
            ? new Rejection(MediaTypeInvalid, "Only WebP and GIF are stored.")
            : !Enum.IsDefined(command.Kind)
                ? new Rejection(KindInvalid, "Unknown kind of file.")
                : null;
}
