using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Accounts;

/// <summary>An account's role as the global log records it (GLOSSARY «Роль»).</summary>
public enum AccountRole
{
    Player,
    Admin,
    Spectator,
}

// The global log's account events (D-106): an audit trail of who changed which account. Passwords and their hashes are
// secrets and never enter the log; the account table is where they live.

/// <summary>The admin created an account; <see cref="MustChangePassword"/> — it signs in with a temporary password.</summary>
[EventType("account-created")]
public sealed record AccountCreated(Guid UserId, string Login, string Name, AccountRole Role, bool MustChangePassword) : IGameEvent;

/// <summary>The admin gave the account a new temporary password; it must be changed at the next sign-in.</summary>
[EventType("account-password-reset")]
public sealed record AccountPasswordReset(Guid UserId) : IGameEvent;

/// <summary>The account's owner changed their password.</summary>
[EventType("account-password-changed")]
public sealed record AccountPasswordChanged(Guid UserId) : IGameEvent;

/// <summary>The admin renamed the account or changed its role.</summary>
[EventType("account-changed")]
public sealed record AccountChanged(Guid UserId, string Name, AccountRole Role) : IGameEvent;

/// <summary>The admin deleted the account (soft): it no longer signs in; its seasons keep its name.</summary>
[EventType("account-deleted")]
public sealed record AccountDeleted(Guid UserId) : IGameEvent;

/// <summary>The admin restored a deleted account; it signs in again after a password reset.</summary>
[EventType("account-restored")]
public sealed record AccountRestored(Guid UserId) : IGameEvent;
