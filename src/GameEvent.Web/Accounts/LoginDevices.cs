using Microsoft.AspNetCore.DataProtection;

namespace GameEvent.Web.Accounts;

/// <summary>
/// The device cookie of a sign-in (SEC-2, D-460): after a right password the browser keeps a signed note of the login it
/// signed in to. A browser with that note is not held by the per-login ceiling of <see cref="LoginThrottle"/>, so a guess
/// spread over other addresses cannot lock the owner out; its own address limit still holds it. The note says nothing
/// but the login, opens nothing by itself and is useless to anyone who does not know the password already.
/// </summary>
public sealed class LoginDevices(IDataProtectionProvider protection, string cookieName, CookieSecurePolicy securePolicy)
{
    /// <summary>A year: a phone that signs in a few times a season stays known.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    private readonly IDataProtector _protector = protection.CreateProtector("GameEvent.LoginDevice.v1");

    public string CookieName => cookieName;

    /// <summary>The browser signed in to <paramref name="normalizedLogin"/> here before.</summary>
    public bool IsKnown(HttpContext http, string normalizedLogin)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Request.Cookies[cookieName] is not { Length: > 0 } value)
        {
            return false;
        }

        try
        {
            return string.Equals(_protector.Unprotect(value), normalizedLogin, StringComparison.Ordinal);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Forged, cut short, or signed by keys this site no longer has: a stranger
            return false;
        }
    }

    /// <summary>Remembers that the browser signed in to <paramref name="normalizedLogin"/>.</summary>
    public void Remember(HttpContext http, string normalizedLogin)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Append(cookieName, _protector.Protect(normalizedLogin), new CookieOptions
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
