using GameEvent.Engine.Accounts;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Queue;

/// <summary>
/// Account commands (D-106): the same queue, one transaction each, events in the global log (no season). The account
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
            IAccountCommand account => await DecideAccountAsync(db, account, now, ct),
            Files.RecordFile file => await DecideFileAsync(db, file, now, ct),
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
            case CreateAccount or SeedAccount:
                {
                    var (login, name, role, password) = command switch
                    {
                        CreateAccount c => (c.Login, c.Name, c.Role, (string?)null),
                        SeedAccount c => (c.Login, c.Name, c.Role, c.Password),
                        _ => throw new InvalidOperationException(),
                    };
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
                    return ([new AccountPasswordReset(user.Id)], () =>
                    {
                        user.PasswordHash = passwords.Hash(user, temporary);
                        user.MustChangePassword = true;
                        user.SecurityStamp = AccountRules.NewStamp();
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

                    return ([new AccountPasswordChanged(user.Id)], () =>
                    {
                        user.PasswordHash = passwords.Hash(user, change.NewPassword);
                        user.MustChangePassword = false;
                        user.SecurityStamp = AccountRules.NewStamp();
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

                    return ([new AccountChanged(user.Id, edit.Name.Trim(), AccountRules.ToLog(edit.Role))], () =>
                    {
                        // A new role ends the account's sessions (D-67)
                        if (user.Role != edit.Role)
                        {
                            user.SecurityStamp = AccountRules.NewStamp();
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

                    return ([new AccountDeleted(user.Id)], () =>
                    {
                        user.IsDeleted = true;
                        user.SecurityStamp = AccountRules.NewStamp();
                    }, null, null);
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

    private static Task<bool> IsLastAdminAsync(GameEventDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AllAsync(u => u.Id == userId || u.Role != Role.Admin || u.IsDeleted, ct);

}
