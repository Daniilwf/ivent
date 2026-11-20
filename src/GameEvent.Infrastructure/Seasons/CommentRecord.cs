using GameEvent.Engine.Comments;

namespace GameEvent.Infrastructure.Seasons;

public sealed class CommentRecord
{
    public Guid SeasonId { get; init; }
    public Guid CommentId { get; init; }
    public CommentTargetKind TargetKind { get; init; }
    public Guid TargetId { get; init; }
    public Guid UserId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The season-local debit recorded at acceptance, retained when the comment stops being visible.</summary>
    public DateOnly CreatedDay { get; init; }

    public required string Text { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public CommentStatus Status { get; set; }
}
