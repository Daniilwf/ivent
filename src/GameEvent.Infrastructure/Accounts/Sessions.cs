using System.Security.Cryptography;
using GameEvent.Engine.Accounts;

namespace GameEvent.Infrastructure.Accounts;

/// <summary>
/// A sign-in on one device (D-466): the cookie names it, every request checks it is still there, not ended and not
/// expired. Ending it signs that device out at once, even a copy of its cookie.
/// </summary>
public sealed class SessionRecord
{
    /// <summary>128 random bits from the system's cryptographic generator: the cookie carries it, sealed by the site.</summary>
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The last request, kept to within <see cref="SessionRules.TouchInterval"/>: not a write per request.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>The browser's own description at the sign-in, cut to <see cref="SessionRules.MaxUserAgentLength"/>.</summary>
    public string? UserAgent { get; set; }

    /// <summary>The client's address at the sign-in, an IPv6 one by its /64 network (as the login limit counts it).</summary>
    public string? Address { get; set; }

    /// <summary>A sliding end: every touch moves it to <see cref="SessionRules.Lifetime"/> after that touch.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the owner, a password change or the admin ended it; null while it lives.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>
/// The account signs in on a device: a new session; the queue gives it its id (in <see cref="SessionStarted"/>). With
/// <see cref="MaxLive"/> live sessions already, the least recently seen end first (SES-2).
/// </summary>
public sealed record StartSession(Guid UserId, string? UserAgent, string? Address, int MaxLive = SessionRules.DefaultMaxLive)
    : IAccountCommand, IOpenInMaintenance;

/// <summary>
/// A request of a live session after <see cref="SessionRules.TouchInterval"/>: its last request and its sliding end move.
/// Bookkeeping, not an event: the log keeps who signed in and out, not every visit.
/// </summary>
public sealed record TouchSession(Guid SessionId) : IAccountCommand, IOpenInMaintenance;

/// <summary>
/// Ends sessions of the account: the named ones (<see cref="Only"/>), or all but <see cref="Keep"/>, or all. Only live
/// sessions of that account are ended; none to end is not an error, nothing is written.
/// </summary>
public sealed record EndSessions(Guid UserId, SessionEndReason Reason, Guid? Only = null, Guid? Keep = null) : IAccountCommand, IOpenInMaintenance;

/// <summary>The scheduler drops the rows of ended and expired sessions (D-469): the log keeps their start and end.</summary>
public sealed record PurgeSessions : IAccountCommand;

/// <summary>Commands the queue takes while the site only reads (D-121, D-468): signing in and out stays open.</summary>
public interface IOpenInMaintenance;

public static class SessionRules
{
    /// <summary>The cookie's life and the session's sliding end: 30 days after the last visit (D-26).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>A live session's last visit is written at most this often.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    /// <summary>The rejection of a command naming a session that is not live, or not this account's.</summary>
    public const string Unknown = "session.unknown";

    /// <summary>
    /// Live sessions an account keeps (<c>Security:MaxSessionsPerAccount</c>): a security limit, not a game number, so it
    /// is the site's setting, not the ruleset's (SES-2).
    /// </summary>
    public const int DefaultMaxLive = 20;

    public const int MaxUserAgentLength = 256;
    public const int MaxAddressLength = 64;

    /// <summary>A session id: 128 bits from the cryptographic generator, unlike the time-ordered ids of rows.</summary>
    public static Guid NewId() => new(RandomNumberGenerator.GetBytes(16));
}
