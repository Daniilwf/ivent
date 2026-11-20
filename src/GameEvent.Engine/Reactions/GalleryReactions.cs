using GameEvent.Engine.Gallery;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Reactions;

public sealed record SetGalleryReaction(Guid ItemId, Guid UserId, string Emoji, bool Selected) : ICommand;

public sealed record GalleryReaction(Guid ItemId, Guid UserId, string Emoji);

[EventType(GalleryReactionSet.Type)]
public sealed record GalleryReactionSet(Guid ItemId, Guid UserId, string Emoji, bool Selected) : IGameEvent
{
    public const string Type = "gallery-reaction-set";
}

internal static class GalleryReactions
{
    public static Decision Decide(SeasonState state, SetGalleryReaction command)
    {
        if (!state.IsCreated || !state.Rules.Features.Gallery || !state.Rules.Features.Reactions)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Gallery reactions are disabled in this season.");
        }

        if (!state.GalleryItems.TryGetValue(command.ItemId, out var item) || item.Hidden)
        {
            return Decision.Reject(GalleryRejectionCodes.TargetUnknown, "There is no visible gallery moment with this id.");
        }

        if (command.UserId == Guid.Empty)
        {
            return Decision.Reject(RejectionCodes.GalleryReactionInvalidUser, "A reaction needs a signed-in account.");
        }

        if (!state.Rules.Social.Reactions.Contains(command.Emoji, StringComparer.Ordinal))
        {
            return Decision.Reject(RejectionCodes.GalleryReactionInvalidEmoji, "This emoji is not enabled in this season.");
        }

        return Decision.Accept(new GalleryReactionSet(command.ItemId, command.UserId, command.Emoji, command.Selected));
    }

    public static SeasonState Apply(SeasonState state, GalleryReactionSet e)
    {
        var reaction = new GalleryReaction(e.ItemId, e.UserId, e.Emoji);
        return state with { GalleryReactions = e.Selected ? state.GalleryReactions.Add(reaction) : state.GalleryReactions.Remove(reaction) };
    }
}
