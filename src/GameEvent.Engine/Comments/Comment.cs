using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Comments;

public enum CommentTargetKind
{
    FeedEntry,
    Review,
}

/// <summary>A public feed command or a reviewed run, within the season of the command envelope.</summary>
public readonly record struct CommentTarget(CommentTargetKind Kind, Guid Id);

public enum CommentStatus
{
    Visible,
    Deleted,
    Hidden,
}

/// <summary>A comment and its original season-calendar debit, retained after removal.</summary>
public sealed record Comment(
    Guid CommentId,
    CommentTarget Target,
    Guid UserId,
    string Text,
    DateTimeOffset CreatedAt,
    DateOnly CreatedDay,
    DateTimeOffset? EditedAt,
    CommentStatus Status);

/// <summary>The comment before an undone command, or null when that command posted it.</summary>
public sealed record CommentSnapshot(Guid CommentId, Comment? Before);

public interface ICommentCommand : ICommand
{
    Guid CommentId { get; }

    CommentTarget Target { get; }
}

public sealed record PostComment(Guid CommentId, CommentTarget Target, Guid UserId, string? Text) : ICommentCommand;

public sealed record EditComment(Guid CommentId, CommentTarget Target, Guid UserId, string? Text) : ICommentCommand;

public sealed record DeleteComment(Guid CommentId, CommentTarget Target, Guid UserId) : ICommentCommand;

/// <summary>Moderation by an administrator, authorized at the command boundary.</summary>
public sealed record HideComment(Guid CommentId, CommentTarget Target) : ICommentCommand;

[EventType(CommentPosted.Type)]
public sealed record CommentPosted(
    Guid CommentId,
    CommentTarget Target,
    Guid UserId,
    string Text,
    DateTimeOffset CreatedAt,
    DateOnly CreatedDay) : IGameEvent
{
    public const string Type = "comment-posted";
}

[EventType(CommentEdited.Type)]
public sealed record CommentEdited(Guid CommentId, string Text, DateTimeOffset EditedAt) : IGameEvent
{
    public const string Type = "comment-edited";
}

[EventType(CommentDeleted.Type)]
public sealed record CommentDeleted(Guid CommentId) : IGameEvent
{
    public const string Type = "comment-deleted";
}

[EventType(CommentHidden.Type)]
public sealed record CommentHidden(Guid CommentId) : IGameEvent
{
    public const string Type = "comment-hidden";
}

public static class CommentRejectionCodes
{
    public const string TargetUnknown = "comment.targetUnknown";
    public const string NotFound = "comment.notFound";
    public const string NotYours = "comment.notYours";
    public const string AlreadyExists = "comment.alreadyExists";
    public const string InvalidId = "comment.invalidId";
    public const string InvalidUser = "comment.invalidUser";
    public const string Empty = "comment.empty";
    public const string TooLong = "comment.tooLong";
    public const string DailyLimit = "comment.dailyLimit";
}
