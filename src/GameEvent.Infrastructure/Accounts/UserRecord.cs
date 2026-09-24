namespace GameEvent.Infrastructure.Accounts;

/// <summary>Role of an account. An admin who also plays has two accounts (SPEC «Аккаунты»).</summary>
public enum Role
{
    Player,
    Admin,
    Spectator,
}

/// <summary>A site account. Accounts live across seasons; soft-deleted accounts stay for history.</summary>
public sealed class UserRecord
{
    public Guid Id { get; set; }

    /// <summary>Login as typed by the admin.</summary>
    public required string Login { get; set; }

    /// <summary>Lower-case invariant login: unique, used for sign-in lookups.</summary>
    public required string NormalizedLogin { get; set; }

    public required string Name { get; set; }

    public required string PasswordHash { get; set; }

    /// <summary>
    /// Changes whenever the password, the role or the account itself changes: sessions carrying an older stamp end.
    /// </summary>
    public required string SecurityStamp { get; set; }

    public Role Role { get; set; }

    /// <summary>Set for a temporary password from the admin: the user must change it (task D8).</summary>
    public bool MustChangePassword { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public static string Normalize(string login) => login.Trim().ToLowerInvariant();
}
