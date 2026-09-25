using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Files;

// The global log's file events (D-108): who stored which file and what it is. The bytes live on disk, not in the log.

/// <summary>
/// A user uploaded a file and the server stored it: a screenshot re-encoded to WebP or a GIF kept as it is, with a
/// thumbnail for the map. <see cref="Frames"/> is 1 for a still image.
/// </summary>
[EventType("file-stored")]
public sealed record FileStored(Guid FileId, Guid OwnerId, string MediaType, long Bytes, int Width, int Height, int Frames) : IGameEvent;
