using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
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
    public const long ApiBodyLimitBytes = 64 * 1024;

    /// <summary>
    /// Every request re-reads the account (16 users: cheap). A deleted account, a changed role or a rotated
    /// security stamp ends the session at once, instead of living in the cookie for 30 days.
    /// </summary>
    public static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.Principal;
        var db = context.HttpContext.RequestServices.GetRequiredService<GameEventDbContext>();
        var user = Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, context.HttpContext.RequestAborted)
            : null;

        var valid = user is { IsDeleted: false }
            && principal is not null
            && principal.FindFirstValue(StampClaim) == user.SecurityStamp
            && principal.IsInRole(user.Role.ToString());
        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        context.HttpContext.Items[MustChangePasswordKey] = user!.MustChangePassword;
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

    /// <summary>Headers for every response: no sniffing, no framing, no referrer leaks.</summary>
    public static void UseSecurityHeaders(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = "frame-ancestors 'none'";
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
