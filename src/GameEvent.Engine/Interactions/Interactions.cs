using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Interactions;

/// <summary>What an invitation is about (SPEC «Модель данных» Interaction, D-523): a coop now, a duel with task 5.12.</summary>
public enum InteractionKind
{
    Coop,
    Duel,
}

/// <summary>Where an invitation stands (GLOSSARY «Ответ на приглашение»).</summary>
public enum InteractionStatus
{
    Pending,
    Accepted,
    Declined,
    Withdrawn,
    Expired,
}

/// <summary>
/// Why a pending invitation was closed without an answer: its time ran out (<c>interactions.inviteTtlHours</c>), the season's
/// deadline came (D-516), the run it invites to ended (completed, dropped, tech-rerolled) before an answer, or another
/// invitation to the run was accepted (D-523).
/// </summary>
public enum InteractionExpiry
{
    Timeout,
    Deadline,
    RunEnded,

    /// <summary>Another invitation to the same run was accepted (D-523).</summary>
    Superseded,

    /// <summary>A player of a duel's challenge was marked inactive (D-525, D-730).</summary>
    Inactive,

    /// <summary>A player of a duel's challenge became the first finisher, who plays out of the race (D-510, D-730).</summary>
    FirstFinisher,

    /// <summary>A response of the partner board closed with its request: by its author or its term (D-742).</summary>
    RequestClosed,

    /// <summary>A player of a duel's challenge deleted his account (D-734).</summary>
    AccountDeleted,
}

/// <summary>
/// An invitation from <see cref="FromPlayerId"/> to <see cref="ToPlayerId"/> (D-523). A coop invitation names the inviter's
/// running coop run (<see cref="RunId"/>); it lives until <see cref="ExpiresAt"/>.
/// </summary>
public sealed record Interaction(
    Guid InteractionId,
    InteractionKind Kind,
    Guid FromPlayerId,
    Guid ToPlayerId,
    Guid? RunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    InteractionStatus Status = InteractionStatus.Pending);

/// <summary>
/// The roller invites <see cref="PartnerId"/> to his running run of a coop game (D-520): the partner joins it on accepting.
/// </summary>
public sealed record InviteToCoop(Guid PlayerId, Guid PartnerId) : ICommand;

/// <summary>The invited player accepts or declines an invitation (D-523); an accept checks everything again now.</summary>
public sealed record AnswerInvitation(Guid PlayerId, Guid InteractionId, bool Accept) : ICommand;

/// <summary>The inviter takes back his pending invitation (D-523).</summary>
public sealed record WithdrawInvitation(Guid PlayerId, Guid InteractionId) : ICommand;

/// <summary>The partner leaves the coop run before its completion, without a penalty (D-522).</summary>
public sealed record LeaveCoop(Guid PlayerId) : ICommand;

/// <summary>An invitation was sent.</summary>
[EventType("interaction-created")]
public sealed record InteractionCreated(Interaction Interaction) : IGameEvent;

/// <summary>The invited player answered: <see cref="Accepted"/> or declined.</summary>
[EventType("interaction-answered")]
public sealed record InteractionAnswered(Guid InteractionId, Guid PlayerId, bool Accepted, DateTimeOffset AnsweredAt) : IGameEvent;

/// <summary>The inviter took his invitation back.</summary>
[EventType("interaction-withdrawn")]
public sealed record InteractionWithdrawn(Guid InteractionId, Guid PlayerId, DateTimeOffset WithdrawnAt) : IGameEvent;

/// <summary>A pending invitation closed without an answer, for <see cref="Reason"/>.</summary>
[EventType("interaction-expired")]
public sealed record InteractionExpired(Guid InteractionId, InteractionExpiry Reason, DateTimeOffset ExpiredAt) : IGameEvent;

/// <summary>
/// <see cref="PlayerId"/> became the partner of the run <see cref="RunId"/> of <see cref="RollerId"/> by the invitation
/// <see cref="InteractionId"/> (D-520): his turn phase is «playing» this run.
/// </summary>
[EventType("coop-joined")]
public sealed record CoopJoined(Guid RunId, Guid PlayerId, Guid RollerId, Guid InteractionId, DateTimeOffset JoinedAt) : IGameEvent;

/// <summary>Why a partner is no longer in the coop run before its completion (D-522, the expert panel on D-520).</summary>
public enum CoopLeaveReason
{
    /// <summary>The partner left (<see cref="LeaveCoop"/>).</summary>
    Left,

    /// <summary>The roller dropped the run.</summary>
    RollerDropped,

    /// <summary>The roller tech-rerolled the run.</summary>
    RollerTechRerolled,

    /// <summary>The admin marked the partner inactive.</summary>
    PartnerInactive,

    /// <summary>The roller completed the run as the first finisher, who does not affect others (D-510, Н-18).</summary>
    RollerFirst,

    /// <summary>The partner was the first finisher when the run was completed (a revoke of another's finish, D-99).</summary>
    PartnerFirst,
}

/// <summary>
/// The partner <see cref="PlayerId"/> is out of the coop run <see cref="RunId"/>: free, without a penalty and without an
/// exclusion of the game (D-522); the run goes on as the roller's own.
/// </summary>
[EventType("coop-left")]
public sealed record CoopLeft(Guid RunId, Guid PlayerId, CoopLeaveReason Reason, DateTimeOffset LeftAt) : IGameEvent;

/// <summary>
/// The completion of a coop run is split (D-521): of <see cref="Total"/> points and steps the roller <see cref="PlayerId"/>
/// takes <see cref="RollerPoints"/> = ⌈total × <see cref="PointsShare"/>⌉, the partner the rest; of <see cref="Coins"/>
/// completion coins the roller ⌈coins × <see cref="CoinsShare"/>⌉, the partner the rest. The shares are written as they
/// were at the completion (<c>reward.coop</c>): every later correction and reject splits by them.
/// </summary>
[EventType("coop-reward-split")]
public sealed record CoopRewardSplit(
    Guid RunId,
    Guid PlayerId,
    Guid PartnerId,
    int Total,
    int RollerPoints,
    int PartnerPoints,
    int Coins,
    int RollerCoins,
    int PartnerCoins,
    decimal PointsShare,
    decimal CoinsShare) : IGameEvent;

/// <summary>The split of D-521: the roller takes ⌈total × share⌉, the partner the rest, so the shares add up to the total.</summary>
public static class CoopShares
{
    public static (int Roller, int Partner) Split(int total, decimal share)
    {
        var roller = (int)Math.Ceiling(total * share);
        return (roller, total - roller);
    }
}

/// <summary>
/// What every kind of invitation shares (D-523): whether its timer runs, so the timer serves a duel invitation (5.12) as it
/// serves a coop one. A duel challenge left pending by duels switched off waits (D-420); a coop invitation burns by its time
/// whatever the flag, since with coop off it can no longer be accepted (D-715).
/// </summary>
public static class InteractionRules
{
    public static bool IsOn(Rulesets.Features features, InteractionKind kind)
    {
        ArgumentNullException.ThrowIfNull(features);
        return kind switch
        {
            InteractionKind.Coop => true,
            InteractionKind.Duel => features.Duels,
            _ => false,
        };
    }
}
