using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Reactions;

public sealed record SetFeedReaction(Guid TargetCommandId, Guid UserId, string Emoji, bool Selected) : ICommand;

public sealed record FeedReaction(Guid TargetCommandId, Guid UserId, string Emoji);

[EventType(FeedReactionSet.Type)]
public sealed record FeedReactionSet(Guid TargetCommandId, Guid UserId, string Emoji, bool Selected) : IGameEvent
{
    public const string Type = "feed-reaction-set";
}

internal static class FeedReactions
{
    public static Decision Decide(SeasonState state, SetFeedReaction command, EngineContext context)
    {
        if (!state.IsCreated || !state.Rules.Features.Reactions)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Feed reactions are disabled in this season.");
        }

        if (!context.FeedReactionTargetAvailable)
        {
            return Decision.Reject(RejectionCodes.FeedReactionTargetUnknown, "There is no available feed row for this command.");
        }

        if (command.UserId == Guid.Empty)
        {
            return Decision.Reject(RejectionCodes.FeedReactionInvalidUser, "A reaction needs a signed-in account.");
        }

        if (!state.Rules.Social.Reactions.Contains(command.Emoji, StringComparer.Ordinal))
        {
            return Decision.Reject(RejectionCodes.FeedReactionInvalidEmoji, "This emoji is not enabled in this season.");
        }

        // Even a confirmation owns its command id and prevents an earlier choice from being undone over it.
        return Decision.Accept(new FeedReactionSet(command.TargetCommandId, command.UserId, command.Emoji, command.Selected));
    }

    public static SeasonState Apply(SeasonState state, FeedReactionSet e)
    {
        var reaction = new FeedReaction(e.TargetCommandId, e.UserId, e.Emoji);
        return state with { FeedReactions = e.Selected ? state.FeedReactions.Add(reaction) : state.FeedReactions.Remove(reaction) };
    }
}
