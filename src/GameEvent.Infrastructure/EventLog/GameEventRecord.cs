namespace GameEvent.Infrastructure.EventLog;

/// <summary>
/// One event of the log (SPEC «Модель данных»: GameEvent). Rows are never rewritten; the data is read
/// through <see cref="Engine.Kernel.EventCodec"/>, which upcasts old versions.
/// </summary>
public sealed class GameEventRecord
{
    public long Id { get; set; }

    /// <summary>
    /// The season log the event belongs to; <see cref="Guid.Empty"/> is the global log of admin events
    /// (pool, categories, D-19), so the unique (SeasonId, Sequence) index covers it too.
    /// </summary>
    public Guid SeasonId { get; set; }

    /// <summary>Position in the season log, 1, 2, 3… without gaps.</summary>
    public long Sequence { get; set; }

    /// <summary>The command that produced the event; one command, one transaction, many events.</summary>
    public Guid CommandId { get; set; }

    /// <summary>Command type name: a repeated <see cref="CommandId"/> must be the same command.</summary>
    public required string CommandType { get; set; }

    /// <summary>
    /// SHA-256 of the command's JSON, hex: a repeated <see cref="CommandId"/> must carry the same body too, or it is
    /// refused rather than answered with the first command's events.
    /// </summary>
    public required string CommandHash { get; set; }

    public required string Type { get; set; }

    public int Version { get; set; }

    public required string Data { get; set; }

    public Guid? AuthorId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The compensating event that undid this one (undo, task C12); null while in force.</summary>
    public long? UndoneByEventId { get; set; }
}
