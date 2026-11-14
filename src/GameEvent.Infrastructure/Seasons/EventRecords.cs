using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Suggestions;

namespace GameEvent.Infrastructure.Seasons;

// The projection of the stage 5 mechanics (migration «Events», task 5.16, D-810, D-811): what a page filters or sorts by is
// a column (status, terms, players); the rest of a record is JSON. Columns win over the JSON when the state is read back,
// so a row changed behind the queue's back is a difference of the integrity check (L4).

/// <summary>A poll of the season (SPEC «Модель данных»: Poll); its options and votes in force are rows of their own.</summary>
public sealed class PollRecord
{
    public Guid Id { get; set; }

    public Guid SeasonId { get; set; }

    public PollStatus Status { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset ClosesAt { get; set; }

    public bool Anonymous { get; set; }

    /// <summary>The rest of the poll (<c>Poll</c> without its options and votes) as JSON.</summary>
    public required string DataJson { get; set; }
}

/// <summary>An option of a poll as it was at the start (D-512); <see cref="Position"/> keeps the order.</summary>
public sealed class PollOptionRecord
{
    public Guid PollId { get; set; }

    public int Position { get; set; }

    public required string OptionId { get; set; }

    public required string Label { get; set; }
}

/// <summary>A user's vote in force (D-513): one per poll and user, also in the database (EV40).</summary>
public sealed class VoteRecord
{
    public Guid PollId { get; set; }

    public Guid UserId { get; set; }

    public required string OptionId { get; set; }

    /// <summary>The vote's place among the poll's votes in force.</summary>
    public int Position { get; set; }
}

/// <summary>A global event of the season (D-517, D-518).</summary>
public sealed class GlobalEventRecord
{
    public Guid Id { get; set; }

    public Guid SeasonId { get; set; }

    public required string ObjectId { get; set; }

    public GlobalEventStatus Status { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    public bool Reached { get; set; }

    /// <summary>The rest of it (<c>GlobalEventState</c>: the snapshot, the goal, the contributions in force) as JSON.</summary>
    public required string DataJson { get; set; }
}

/// <summary>An invitation between two players — to coop or to a duel (D-523, D-525).</summary>
public sealed class InteractionRecord
{
    public Guid Id { get; set; }

    public Guid SeasonId { get; set; }

    public InteractionKind Kind { get; set; }

    public Guid FromPlayerId { get; set; }

    public Guid ToPlayerId { get; set; }

    public Guid? RunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public InteractionStatus Status { get; set; }
}

/// <summary>A request of the partner board «ищу напарника» (D-524).</summary>
public sealed class PartnerRequestRecord
{
    public Guid Id { get; set; }

    public Guid SeasonId { get; set; }

    public Guid PlayerId { get; set; }

    public Guid RunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public PartnerRequestStatus Status { get; set; }
}

/// <summary>A weekly challenge of the season (D-526) with its window; its completions in force are rows of their own.</summary>
public sealed class WeeklyChallengeRecord
{
    public Guid Id { get; set; }

    public Guid SeasonId { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    public int RewardCoins { get; set; }

    /// <summary>The challenge's definition as it was set, JSON.</summary>
    public required string DefinitionJson { get; set; }
}

/// <summary>A completion of a weekly challenge in force: the run it stands on and the coins it paid.</summary>
public sealed class WeeklyChallengeCompletionRecord
{
    public Guid ChallengeId { get; set; }

    public Guid PlayerId { get; set; }

    public Guid RunId { get; set; }

    public int Coins { get; set; }

    /// <summary>The completion's place among the challenge's completions.</summary>
    public int Position { get; set; }
}

/// <summary>A suggestion of the season's suggestion box (D-527).</summary>
public sealed class SuggestionRecord
{
    public Guid Id { get; set; }

    public Guid SeasonId { get; set; }

    public Guid PlayerId { get; set; }

    public Guid UserId { get; set; }

    public SuggestionKind Kind { get; set; }

    public required string Text { get; set; }

    /// <summary>The card of a suggested game as JSON; null for an event or an item.</summary>
    public string? GameJson { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }

    public SuggestionStatus Status { get; set; }

    public string? Comment { get; set; }
}
