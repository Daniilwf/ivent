using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Files;

// The global log's file events (D-108): who stored which file and what it is. The bytes live on disk, not in the log.

/// <summary>
/// What a stored file is for (D-121): an upload the user shows on the site (a proof, an avatar, a cover), or a bug
/// report's screenshot, which only the admin sees and nothing else may show.
/// </summary>
public enum FileKind
{
    Upload,
    BugScreenshot,
}

/// <summary>
/// A user uploaded a file and the server stored it: a screenshot re-encoded to WebP or a GIF kept as it is, with a
/// thumbnail for the map. <see cref="Frames"/> is 1 for a still image. v2 (D-121) adds <see cref="Kind"/>; v1 was always
/// an upload.
/// </summary>
[EventType("file-stored", version: 2)]
public sealed record FileStored(Guid FileId, Guid OwnerId, string MediaType, long Bytes, int Width, int Height, int Frames, FileKind Kind) : IGameEvent;
