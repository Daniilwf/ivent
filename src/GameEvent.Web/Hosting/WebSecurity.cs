using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Hosting;

/// <summary>Web-level protections (D-72): session revalidation, trusted proxy, headers, body size.</summary>
public static class WebSecurity
{
    public const string StampClaim = "ge.stamp";

    /// <summary>The session the cookie belongs to (D-466): a cookie without one is signed out (D-467).</summary>
    public const string SessionClaim = "ge.session";
    public const long ApiBodyLimitBytes = 64 * 1024;

    /// <summary>The cookie's session id, or null for a cookie made before sessions (D-467).</summary>
    public static Guid? SessionId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal?.FindFirstValue(SessionClaim), out var id) ? id : null;

    /// <summary>
    /// Every request re-reads the account and its session (16 users: cheap). A deleted account, a changed role, a rotated
    /// security stamp or an ended, expired or unknown session ends the sign-in at once, instead of living in the cookie
    /// for 30 days (D-67, D-466). A live session's visit is written at most every <see cref="SessionRules.TouchInterval"/>.
    /// </summary>
    public static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.Principal;
        var services = context.HttpContext.RequestServices;
        var db = services.GetRequiredService<GameEventDbContext>();
        var ct = context.HttpContext.RequestAborted;
        var user = Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct)
            : null;
        var session = principal?.SessionId() is { } sessionId
            ? await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, ct)
            : null;
        var now = services.GetRequiredService<IClock>().UtcNow;

        var valid = user is { IsDeleted: false }
            && principal is not null
            && principal.FindFirstValue(StampClaim) == user.SecurityStamp
            && principal.IsInRole(user.Role.ToString())
            && session is not null
            && session.UserId == user.Id
            && session.IsActive(now);
        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        context.HttpContext.Items[MustChangePasswordKey] = user!.MustChangePassword;
        if (now - session!.LastSeenAt >= SessionRules.TouchInterval)
        {
            await services.GetRequiredService<Accounts.SessionTouches>().TouchAsync(session.Id, ct);
        }
    }

    /// <summary>Set for a signed-in request whose account still has a temporary password (D-106).</summary>
    public const string MustChangePasswordKey = "ge.mustChangePassword";

    // A temporary password opens only what it takes to replace it (D-106).
    private static readonly string[] s_openWithTemporaryPassword =
        ["/api/auth/me", "/api/auth/password", "/api/auth/logout", "/api/auth/antiforgery", "/api/auth/login", "/api/status"];

    /// <summary>
    /// With a temporary password every API call but the password change is refused, the hub too: 403 with
    /// <c>account.mustChangePassword</c> (A1, D-106).
    /// </summary>
    public static async Task RequirePasswordChangeAsync(HttpContext http, Func<Task> next)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(next);
        if (http.Items.TryGetValue(MustChangePasswordKey, out var must) && must is true
            && (http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                || http.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
            && !s_openWithTemporaryPassword.Any(p => http.Request.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(
                new { title = "Change the temporary password first.", status = 403, code = "account.mustChangePassword" },
                http.RequestAborted);
            return;
        }

        await next();
    }

    /// <summary>
    /// Only proxies listed in <c>Proxy:KnownNetworks</c> (the Caddy container network) may set the client address.
    /// With none configured, X-Forwarded-For is ignored, so it cannot be spoofed to dodge the login limit.
    /// </summary>
    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [])
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    }

    /// <summary>Rate-limit key for a client address: IPv6 clients are grouped by their /64 prefix.</summary>
    public static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>
    /// The page's policy (SEC-1, D-462; promised by D-72 with the frontend): scripts, fonts, pictures and connections
    /// only from the site itself — the hub's WebSocket named too, for browsers whose <c>'self'</c> does not cover it.
    /// Styles may be inline: Radix and Motion put small style elements in the page; a style runs no code. Pictures may
    /// also be <c>data:</c> and <c>blob:</c> — the preview of an upload and the screenshot of a bug report, whose drawing
    /// also sets the base address of its own copy of the page to the site's stylesheet (<c>base-uri 'self'</c>). Files
    /// served by the API replace it with their own, stricter one.
    /// </summary>
    internal static string ContentSecurityPolicy(HttpRequest request) =>
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; "
        + (request.Host.Value is { Length: > 0 } host && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']')
            ? $"connect-src 'self' {(request.IsHttps ? "wss" : "ws")}://{host}; "
            : "connect-src 'self'; ")
        + "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>Headers for every response: no sniffing, no framing, no referrer leaks, only the site's own scripts.</summary>
    public static void UseSecurityHeaders(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = ContentSecurityPolicy(context.Request);
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            await next(context);
        });
    }

    /// <summary>API bodies are small JSON; the 30 MB Kestrel default is for future uploads only.</summary>
    public static void UseApiBodyLimit(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            // An upload carries a picture (D-108), the map editor a whole map; every other API body is small JSON
            var bodyLimit = Files.FileEndpoints.IsUpload(context.Request)
                ? context.RequestServices.GetRequiredService<Infrastructure.Files.FileLimits>().MaxUploadBytes + Files.FileEndpoints.FormOverheadBytes
                : Map.AdminMapEndpoints.IsMapBody(context.Request)
                    ? Map.AdminMapEndpoints.MaxMapRequestBytes
                    : ApiBodyLimitBytes;
            if (context.Request.Path.StartsWithSegments("/api")
                && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = bodyLimit;
            }

            if (context.Request.Path.StartsWithSegments("/api") && context.Request.ContentLength > bodyLimit)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            await next(context);
        });
    }
}
