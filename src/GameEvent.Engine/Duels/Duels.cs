using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Duels;

// Duels (SPEC «Добровольные взаимодействия», D-523, D-525, the expert panel, D-540, D-730…D-739). A duel's invitation
// lives here on its own until task 5.10's Interaction lands: D-730 says how the two are joined.

/// <summary>Where a duel stands: a challenge waiting for its answer, closed without a duel, a race going on, or its end.</summary>
public enum DuelStatus
{
    /// <summary>The challenge waits for the opponent's answer until <see cref="Duel.ExpiresAt"/>.</summary>
    Pending,

    /// <summary>The opponent declined the challenge.</summary>
    Declined,

    /// <summary>The challenger took the challenge back.</summary>
    Withdrawn,

    /// <summary>The challenge closed without an answer (<see cref="DuelExpiry"/>).</summary>
    Expired,

    /// <summary>Accepted: the race goes on until <see cref="Duel.ExpiresAt"/>.</summary>
    Active,

    /// <summary>Won by <see cref="Duel.WinnerId"/>.</summary>
    Won,

    /// <summary>Nobody won: the time ran out, the deadline came, or the winner's proof was rejected.</summary>
    Drawn,

    /// <summary>Called off without a payout: nobody's fault (a tech reroll), or a player left the race (inactive, the first finisher).</summary>
    Cancelled,
}

/// <summary>Why a challenge closed without an answer.</summary>
public enum DuelExpiry
{
    /// <summary><c>interactions.inviteTtlHours</c> passed (the timer).</summary>
    Timeout,

    /// <summary>The season's deadline came (D-516).</summary>
    Deadline,

    /// <summary>One of the two was marked inactive.</summary>
    Inactive,

    /// <summary>One of the two became the first finisher, who plays out of the race (D-510).</summary>
    FirstFinisher,
}

/// <summary>How an accepted duel ended.</summary>
public enum DuelEnd
{
    /// <summary>A run that counts was completed in time: its player won (<see cref="DuelStatus.Won"/>).</summary>
    Completed,

    /// <summary>The other player dropped a run that counts (<c>duels.dropForfeits</c>): this one won (<see cref="DuelStatus.Won"/>).</summary>
    Forfeit,

    /// <summary><c>duels.durationDays</c> passed without a win (<see cref="DuelStatus.Drawn"/>).</summary>
    TimeOut,

    /// <summary>The season's deadline came first (<see cref="DuelStatus.Drawn"/>, D-516).</summary>
    Deadline,

    /// <summary>The winning run's proof was rejected: the win is gone and does not pass on (<see cref="DuelStatus.Drawn"/>).</summary>
    WinRejected,

    /// <summary>A run that counts was tech-rerolled: nobody's fault (<see cref="DuelStatus.Cancelled"/>).</summary>
    TechReroll,

    /// <summary>One of the two was marked inactive (<see cref="DuelStatus.Cancelled"/>).</summary>
    Inactive,

    /// <summary>One of the two became the first finisher (<see cref="DuelStatus.Cancelled"/>, D-510).</summary>
    FirstFinisher,
}

/// <summary>
/// A duel between <see cref="ChallengerId"/> and <see cref="OpponentId"/> (D-525). <see cref="ExpiresAt"/> holds the
/// challenge's time first (<c>interactions.inviteTtlHours</c>), then the race's end (<see cref="AcceptedAt"/> +
/// <c>duels.durationDays</c>), as the expert panel says. <see cref="RunsAtAcceptance"/> — the runs the two were playing when
/// the duel was accepted: they never count (D-731). A won duel holds the winner, the winning run (none for a forfeit) and
/// the coins it paid; <see cref="EndedAt"/> starts the pair's cooldown.
/// </summary>
public sealed record Duel(
    Guid DuelId,
    Guid ChallengerId,
    Guid OpponentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DuelStatus Status = DuelStatus.Pending,
    DateTimeOffset? AcceptedAt = null,
    EquatableArray<Guid> RunsAtAcceptance = default,
    Guid? WinnerId = null,
    Guid? RunId = null,
    int Coins = 0,
    DuelEnd? End = null,
    DateTimeOffset? EndedAt = null)
{
    public bool Involves(Guid playerId) => ChallengerId == playerId || OpponentId == playerId;

    /// <summary>The other player of the duel.</summary>
    public Guid Other(Guid playerId) => playerId == ChallengerId ? OpponentId : ChallengerId;

    /// <summary>A challenge waiting or a race going on.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOpen => Status is DuelStatus.Pending or DuelStatus.Active;
}

/// <summary>
/// <see cref="PlayerId"/> challenges <see cref="OpponentId"/> to a duel (D-525): whoever of the two first completes a run
/// that counts within <c>duels.durationDays</c> of the acceptance wins <c>duels.rewardCoins</c> from the system.
/// </summary>
public sealed record ChallengeToDuel(Guid PlayerId, Guid OpponentId) : ICommand;

/// <summary>The challenged player accepts or declines (D-523); an accept checks everything again now.</summary>
public sealed record AnswerDuel(Guid PlayerId, Guid DuelId, bool Accept) : ICommand;

/// <summary>The challenger takes back his challenge while it waits (D-523).</summary>
public sealed record WithdrawDuel(Guid PlayerId, Guid DuelId) : ICommand;

/// <summary>A challenge was sent.</summary>
[EventType("duel-challenged")]
public sealed record DuelChallenged(Duel Duel) : IGameEvent;

/// <summary>
/// The challenged player answered. An accept starts the race: it ends at <see cref="ExpiresAt"/>, and the runs the two
/// were playing then (<see cref="RunsAtAcceptance"/>) never count (D-731). A decline has neither.
/// </summary>
[EventType("duel-answered")]
public sealed record DuelAnswered(
    Guid DuelId, bool Accepted, DateTimeOffset AnsweredAt, DateTimeOffset? ExpiresAt, EquatableArray<Guid> RunsAtAcceptance) : IGameEvent;

/// <summary>The challenger took his challenge back.</summary>
[EventType("duel-withdrawn")]
public sealed record DuelWithdrawn(Guid DuelId, DateTimeOffset WithdrawnAt) : IGameEvent;

/// <summary>A challenge closed without an answer, for <see cref="Reason"/>.</summary>
[EventType("duel-expired")]
public sealed record DuelExpired(Guid DuelId, DuelExpiry Reason, DateTimeOffset ExpiredAt) : IGameEvent;

/// <summary>
/// An accepted duel ended (D-525): <see cref="Status"/> won, drawn or cancelled, for <see cref="End"/>. A win names the
/// winner, the winning run (none for a forfeit) and the <see cref="Coins"/> it paid — 0 for the first finisher (D-414,
/// D-422) and past <c>duels.maxWinsPerSeason</c>; the coins follow as their own change.
/// </summary>
[EventType("duel-settled")]
public sealed record DuelSettled(
    Guid DuelId, DuelStatus Status, DuelEnd End, Guid? WinnerId, Guid? RunId, int Coins, DateTimeOffset SettledAt) : IGameEvent;

/// <summary>
/// The winning run's proof was rejected (the expert panel (5), like a bet, D-414): the duel is a draw, the win does not pass
/// to the other player, <see cref="Coins"/> of what it paid are taken back (0 for the frozen first finisher, D-422).
/// </summary>
[EventType("duel-win-revoked")]
public sealed record DuelWinRevoked(Guid DuelId, Guid WinnerId, Guid RunId, int Coins) : IGameEvent;
