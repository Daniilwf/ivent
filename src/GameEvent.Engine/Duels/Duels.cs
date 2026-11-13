using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Duels;

// Duels (SPEC «Добровольные взаимодействия», D-523, D-525, the expert panel, D-540, D-730…D-739). The challenge is an
// invitation of kind Duel (Interactions, D-718): sent by ChallengeToDuel, answered by AnswerInvitation, withdrawn by
// WithdrawInvitation, burned by the invitations' timer. Its accept starts the race, which lives here.

/// <summary>Where an accepted duel stands: the race goes on, or how it ended.</summary>
public enum DuelStatus
{
    /// <summary>The race goes on until <see cref="Duel.ExpiresAt"/>.</summary>
    Active,

    /// <summary>Won by <see cref="Duel.WinnerId"/>.</summary>
    Won,

    /// <summary>Nobody won: the time ran out, the deadline came, or the winner's proof was rejected.</summary>
    Drawn,

    /// <summary>Called off without a payout: nobody's fault (a tech reroll), or a player left the race (inactive, the first finisher).</summary>
    Cancelled,
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
/// An accepted duel between <see cref="ChallengerId"/> and <see cref="OpponentId"/> (D-525); its id is the id of the
/// challenge it was accepted from. <see cref="ExpiresAt"/> — the race's end (<see cref="AcceptedAt"/> +
/// <c>duels.durationDays</c>): the challenge's own time is the invitation's. <see cref="RunsAtAcceptance"/> — the runs the
/// two were playing when the duel was accepted: they never count (D-731). A won duel holds the winner, the winning run
/// (none for a forfeit) and the coins it paid; <see cref="EndedAt"/> starts the pair's cooldown.
/// </summary>
public sealed record Duel(
    Guid DuelId,
    Guid ChallengerId,
    Guid OpponentId,
    DateTimeOffset AcceptedAt,
    DateTimeOffset ExpiresAt,
    EquatableArray<Guid> RunsAtAcceptance,
    DuelStatus Status = DuelStatus.Active,
    Guid? WinnerId = null,
    Guid? RunId = null,
    int Coins = 0,
    DuelEnd? End = null,
    DateTimeOffset? EndedAt = null)
{
    public bool Involves(Guid playerId) => ChallengerId == playerId || OpponentId == playerId;

    /// <summary>The other player of the duel.</summary>
    public Guid Other(Guid playerId) => playerId == ChallengerId ? OpponentId : ChallengerId;
}

/// <summary>
/// <see cref="PlayerId"/> challenges <see cref="OpponentId"/> to a duel (D-525): an invitation of kind Duel. Whoever of
/// the two first completes a run that counts within <c>duels.durationDays</c> of the acceptance wins
/// <c>duels.rewardCoins</c> from the system.
/// </summary>
public sealed record ChallengeToDuel(Guid PlayerId, Guid OpponentId) : ICommand;

/// <summary>
/// The challenge <see cref="DuelId"/> was accepted (it follows its <c>interaction-answered</c>): the race ends at
/// <see cref="ExpiresAt"/>, and the runs the two were playing then (<see cref="RunsAtAcceptance"/>) never count (D-731).
/// </summary>
[EventType("duel-started")]
public sealed record DuelStarted(
    Guid DuelId, Guid ChallengerId, Guid OpponentId, DateTimeOffset AcceptedAt, DateTimeOffset ExpiresAt, EquatableArray<Guid> RunsAtAcceptance) : IGameEvent;

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
