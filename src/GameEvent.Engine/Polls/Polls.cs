using System.Text.Json.Serialization;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Polls;

/// <summary>Who started a poll (D-512): the admin with a <see cref="PollDefinition"/>, or a drawn event's <c>requestChoice.vote</c>.</summary>
public enum PollSource
{
    Admin,
    Event,
}

/// <summary>Where a poll is: open for votes, closed with its result, or cancelled by the deadline (D-516).</summary>
public enum PollStatus
{
    Open,
    Closed,
    Cancelled,
}

/// <summary>What closed a poll (D-514): its time by the timer, or the admin early.</summary>
public enum PollCloseReason
{
    Timer,
    Admin,
}

/// <summary>
/// An option of a poll as it was when the poll started (D-512): <see cref="Id"/> is what a vote names — a category's name,
/// a player's or a game's id in the «N» format, a text of the list; <see cref="Label"/> is what the screen shows.
/// </summary>
public sealed record PollOption(string Id, string Label);

/// <summary>The votes an option got by the close.</summary>
public sealed record PollCount(string OptionId, int Votes);

/// <summary>A user's vote in force (D-513): one per user, the last one counts.</summary>
public sealed record Vote(Guid UserId, string OptionId);

/// <summary>The admin starts a poll (D-512): the definition of CONTENT.md «голосование».</summary>
public sealed record StartPoll(PollDefinition Definition) : ICommand;

/// <summary>
/// A user votes (D-513): a player of the season or a spectator (a user without a player in it). A second vote changes the
/// first while <c>polls.allowChangeVote</c> holds.
/// </summary>
public sealed record CastVote(Guid PollId, Guid UserId, string OptionId) : ICommand;

/// <summary>The admin closes an open poll before its time (D-514).</summary>
public sealed record ClosePoll(Guid PollId) : ICommand;

/// <summary>
/// A poll started (D-512) with the snapshot of its question and options. <see cref="OnResult"/> — the admin's effect of the
/// result (<c>$result</c>). An event's poll names its drawer (<see cref="PlayerId"/>), the suspended effect that waits for it
/// (<see cref="ChoiceId"/>), the event (<see cref="ObjectId"/>, <see cref="Kind"/>) and the player whose games were offered
/// (<see cref="TargetId"/>). Each of those is written only when set.
/// </summary>
[EventType("poll-started")]
public sealed record PollStarted(
    Guid PollId,
    PollSource Source,
    string Question,
    EquatableArray<PollOption> Options,
    PollVoters Voters,
    bool Anonymous,
    bool ShowLiveCounts,
    DateTimeOffset StartedAt,
    DateTimeOffset ClosesAt) : IGameEvent
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectSpec? OnResult { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PlayerId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ChoiceId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObjectId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventKind? Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TargetId { get; init; }
}

/// <summary>
/// A user's first vote (D-513). The user is kept for «one vote» and the undo; an anonymous poll never shows them, and a poll
/// without live counts does not show the option before the close (D-515, D-541) — the flags say so to the feed.
/// </summary>
[EventType("vote-cast")]
public sealed record VoteCast(Guid PollId, Guid UserId, string OptionId, bool Anonymous, bool ShowLiveCounts) : IGameEvent;

/// <summary>A user changed the vote (D-513): the last one counts. In an anonymous poll it is not published (D-541).</summary>
[EventType("vote-changed")]
public sealed record VoteChanged(Guid PollId, Guid UserId, string OptionId, bool Anonymous, bool ShowLiveCounts) : IGameEvent;

/// <summary>
/// A poll closed (D-514): <see cref="Counts"/> — every option in the snapshot's order with its votes; <see cref="Result"/> —
/// the option with the most, null without votes; a tie among <see cref="Tied"/> is broken by the server's roll
/// <see cref="TieRoll"/> (the index among them), written so a replay never rolls again. The result's effect follows in the
/// same command.
/// </summary>
[EventType("poll-closed")]
public sealed record PollClosed(
    Guid PollId, PollCloseReason Reason, string? Result, EquatableArray<PollCount> Counts, EquatableArray<string> Tied, int? TieRoll) : IGameEvent;

/// <summary>A poll whose result would act was cancelled by the deadline (D-516): no result, no effect.</summary>
[EventType("poll-cancelled")]
public sealed record PollCancelled(Guid PollId) : IGameEvent;

/// <summary>
/// A poll of the season (D-512…D-516): its snapshot, its votes in force (one per user, D-513) and how it ended.
/// </summary>
public sealed record Poll(
    Guid PollId,
    PollSource Source,
    string Question,
    EquatableArray<PollOption> Options,
    PollVoters Voters,
    bool Anonymous,
    bool ShowLiveCounts,
    DateTimeOffset StartedAt,
    DateTimeOffset ClosesAt,
    PollStatus Status,
    EquatableArray<Vote> Votes)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectSpec? OnResult { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PlayerId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ChoiceId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObjectId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventKind? Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TargetId { get; init; }

    /// <summary>The option that won; null while open, without votes or cancelled.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Result { get; init; }

    /// <summary>
    /// Whether the result acts on players (D-513, D-516): the admin's effect or an event that waits for it. The first finisher
    /// does not vote in it, and the deadline cancels it.
    /// </summary>
    [JsonIgnore]
    public bool HasEffect => OnResult is not null || Source == PollSource.Event;

    /// <summary>The votes per option in the snapshot's order (0 included).</summary>
    public EquatableArray<PollCount> Counts() =>
        [.. Options.Select(o => new PollCount(o.Id, Votes.Count(v => string.Equals(v.OptionId, o.Id, StringComparison.Ordinal))))];
}
