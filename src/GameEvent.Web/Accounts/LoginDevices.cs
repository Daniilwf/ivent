using System.Globalization;
using GameEvent.Infrastructure.Accounts;
using Microsoft.AspNetCore.DataProtection;

namespace GameEvent.Web.Accounts;

/// <summary>
/// The device cookie of a sign-in (SEC-2, SEC-4, D-460): after a right password the browser keeps a signed note of the
/// account it signed in to — its id, its security stamp at that moment and when the note runs out. A browser with a note
/// that still matches the account is not held by the per-login ceiling of <see cref="LoginThrottle"/>, so a guess spread
/// over other addresses cannot lock the owner out; its own address limit still holds it. The note opens nothing by itself.
/// Its lifetime runs by wall time (<see cref="TimeProvider"/>) like the session's, not by the game clock (SEC-7, D-650).
/// It ends with the password: a password change or reset, a role change or a deletion rotate the stamp, and a login
/// deleted and given to someone else is another account, so a browser that got in with an old password is a stranger again.
/// </summary>
public sealed class LoginDevices(IDataProtectionProvider protection, TimeProvider time, string cookieName, CookieSecurePolicy securePolicy)
{
    /// <summary>A year: a phone that signs in a few times a season stays known. Every sign-in renews it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    private readonly IDataProtector _protector = protection.CreateProtector("GameEvent.LoginDevice.v2");

    public string CookieName => cookieName;

    /// <summary>The browser signed in here to <paramref name="user"/> as it is now (same account, same stamp) within the lifetime.</summary>
    public bool IsKnown(HttpContext http, UserRecord? user)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (user is null || http.Request.Cookies[cookieName] is not { Length: > 0 } value)
        {
            return false;
        }

        string note;
        try
        {
            note = _protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Forged, cut short, or signed by keys this site no longer has: a stranger
            return false;
        }

        var parts = note.Split('|', 3);
        return parts.Length == 3
            && Guid.TryParseExact(parts[0], "N", out var id) && id == user.Id
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            && time.GetUtcNow().ToUnixTimeSeconds() < expires
            && string.Equals(parts[2], user.SecurityStamp, StringComparison.Ordinal);
    }

    /// <summary>Remembers that the browser signed in to <paramref name="user"/> with its current security stamp.</summary>
    public void Remember(HttpContext http, UserRecord user)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        var expires = time.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        var note = string.Create(CultureInfo.InvariantCulture, $"{user.Id:N}|{expires}|{user.SecurityStamp}");
        http.Response.Cookies.Append(cookieName, _protector.Protect(note), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = securePolicy == CookieSecurePolicy.Always || http.Request.IsHttps,
            Path = "/",
            MaxAge = Lifetime,
            IsEssential = true,
        });
    }
}
