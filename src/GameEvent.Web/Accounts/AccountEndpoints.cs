using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Accounts;

public sealed record LoginRequest([property: Required, MaxLength(64)] string Login, [property: Required, MaxLength(256)] string Password);

/// <summary>
/// The signed-in user. <c>token</c> — the colour of their token in the latest season they play (Seasons.PlayerTokens), as on
/// their profile; none outside seasons (D-202).
/// </summary>
public sealed record CurrentUser(Guid Id, string Login, string Name, Role Role, bool MustChangePassword, Files.FileLinkView? Avatar, int? Token = null);

public sealed record AntiforgeryToken(string Token, string HeaderName);

/// <summary>Sign-in by login and password with a cookie (D-26). Accounts are created by the admin (D-106).</summary>
public static partial class AccountEndpoints
{
    public static void MapAccounts(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        // The SPA fetches a token before any POST and again after signing in (the token is bound to the user).
        auth.MapGet("/antiforgery", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            return TypedResults.Ok(new AntiforgeryToken(tokens.RequestToken!, Csrf.HeaderName));
        });

        auth.MapPost("/login", LoginAsync)
            .RequireRateLimiting(AppSetup.LoginRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Signing out ends this device's session on the server too: a copy of the cookie stops working (D-466)
        auth.MapPost("/logout", async (HttpContext http, ClaimsPrincipal principal, CommandBus bus, IIdGenerator ids) =>
        {
            if (principal.UserId() is { } userId && principal.SessionId() is { } sessionId)
            {
                await bus.SendAsync(
                    new CommandEnvelope(ids.NewId(), Guid.Empty, new EndSessions(userId, Engine.Accounts.SessionEndReason.SignedOut, Only: sessionId), userId),
                    http.RequestAborted);
            }

            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.NoContent();
        });

        auth.MapGet("/me", async Task<Results<Ok<CurrentUser>, UnauthorizedHttpResult>> (
            ClaimsPrincipal principal, GameEventDbContext db, CancellationToken ct) =>
        {
            var user = await FindCurrentAsync(principal, db, ct);
            return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(await ToCurrentAsync(user, db, ct));
        }).RequireAuthorization();
    }

    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static async Task<Results<Ok<CurrentUser>, UnauthorizedHttpResult, ValidationProblem, StatusCodeHttpResult>> LoginAsync(
        LoginRequest request, HttpContext http, GameEventDbContext db, Passwords passwords, LoginThrottle throttle, LoginDevices devices,
        CommandBus bus, IIdGenerator ids, ILoggerFactory loggers, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrEmpty(request.Password)
            || request.Login.Length > 64 || request.Password.Length > 256)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [string.IsNullOrWhiteSpace(request.Login) || request.Login.Length > 64 ? "login" : "password"] =
                    ["Login (up to 64 characters) and password (up to 256) are required."],
            });
        }

        var normalized = UserRecord.Normalize(request.Login);
        var logger = loggers.CreateLogger(typeof(AccountEndpoints));
        var address = WebSecurity.ClientKey(http.Connection.RemoteIpAddress);
        if (!throttle.TryBegin(normalized, address, devices.IsKnown(http, normalized)))
        {
            LogLoginThrottled(logger, TypedLogin(null, normalized), address);
            return TypedResults.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.NormalizedLogin == normalized && !u.IsDeleted, ct);
        if (!passwords.Verify(user, request.Password))
        {
            LogLoginFailed(logger, TypedLogin(user, normalized), address);
            return TypedResults.Unauthorized();
        }

        throttle.Succeeded(normalized, address);
        devices.Remember(http, normalized);

        // A browser signing in again leaves its previous session behind: it ends, not lingers unseen (SES-1)
        if (http.User.UserId() is { } previousUser && http.User.SessionId() is { } previous)
        {
            await bus.SendAsync(
                new CommandEnvelope(ids.NewId(), Guid.Empty, new EndSessions(previousUser, Engine.Accounts.SessionEndReason.SignedOut, Only: previous), previousUser), ct);
        }

        // Every sign-in is a new session with a new id: a cookie planted before it names none of it (D-466)
        var started = await bus.SendAsync(
            new CommandEnvelope(ids.NewId(), Guid.Empty, new StartSession(user!.Id, http.Request.Headers.UserAgent.ToString(), address), user.Id), ct);
        if (!started.IsAccepted)
        {
            return TypedResults.Unauthorized();
        }

        await SignInAsync(http, user, started.Events.Select(e => e.Event).OfType<Engine.Accounts.SessionStarted>().Single().SessionId);
        return TypedResults.Ok(await ToCurrentAsync(user, db, ct));
    }

    /// <summary>The cookie of session <paramref name="sessionId"/> for <paramref name="user"/> with its current security stamp (D-67, D-466).</summary>
    internal static Task SignInAsync(HttpContext http, UserRecord user, Guid sessionId)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Login),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(WebSecurity.StampClaim, user.SecurityStamp),
                new Claim(WebSecurity.SessionClaim, sessionId.ToString()),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>
    /// What a failed sign-in writes to the log (D-107): an existing login as it is, anything else only as a short hash —
    /// the field may hold a pasted password, and the log is kept for a month.
    /// </summary>
    internal static string TypedLogin(UserRecord? user, string normalized) => user is not null
        ? user.Login
        : "unknown#" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))[..8];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed sign-in for {Login} from {Address}")]
    private static partial void LogLoginFailed(ILogger logger, string login, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wrong current password of {Login} from {Address} at a password change")]
    internal static partial void LogPasswordCheckFailed(ILogger logger, string login, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in for {Login} from {Address} throttled after repeated failures")]
    private static partial void LogLoginThrottled(ILogger logger, string login, string address);

    private static async Task<UserRecord?> FindCurrentAsync(ClaimsPrincipal principal, GameEventDbContext db, CancellationToken ct) =>
        principal.UserId() is { } id
            ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct)
            : null;

    private static async Task<CurrentUser> ToCurrentAsync(UserRecord user, GameEventDbContext db, CancellationToken ct) =>
        new(user.Id, user.Login, user.Name, user.Role, user.MustChangePassword, user.AvatarFileId is { } avatar ? Files.FileLinkView.Of(avatar) : null,
            await db.LatestTokenAsync(user.Id, ct));
}
