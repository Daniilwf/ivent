using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Suggestions;

public enum SuggestionKind
{
    Game,
    Event,
    Item,
}

public enum SuggestionStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>
/// A player of the season suggests a game, an event or an item (SPEC «Предложка», D-527): a text, and for a game the fields
/// of its card. At most <c>suggestions.perDay</c> a calendar day of <c>season.timezone</c>.
/// </summary>
public sealed record SubmitSuggestion(Guid PlayerId, SuggestionKind Kind, string Text, GameCard? Game) : ICommand;

/// <summary>
/// The admin approves or rejects a waiting suggestion; a rejection explains itself. An approved game goes to the pool by the
/// pool's own command, issued by the queue from the approved suggestion (D-572).
/// </summary>
public sealed record ReviewSuggestion(Guid SuggestionId, bool Approve, string? Comment) : ICommand;

[EventType("suggestion-submitted")]
public sealed record SuggestionSubmitted(
    Guid SuggestionId, Guid PlayerId, Guid UserId, SuggestionKind Kind, string Text, GameCard? Game, DateTimeOffset SubmittedAt) : IGameEvent;

[EventType("suggestion-reviewed")]
public sealed record SuggestionReviewed(Guid SuggestionId, SuggestionStatus Status, string? Comment, DateTimeOffset ReviewedAt) : IGameEvent;

/// <summary>A suggestion of the season: its author (player and account), what it suggests and how the admin decided.</summary>
public sealed record Suggestion(
    Guid SuggestionId,
    Guid PlayerId,
    Guid UserId,
    SuggestionKind Kind,
    string Text,
    GameCard? Game,
    DateTimeOffset SubmittedAt,
    SuggestionStatus Status,
    string? Comment);

/// <summary>
/// The suggestion box (D-527, D-571). It changes no standing: suggesting and reviewing go on after the deadline, until the
/// results are fixed (D-575).
/// </summary>
internal static class SuggestionBox
{
    public static Decision Decide(SeasonState state, SubmitSuggestion command, EngineContext context)
    {
        if (Guard(state) is { } refused)
        {
            return refused;
        }

        // A spectator has no player in the season (Н-21)
        if (!state.Players.TryGetValue(command.PlayerId, out var player))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.PlayerId} is not in the season.");
        }

        var text = command.Text?.Trim() ?? "";
        if (command.Kind == SuggestionKind.Game ? string.IsNullOrWhiteSpace(command.Game?.Title) : text.Length == 0)
        {
            return command.Kind == SuggestionKind.Game
                ? Decision.Reject(RejectionCodes.SuggestionGameRequired, "A game is suggested with its card and a title.")
                : Decision.Reject(RejectionCodes.SuggestionEmpty, "Say what the event or the item is.");
        }

        if (text.Length > Limits.MaxSuggestionLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"A suggestion is limited to {Limits.MaxSuggestionLength} characters.");
        }

        var now = context.Clock.UtcNow;
        var perDay = state.Rules.Suggestions?.PerDay ?? throw new InvalidOperationException("The validator requires suggestions.");
        var zone = SeasonTime.Zone(state.Rules.Season.Timezone);
        var today = SeasonTime.Day(now, zone);
        if (state.Suggestions.Values.Count(s => s.PlayerId == player.PlayerId && SeasonTime.Day(s.SubmittedAt, zone) == today) >= perDay)
        {
            return Decision.Reject(RejectionCodes.SuggestionDailyLimit, $"At most {perDay} suggestions a day.");
        }

        var game = command.Kind == SuggestionKind.Game ? command.Game! with { Title = command.Game!.Title.Trim() } : null;
        return Decision.Accept(new SuggestionSubmitted(context.Ids.NewId(), player.PlayerId, player.UserId, command.Kind, text, game, now));
    }

    public static Decision Decide(SeasonState state, ReviewSuggestion command, EngineContext context)
    {
        if (Guard(state) is { } refused)
        {
            return refused;
        }

        if (state.Suggestions.GetValueOrDefault(command.SuggestionId) is not { } suggestion)
        {
            return Decision.Reject(RejectionCodes.SuggestionUnknown, $"Suggestion {command.SuggestionId} is not in the season.");
        }

        if (suggestion.Status != SuggestionStatus.Pending)
        {
            return Decision.Reject(RejectionCodes.SuggestionAlreadyReviewed, $"The suggestion is {suggestion.Status} already.");
        }

        var comment = string.IsNullOrWhiteSpace(command.Comment) ? null : command.Comment.Trim();

        // A rejection explains itself, as a rejected proof does (Н-28)
        if (!command.Approve && comment is null)
        {
            return Decision.Reject(RejectionCodes.CommentRequired, "A rejection says why.");
        }

        if (comment?.Length > Limits.MaxCommentLength)
        {
            return Decision.Reject(RejectionCodes.CommentTooLong, $"The comment is limited to {Limits.MaxCommentLength} characters.");
        }

        return Decision.Accept(new SuggestionReviewed(
            suggestion.SuggestionId, command.Approve ? SuggestionStatus.Approved : SuggestionStatus.Rejected, comment, context.Clock.UtcNow));
    }

    private static Decision? Guard(SeasonState state)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (SeasonSetup.IsOver(state))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: the suggestion box is closed.");
        }

        return state.Rules.Features.Suggestions
            ? null
            : Decision.Reject(RejectionCodes.FeatureDisabled, "The suggestion box is off in this season.");
    }

    public static SeasonState Apply(SeasonState state, SuggestionSubmitted e) =>
        state with
        {
            Suggestions = state.Suggestions.Add(
                e.SuggestionId, new Suggestion(e.SuggestionId, e.PlayerId, e.UserId, e.Kind, e.Text, e.Game, e.SubmittedAt, SuggestionStatus.Pending, null)),
        };

    public static SeasonState Apply(SeasonState state, SuggestionReviewed e) =>
        state with
        {
            Suggestions = state.Suggestions.SetItem(
                e.SuggestionId, state.Suggestions[e.SuggestionId] with { Status = e.Status, Comment = e.Comment }),
        };
}

/// <summary>The season's calendar (Н-20): days by <c>season.timezone</c>; an unknown zone counts in UTC (D-575).</summary>
internal static class SeasonTime
{
    public static TimeZoneInfo Zone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    public static DateOnly Day(DateTimeOffset moment, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, zone).DateTime);
}
