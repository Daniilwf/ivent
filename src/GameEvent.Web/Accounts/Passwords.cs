using GameEvent.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;

namespace GameEvent.Web.Accounts;

/// <summary>Password hashing with ASP.NET Core Identity's PBKDF2 hasher, without the rest of Identity (D-26).</summary>
public sealed class Passwords
{
    private readonly PasswordHasher<UserRecord> _hasher = new();

    // Verifying against this when the login is unknown keeps the response time the same as for a wrong password.
    private readonly string _dummyHash;

    public Passwords() => _dummyHash = _hasher.HashPassword(null!, Guid.NewGuid().ToString());

    public string Hash(UserRecord user, string password) => _hasher.HashPassword(user, password);

    public bool Verify(UserRecord? user, string password)
    {
        if (user is null)
        {
            _hasher.VerifyHashedPassword(null!, _dummyHash, password);
            return false;
        }

        return _hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
    }
}
