using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Comments;

internal static class CommentThread
{
    public static Decision Decide(SeasonState state, ICommentCommand command, EngineContext context)
    {
        if (!state.IsCreated || !state.Rules.Features.Comments)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Comments are disabled in this season.");
        }

        if (!HasPublicTarget(state, command.Target, context))
        {
            return Decision.Reject(CommentRejectionCodes.TargetUnknown, "There is no public discussion for this target.");
        }

        if (command.CommentId == Guid.Empty)
        {
            return Decision.Reject(CommentRejectionCodes.InvalidId, "A comment needs an id.");
        }

        if (command is PostComment post)
        {
            return Post(state, post, context);
        }

        if (!state.Comments.TryGetValue(command.CommentId, out var comment)
            || comment.Target != command.Target || comment.Status != CommentStatus.Visible)
        {
            return Decision.Reject(CommentRejectionCodes.NotFound, "There is no visible comment in this discussion.");
        }

        return command switch
        {
            EditComment edit when edit.UserId == comment.UserId => Edit(edit, context),
            DeleteComment delete when delete.UserId == comment.UserId => Decision.Accept(new CommentDeleted(delete.CommentId)),
            HideComment hide => Decision.Accept(new CommentHidden(hide.CommentId)),
            _ => Decision.Reject(CommentRejectionCodes.NotYours, "Only its author can edit or delete this comment."),
        };
    }

    private static bool HasPublicTarget(SeasonState state, CommentTarget target, EngineContext context) =>
        target.Id != Guid.Empty && (target.Kind switch
        {
            CommentTargetKind.FeedEntry => context.CommentTargetAvailable,
            CommentTargetKind.Review => state.Runs.TryGetValue(target.Id, out var run)
                && run.Review is not null
                && context.Pool.Games.Any(game => game.Id == run.GameId && !game.IsDeleted),
            _ => false,
        });

    private static Decision Post(SeasonState state, PostComment command, EngineContext context)
    {
        if (command.UserId == Guid.Empty)
        {
            return Decision.Reject(CommentRejectionCodes.InvalidUser, "A comment needs a signed-in account.");
        }

        if (state.Comments.ContainsKey(command.CommentId))
        {
            return Decision.Reject(CommentRejectionCodes.AlreadyExists, "This comment id has already been used.");
        }

        var text = command.Text?.Trim() ?? "";
        if (ValidateText(text) is { } invalid)
        {
            return invalid;
        }

        var now = context.Clock.UtcNow;
        var timezone = state.Rules.Season.Timezone;
        if (SeasonCalendar.Day(context.TimeZones, timezone, now) is not { } day)
        {
            return Decision.Reject(RejectionCodes.SeasonTimezoneUnknown, $"The season's time zone '{timezone}' is not known here.");
        }

        var limit = state.Rules.Social.CommentsPerDay;
        if (state.Comments.Values.Count(comment => comment.UserId == command.UserId && comment.CreatedDay == day) >= limit)
        {
            return Decision.Reject(CommentRejectionCodes.DailyLimit, "This account has reached the daily comment limit.");
        }

        return Decision.Accept(new CommentPosted(command.CommentId, command.Target, command.UserId, text, now, day));
    }

    private static Decision Edit(EditComment command, EngineContext context)
    {
        var text = command.Text?.Trim() ?? "";
        return ValidateText(text) ?? Decision.Accept(new CommentEdited(command.CommentId, text, context.Clock.UtcNow));
    }

    private static Decision? ValidateText(string text) => text.Length switch
    {
        0 => Decision.Reject(CommentRejectionCodes.Empty, "Write a comment."),
        > Limits.MaxCommentLength => Decision.Reject(CommentRejectionCodes.TooLong, $"A comment is limited to {Limits.MaxCommentLength} characters."),
        _ => null,
    };

    public static SeasonState Apply(SeasonState state, CommentPosted e) =>
        state with
        {
            Comments = state.Comments.Add(e.CommentId, new Comment(
                e.CommentId, e.Target, e.UserId, e.Text, e.CreatedAt, e.CreatedDay, EditedAt: null, CommentStatus.Visible)),
        };

    public static SeasonState Apply(SeasonState state, CommentEdited e) =>
        state with { Comments = state.Comments.SetItem(e.CommentId, state.Comments[e.CommentId] with { Text = e.Text, EditedAt = e.EditedAt }) };

    public static SeasonState Apply(SeasonState state, CommentDeleted e) =>
        state with { Comments = state.Comments.SetItem(e.CommentId, state.Comments[e.CommentId] with { Status = CommentStatus.Deleted }) };

    public static SeasonState Apply(SeasonState state, CommentHidden e) =>
        state with { Comments = state.Comments.SetItem(e.CommentId, state.Comments[e.CommentId] with { Status = CommentStatus.Hidden }) };
}
