using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Reactions;

public sealed record SetReviewReaction(Guid RunId, Guid UserId, string Emoji, bool Selected) : ICommand;

public sealed record ReviewReaction(Guid RunId, Guid UserId, string Emoji);

[EventType(ReviewReactionSet.Type)]
public sealed record ReviewReactionSet(Guid RunId, Guid UserId, string Emoji, bool Selected) : IGameEvent
{
    public const string Type = "review-reaction-set";
}

internal static class ReviewReactions
{
    public static Decision Decide(SeasonState state, SetReviewReaction command, EngineContext context)
    {
        if (!state.IsCreated || !state.Rules.Features.Reactions)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Review reactions are disabled in this season.");
        }

        if (!state.Runs.TryGetValue(command.RunId, out var run) || run.Review is null
            || context.Pool.Games.Any(g => g.Id == run.GameId && g.IsDeleted))
        {
            return Decision.Reject(RejectionCodes.ReviewReactionReviewUnknown, "There is no review for this run.");
        }

        if (command.UserId == Guid.Empty)
        {
            return Decision.Reject(RejectionCodes.ReviewReactionInvalidUser, "A reaction needs a signed-in account.");
        }

        if (!state.Rules.Social.Reactions.Contains(command.Emoji, StringComparer.Ordinal))
        {
            return Decision.Reject(RejectionCodes.ReviewReactionInvalidEmoji, "This emoji is not enabled in this season.");
        }

        // Keep the result even when the set already has the desired membership: a delayed retry must not undo a newer choice.
        return Decision.Accept(new ReviewReactionSet(command.RunId, command.UserId, command.Emoji, command.Selected));
    }

    public static SeasonState Apply(SeasonState state, ReviewReactionSet e)
    {
        var reaction = new ReviewReaction(e.RunId, e.UserId, e.Emoji);
        return state with { ReviewReactions = e.Selected ? state.ReviewReactions.Add(reaction) : state.ReviewReactions.Remove(reaction) };
    }
}
