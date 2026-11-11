using GameEvent.Engine.Accounts;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Queue;

/// <summary>
/// Global commands — accounts (D-106) and files (D-108): the same queue, one transaction each, events in the global log
/// (no season); the account rules are here, the file rules in CommandProcessor.Files. The account
/// table holds what the log must not — password hashes — so the rules read it, and the log is the audit trail.
/// </summary>
public sealed partial class CommandProcessor
{
    private async Task<CommandOutcome> ProcessGlobalAsync(
        GameEventDbContext db, CommandEnvelope envelope, IGlobalCommand command, string commandType, string commandHash, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var (events, apply, secret, rejection) = command switch
        {
            StartSession or TouchSession or EndSessions or PurgeSessions => await DecideSessionAsync(db, command, SessionNow, ct),
            IAccountCommand account => await DecideAccountAsync(db, account, now, ct),
            Files.RecordFile file => await DecideFileAsync(db, file, now, ct),
            Pool.AddGame or Pool.ChangeGame or Pool.DeleteGame or Pool.RestoreGame or Pool.SetCategory or Pool.RemoveCategory
                => await DecidePoolAsync(db, command, now, ct),
            BugReports.ReportBug or BugReports.SetBugReportStatus => await DecideBugReportAsync(db, command, now, ct),
            _ => throw new InvalidOperationException($"No rules for the global command {commandType}."),
        };
        if (rejection is not null)
        {
            return new CommandOutcome(false, false, rejection, []);
        }

        var sequence = await db.Events.Where(e => e.SeasonId == Guid.Empty).MaxAsync(e => (long?)e.Sequence, ct) ?? 0;
        var records = events.Select(e =>
        {
            var stored = EventCodec.Encode(e);
            return new GameEventRecord
            {
                SeasonId = Guid.Empty,
                Sequence = ++sequence,
                CommandId = envelope.CommandId,
                CommandType = commandType,
                CommandHash = commandHash,
                Type = stored.Type,
                Version = stored.Version,
                Data = stored.Data,
                AuthorId = envelope.AuthorId,
                OccurredAt = now,
            };
        }).ToList();

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            db.Events.AddRange(records);
            apply!();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var logged = records.Zip(events, (r, e) => new LoggedEvent(Guid.Empty, r.Sequence, r.CommandId, e, r.OccurredAt)).ToList();
        await NotifyAsync(envelope, logged, ct);
        return new CommandOutcome(true, false, null, logged, secret);
    }

    private async Task<(IReadOnlyList<IGameEvent> Events, Action? Apply, string? Secret, Rejection? Rejection)> DecideAccountAsync(
        GameEventDbContext db, IAccountCommand command, DateTimeOffset now, CancellationToken ct)
    {
        static (IReadOnlyList<IGameEvent>, Action?, string?, Rejection?) Reject(string code, string detail) => ([], null, null, new Rejection(code, detail));

        switch (command)
        {
            case CreateAccount or SeedAccount or CreateFirstAdmin:
                {
                    var (login, name, role, password) = command switch
                    {
                        CreateAccount c => (c.Login, c.Name, c.Role, (string?)null),
                        SeedAccount c => (c.Login, c.Name, c.Role, c.Password),
                        CreateFirstAdmin c => (c.Login, c.Name, Role.Admin, (string?)null),
                        _ => throw new InvalidOperationException(),
                    };
                    if (command is CreateFirstAdmin && await db.Users.AnyAsync(u => u.Role == Role.Admin && !u.IsDeleted, ct))
                    {
                        return Reject(AccountRules.AdminExists, "The site has an admin already: new accounts are made on the admin page.");
                    }

                    if (!AccountRules.IsValidLogin(login))
                    {
                        return Reject(AccountRules.LoginInvalid, $"A login is {AccountRules.MinLoginLength}–{AccountRules.MaxLoginLength} Latin letters, digits, «.», «-» or «_».");
                    }

                    if (!AccountRules.IsValidName(name))
                    {
                        return Reject(AccountRules.NameInvalid, $"A name is 1–{AccountRules.MaxNameLength} characters.");
                    }

                    if (!Enum.IsDefined(role))
                    {
                        return Reject(AccountRules.RoleInvalid, "Unknown role.");
                    }

                    var normalized = UserRecord.Normalize(login);
                    if (await db.Users.AnyAsync(u => u.NormalizedLogin == normalized, ct))
                    {
                        return Reject(AccountRules.LoginTaken, $"Login «{login}» is taken (deleted accounts keep theirs).");
                    }

                    if (password is not null && !AccountRules.IsValidPassword(password, login))
                    {
                        return Reject(AccountRules.PasswordInvalid, $"A password is {AccountRules.MinPasswordLength}–{AccountRules.MaxPasswordLength} characters and not the login.");
                    }

                    var temporary = password is null ? AccountRules.TemporaryPassword() : null;
                    var userId = ids.NewId();
                    var user = new UserRecord
                    {
                        Id = userId,
                        Login = login,
                        NormalizedLogin = normalized,
                        Name = name.Trim(),
                        Role = role,
                        PasswordHash = "",
                        SecurityStamp = AccountRules.NewStamp(),
                        MustChangePassword = password is null,
                        CreatedAt = now,
                    };
                    user.PasswordHash = passwords.Hash(user, password ?? temporary!);
                    return ([new AccountCreated(userId, login, user.Name, AccountRules.ToLog(role), user.MustChangePassword)], () => db.Users.Add(user), temporary, null);
                }

            case ResetPassword reset:
                {
                    if (await db.Users.SingleOrDefaultAsync(u => u.Id == reset.UserId, ct) is not { } user)
                    {
                        return Reject(AccountRules.Unknown, $"Account {reset.UserId} does not exist.");
                    }

                    var temporary = AccountRules.TemporaryPassword();
                    var sessions = await LiveSessionsAsync(db, user.Id, SessionNow, ct);
                    return ([new AccountPasswordReset(user.Id), .. Ended(user.Id, sessions, SessionEndReason.AccountChanged)], () =>
                    {
                        user.PasswordHash = passwords.Hash(user, temporary);
                        user.MustChangePassword = true;
                        user.SecurityStamp = AccountRules.NewStamp();
                        EndAll(sessions, SessionNow);
                    }, temporary, null);
                }

            case ChangeOwnPassword change:
                {
                    if (await db.Users.SingleOrDefaultAsync(u => u.Id == change.UserId && !u.IsDeleted, ct) is not { } user)
                    {
                        return Reject(AccountRules.Unknown, $"Account {change.UserId} does not exist.");
                    }

                    // Checked against this stamp; a reset or another change since makes it stale (D-106)
                    if (user.SecurityStamp != change.ExpectedStamp)
                    {
                        return Reject(AccountRules.Stale, "The account changed since the current password was checked; sign in again.");
                    }

                    if (!AccountRules.IsValidPassword(change.NewPassword, user.Login) || passwords.Verify(user, change.NewPassword))
                    {
                        return Reject(AccountRules.PasswordInvalid, $"A new password is {AccountRules.MinPasswordLength}–{AccountRules.MaxPasswordLength} characters, not the login and not the current one.");
                    }

                    // The device that changed it stays signed in; every other session ends (D-467)
                    var others = (await LiveSessionsAsync(db, user.Id, SessionNow, ct)).Where(s => s.Id != change.KeepSession).ToList();
                    return ([new AccountPasswordChanged(user.Id), .. Ended(user.Id, others, SessionEndReason.PasswordChanged)], () =>
                    {
                        user.PasswordHash = passwords.Hash(user, change.NewPassword);
                        user.MustChangePassword = false;
                        user.SecurityStamp = AccountRules.NewStamp();
                        EndAll(others, SessionNow);
                    }, null, null);
                }

            case ChangeAccount edit:
                {
                    if (await db.Users.SingleOrDefaultAsync(u => u.Id == edit.UserId, ct) is not { } user)
                    {
                        return Reject(AccountRules.Unknown, $"Account {edit.UserId} does not exist.");
                    }

                    if (!AccountRules.IsValidName(edit.Name))
                    {
                        return Reject(AccountRules.NameInvalid, $"A name is 1–{AccountRules.MaxNameLength} characters.");
                    }

                    if (!Enum.IsDefined(edit.Role))
                    {
                        return Reject(AccountRules.RoleInvalid, "Unknown role.");
                    }

                    if (user.Name == edit.Name.Trim() && user.Role == edit.Role)
                    {
                        return Reject(AccountRules.NothingToChange, "Nothing changes.");
                    }

                    if (user.Role == Role.Admin && edit.Role != Role.Admin && !user.IsDeleted && await IsLastAdminAsync(db, user.Id, ct))
                    {
                        return Reject(AccountRules.LastAdmin, "The last admin keeps the role.");
                    }

                    var sessions = user.Role != edit.Role ? await LiveSessionsAsync(db, user.Id, SessionNow, ct) : [];
                    return ([new AccountChanged(user.Id, edit.Name.Trim(), AccountRules.ToLog(edit.Role)), .. Ended(user.Id, sessions, SessionEndReason.AccountChanged)], () =>
                    {
                        // A new role ends the account's sessions (D-67, D-467)
                        if (user.Role != edit.Role)
                        {
                            user.SecurityStamp = AccountRules.NewStamp();
                            EndAll(sessions, SessionNow);
                        }

                        user.Name = edit.Name.Trim();
                        user.Role = edit.Role;
                    }, null, null);
                }

            case DeleteAccount delete:
                {
                    if (await db.Users.SingleOrDefaultAsync(u => u.Id == delete.UserId, ct) is not { } user)
                    {
                        return Reject(AccountRules.Unknown, $"Account {delete.UserId} does not exist.");
                    }

                    if (user.IsDeleted)
                    {
                        return Reject(AccountRules.Deleted, "The account is already deleted.");
                    }

                    if (user.Role == Role.Admin && await IsLastAdminAsync(db, user.Id, ct))
                    {
                        return Reject(AccountRules.LastAdmin, "The last admin is not deleted.");
                    }

                    var sessions = await LiveSessionsAsync(db, user.Id, SessionNow, ct);
                    return ([new AccountDeleted(user.Id), .. Ended(user.Id, sessions, SessionEndReason.AccountChanged)], () =>
                    {
                        user.IsDeleted = true;
                        user.SecurityStamp = AccountRules.NewStamp();
                        EndAll(sessions, SessionNow);
                    }, null, null);
                }

            case SetAvatar avatar:
                {
                    if (await db.Users.SingleOrDefaultAsync(u => u.Id == avatar.UserId, ct) is not { } user)
                    {
                        return Reject(AccountRules.Unknown, $"Account {avatar.UserId} does not exist.");
                    }

                    if (user.IsDeleted)
                    {
                        return Reject(AccountRules.Deleted, "The account is deleted.");
                    }

                    if (avatar.FileId is { } fileId && !await db.Files.AnyAsync(f => f.Id == fileId && !f.IsDeleted && f.Kind == Engine.Files.FileKind.Upload, ct))
                    {
                        return Reject(AccountRules.AvatarFileUnknown, $"File {fileId} is not stored.");
                    }

                    return user.AvatarFileId == avatar.FileId
                        ? Reject(AccountRules.NothingToChange, "The avatar is already this one.")
                        : ([new AccountAvatarChanged(user.Id, avatar.FileId)], () => user.AvatarFileId = avatar.FileId, null, null);
                }

            case RestoreAccount restore:
                {
                    if (await db.Users.SingleOrDefaultAsync(u => u.Id == restore.UserId, ct) is not { } user)
                    {
                        return Reject(AccountRules.Unknown, $"Account {restore.UserId} does not exist.");
                    }

                    return !user.IsDeleted
                        ? Reject(AccountRules.NotDeleted, "The account is not deleted.")
                        : ([new AccountRestored(user.Id)], () =>
                        {
                            // Whatever password it had stays behind: only a reset lets it in again (D-106)
                            user.IsDeleted = false;
                            user.PasswordHash = AccountRules.DisabledHash();
                            user.MustChangePassword = true;
                            user.SecurityStamp = AccountRules.NewStamp();
                        }, null, null);
                }

            default:
                throw new InvalidOperationException($"Unknown account command {command.GetType().Name}.");
        }
    }

    /// <summary>
    /// Sessions (D-466): the sign-in makes one, a live one's visits move its end, the owner or the admin ends them, the
    /// scheduler drops the rows of the ended. They are the accounts' data, so they go through the same queue (D-469).
    /// </summary>
    private static async Task<(IReadOnlyList<IGameEvent> Events, Action? Apply, string? Secret, Rejection? Rejection)> DecideSessionAsync(
        GameEventDbContext db, IGlobalCommand command, DateTimeOffset now, CancellationToken ct)
    {
        switch (command)
        {
            case StartSession start:
                {
                    if (!await db.Users.AnyAsync(u => u.Id == start.UserId && !u.IsDeleted, ct))
                    {
                        return ([], null, null, new Rejection(AccountRules.Unknown, $"Account {start.UserId} does not exist."));
                    }

                    // Over the limit the least recently seen sessions end in the same command (SES-2)
                    var live = await LiveSessionsAsync(db, start.UserId, now, ct);
                    var over = live.OrderBy(s => s.LastSeenAt).ThenBy(s => s.CreatedAt)
                        .Take(Math.Max(0, live.Count - Math.Max(1, start.MaxLive) + 1))
                        .ToList();
                    var session = new SessionRecord
                    {
                        Id = SessionRules.NewId(),
                        UserId = start.UserId,
                        CreatedAt = now,
                        LastSeenAt = now,
                        ExpiresAt = now + SessionRules.Lifetime,
                        UserAgent = Cut(start.UserAgent, SessionRules.MaxUserAgentLength),
                        Address = Cut(start.Address, SessionRules.MaxAddressLength),
                    };
                    return ([.. Ended(start.UserId, over, SessionEndReason.Limit), new SessionStarted(start.UserId, session.Id)], () =>
                    {
                        EndAll(over, now);
                        db.Sessions.Add(session);
                    }, null, null);
                }

            case TouchSession touch:
                {
                    // An ended or expired session stays as it was: a touch never brings one back
                    var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == touch.SessionId, ct);
                    return session is not null && session.IsActive(now)
                        ? ([], () =>
                        {
                            session.LastSeenAt = now;
                            session.ExpiresAt = now + SessionRules.Lifetime;
                        }, null, null)
                        : ([], null, null, new Rejection(SessionRules.Unknown, $"Session {touch.SessionId} is not live."));
                }

            case EndSessions end:
                {
                    if (!await db.Users.AnyAsync(u => u.Id == end.UserId, ct))
                    {
                        return ([], null, null, new Rejection(AccountRules.Unknown, $"Account {end.UserId} does not exist."));
                    }

                    var ended = (await LiveSessionsAsync(db, end.UserId, now, ct))
                        .Where(s => (end.Only is not { } only || s.Id == only) && s.Id != end.Keep)
                        .ToList();
                    if (end.Only is not null && ended.Count == 0)
                    {
                        return ([], null, null, new Rejection(SessionRules.Unknown, $"Session {end.Only} of account {end.UserId} is not live."));
                    }

                    return ended.Count == 0
                        ? ([], () => { }, null, null)
                        : ([new SessionsEnded(end.UserId, [.. ended.Select(s => s.Id)], end.Reason)], () => EndAll(ended, now), null, null);
                }

            case PurgeSessions:
                {
                    var gone = await db.Sessions.Where(s => s.RevokedAt != null || s.ExpiresAt <= now).ToListAsync(ct);
                    return ([], () => db.Sessions.RemoveRange(gone), null, null);
                }

            default:
                throw new InvalidOperationException($"Unknown session command {command.GetType().Name}.");
        }
    }

    // Times compare in SQL: they are stored as UTC ticks (UtcTicksConverter), which sort as the moments do
    private static Task<List<SessionRecord>> LiveSessionsAsync(GameEventDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now).ToListAsync(ct);

    /// <summary>The log's record of the sessions a command ends, none when it ends none.</summary>
    private static IGameEvent[] Ended(Guid userId, List<SessionRecord> sessions, SessionEndReason reason) =>
        sessions.Count == 0 ? [] : [new SessionsEnded(userId, [.. sessions.Select(s => s.Id)], reason)];

    private static void EndAll(IEnumerable<SessionRecord> sessions, DateTimeOffset now)
    {
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
        }
    }

    private static string? Cut(string? text, int length) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= length ? text : text[..length];

    private static Task<bool> IsLastAdminAsync(GameEventDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AllAsync(u => u.Id == userId || u.Role != Role.Admin || u.IsDeleted, ct);

}
