using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Gallery;

/// <summary>A player's screenshot. The prepared file id is supplied separately by the queue.</summary>
public sealed record PublishGalleryMoment(Guid ItemId, Guid UserId, string? Caption, string ImageSha256) : ICommand
{
    /// <summary>The same caption always contributes the same value to the command fingerprint.</summary>
    public string? Caption { get; } = Caption?.Trim() ?? "";

    /// <summary>The original image's checksum, independent of the id assigned to a prepared copy.</summary>
    public string ImageSha256 { get; } = ImageSha256?.ToUpperInvariant() ?? "";
}

/// <summary>Moderation by an administrator authorized at the command boundary.</summary>
public sealed record HideGalleryMoment(Guid ItemId) : ICommand;

/// <summary>A posted screenshot and its date at publication, retained when hidden.</summary>
public sealed record GalleryItem(
    Guid ItemId,
    Guid UserId,
    Guid PlayerId,
    Guid FileId,
    string Caption,
    DateTimeOffset CreatedAt,
    DateOnly CreatedDay,
    bool Hidden);

/// <summary>The item before an undone command, or null when that command published it.</summary>
public sealed record GalleryItemSnapshot(Guid ItemId, GalleryItem? Before);

[EventType(GalleryMomentPublished.Type)]
public sealed record GalleryMomentPublished(
    Guid ItemId,
    Guid UserId,
    Guid PlayerId,
    Guid FileId,
    string Caption,
    DateTimeOffset CreatedAt,
    DateOnly CreatedDay) : IGameEvent
{
    public const string Type = "gallery-moment-published";
}

[EventType(GalleryMomentHidden.Type)]
public sealed record GalleryMomentHidden(Guid ItemId) : IGameEvent
{
    public const string Type = "gallery-moment-hidden";
}

public static class GalleryRejectionCodes
{
    public const string NotParticipant = "gallery.notParticipant";
    public const string TargetUnknown = "gallery.targetUnknown";
    public const string ItemAlreadyExists = "gallery.itemAlreadyExists";
    public const string InvalidItemId = "gallery.invalidItemId";
    public const string InvalidImage = "gallery.invalidImage";
    public const string CaptionRequired = "gallery.captionRequired";
    public const string CaptionTooLong = "gallery.captionTooLong";
    public const string DailyLimit = "gallery.dailyLimit";
}

internal static class GalleryMoments
{
    public static Decision Decide(SeasonState state, PublishGalleryMoment command, EngineContext context)
    {
        if (!IsEnabled(state))
        {
            return Disabled();
        }

        if (SeasonSetup.IsOver(state))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, "The season is closed to new gallery moments.");
        }

        if (state.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonNotActive, "The season has not started.");
        }

        var player = state.Players.Values.FirstOrDefault(player => player.UserId == command.UserId && !player.IsInactive);
        if (player is null)
        {
            return Decision.Reject(GalleryRejectionCodes.NotParticipant, "Only a current participant can publish a gallery moment.");
        }

        if (command.ItemId == Guid.Empty)
        {
            return Decision.Reject(GalleryRejectionCodes.InvalidItemId, "A gallery moment needs an id.");
        }

        if (state.GalleryItems.ContainsKey(command.ItemId))
        {
            return Decision.Reject(GalleryRejectionCodes.ItemAlreadyExists, "This gallery moment id has already been used.");
        }

        var caption = command.Caption ?? "";
        if (caption.Length == 0)
        {
            return Decision.Reject(GalleryRejectionCodes.CaptionRequired, "A gallery moment needs a caption.");
        }

        if (caption.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(GalleryRejectionCodes.CaptionTooLong, $"A gallery caption is limited to {Limits.MaxCommentLength} characters.");
        }

        if (context.PreparedGalleryFileId is not { } fileId || fileId == Guid.Empty
            || command.ImageSha256.Length != 64 || !command.ImageSha256.All(char.IsAsciiHexDigit)
            || state.GalleryItems.Values.Any(item => item.FileId == fileId))
        {
            return Decision.Reject(GalleryRejectionCodes.InvalidImage, "A gallery moment needs its own prepared image.");
        }

        var now = context.Clock.UtcNow;
        var timezone = state.Rules.Season.Timezone;
        if (SeasonCalendar.Day(context.TimeZones, timezone, now) is not { } day)
        {
            return Decision.Reject(RejectionCodes.SeasonTimezoneUnknown, $"The season's time zone '{timezone}' is not known here.");
        }

        var limit = state.Rules.Social.GalleryUploadsPerDay;
        if (state.GalleryItems.Values.Count(item => item.UserId == command.UserId
            && SeasonCalendar.Day(context.TimeZones, timezone, item.CreatedAt) == day) >= limit)
        {
            return Decision.Reject(GalleryRejectionCodes.DailyLimit, "This account has reached the daily gallery upload limit.");
        }

        return Decision.Accept(new GalleryMomentPublished(command.ItemId, command.UserId, player.PlayerId, fileId, caption, now, day));
    }

    public static Decision Decide(SeasonState state, HideGalleryMoment command)
    {
        if (!IsEnabled(state))
        {
            return Disabled();
        }

        return state.GalleryItems.TryGetValue(command.ItemId, out var item) && !item.Hidden
            ? Decision.Accept(new GalleryMomentHidden(command.ItemId))
            : Decision.Reject(GalleryRejectionCodes.TargetUnknown, "There is no visible gallery moment with this id.");
    }

    public static SeasonState Apply(SeasonState state, GalleryMomentPublished e) =>
        state with
        {
            GalleryItems = state.GalleryItems.Add(e.ItemId, new GalleryItem(
                e.ItemId, e.UserId, e.PlayerId, e.FileId, e.Caption, e.CreatedAt, e.CreatedDay, Hidden: false)),
        };

    public static SeasonState Apply(SeasonState state, GalleryMomentHidden e) =>
        state with { GalleryItems = state.GalleryItems.SetItem(e.ItemId, state.GalleryItems[e.ItemId] with { Hidden = true }) };

    private static bool IsEnabled(SeasonState state) => state.IsCreated && state.Rules.Features.Gallery;

    private static Decision Disabled() => Decision.Reject(RejectionCodes.FeatureDisabled, "The gallery is disabled in this season.");
}
