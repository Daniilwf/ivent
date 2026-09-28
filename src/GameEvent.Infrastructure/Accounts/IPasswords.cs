namespace GameEvent.Infrastructure.Accounts;

/// <summary>
/// Password hashing as the queue needs it for account commands (D-106). The implementation lives in the web layer
/// (ASP.NET Core Identity's PBKDF2 hasher, D-26): the infrastructure does not depend on ASP.NET Core.
/// </summary>
public interface IPasswords
{
    string Hash(UserRecord user, string password);

    bool Verify(UserRecord? user, string password);
}
