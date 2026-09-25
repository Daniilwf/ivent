using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GameEvent.Engine.Accounts;
using GameEvent.Engine.Kernel;

namespace GameEvent.Infrastructure.Accounts;

/// <summary>A command about accounts: it goes through the queue to the global log (D-106), not to a season.</summary>
public interface IAccountCommand : Queue.IGlobalCommand;

/// <summary>A command carrying a secret: its fingerprint for the repeat check is taken without it (D-95, D-106).</summary>
public interface ISecretCommand
{
    ICommand WithoutSecret();
}

/// <summary>
/// The admin creates an account; the queue gives it an id and a temporary password, given back once (D-106). The id
/// is not in the command, so a repeated request is the same command.
/// </summary>
public sealed record CreateAccount(string Login, string Name, Role Role) : IAccountCommand;

/// <summary>The admin gives an account a new temporary password (given back once); the owner must change it.</summary>
public sealed record ResetPassword(Guid UserId) : IAccountCommand;

/// <summary>
/// The owner sets a new password, the current one already checked by the endpoint against the account whose security
/// stamp is <see cref="ExpectedStamp"/>: a reset or another change in between makes the command stale (D-106).
/// </summary>
public sealed record ChangeOwnPassword(Guid UserId, string NewPassword, string ExpectedStamp) : IAccountCommand, ISecretCommand
{
    public ICommand WithoutSecret() => this with { NewPassword = "" };
}

/// <summary>The admin renames an account or changes its role.</summary>
public sealed record ChangeAccount(Guid UserId, string Name, Role Role) : IAccountCommand;

public sealed record DeleteAccount(Guid UserId) : IAccountCommand;

/// <summary>
/// The account's avatar becomes a stored file (a picture or a GIF, D-108), or none (D-117). The site checks who may set it
/// and whose the file is; the queue checks that the account and the file exist.
/// </summary>
public sealed record SetAvatar(Guid UserId, Guid? FileId) : IAccountCommand;

public sealed record RestoreAccount(Guid UserId) : IAccountCommand;

/// <summary>
/// An account with a known password, no change required: the development seed and tests (D-106). Not reachable from
/// the API.
/// </summary>
public sealed record SeedAccount(string Login, string Name, Role Role, string Password) : IAccountCommand, ISecretCommand
{
    public ICommand WithoutSecret() => this with { Password = "" };
}

public static partial class AccountRules
{
    public const int MinLoginLength = 2;
    public const int MaxLoginLength = 32;
    public const int MaxNameLength = 64;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 256;
    public const int TemporaryPasswordLength = 12;

    // No look-alikes (0/o, 1/l/i): the admin reads it out to a friend.
    private const string TemporaryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public const string AvatarFileUnknown = "account.avatarFileUnknown";
    public const string LoginInvalid = "account.loginInvalid";
    public const string LoginTaken = "account.loginTaken";
    public const string NameInvalid = "account.nameInvalid";
    public const string Unknown = "account.unknown";
    public const string Deleted = "account.deleted";
    public const string NotDeleted = "account.notDeleted";
    public const string LastAdmin = "account.lastAdmin";
    public const string PasswordInvalid = "account.passwordInvalid";
    public const string NothingToChange = "account.nothingToChange";
    public const string Stale = "account.stale";
    public const string RoleInvalid = "account.roleInvalid";

    /// <summary>Latin letters, digits, dot, dash, underscore; 2–32 characters (logins are typed on phones too).</summary>
    public static bool IsValidLogin(string? login) =>
        login is not null && login.Length is >= MinLoginLength and <= MaxLoginLength && LoginPattern().IsMatch(login);

    public static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength;

    public static bool IsValidPassword(string? password, string login) =>
        password is not null && password.Length is >= MinPasswordLength and <= MaxPasswordLength
        && !string.Equals(password, login, StringComparison.OrdinalIgnoreCase);

    /// <summary>A temporary password from the system's cryptographic generator, never the game's random source (D-106).</summary>
    public static string TemporaryPassword() =>
        string.Concat(Enumerable.Range(0, TemporaryPasswordLength).Select(_ => TemporaryAlphabet[RandomNumberGenerator.GetInt32(TemporaryAlphabet.Length)]));

    /// <summary>A new security stamp: 128 random bits.</summary>
    public static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Hashes no password matches: an imported placeholder and a restored account until its reset (D-105, D-106). The
    /// sign-in treats them as a wrong password without asking the hasher.
    /// </summary>
    public const string DisabledHashPrefix = "disabled:";

    public static string DisabledHash() => DisabledHashPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static bool IsDisabledHash(string hash) =>
        hash.StartsWith(DisabledHashPrefix, StringComparison.Ordinal) || hash.StartsWith(Seasons.SeasonTransfer.PlaceholderHashPrefix, StringComparison.Ordinal);

    public static AccountRole ToLog(Role role) =>
        role switch
        {
            Role.Player => AccountRole.Player,
            Role.Admin => AccountRole.Admin,
            Role.Spectator => AccountRole.Spectator,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
        };

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LoginPattern();
}
