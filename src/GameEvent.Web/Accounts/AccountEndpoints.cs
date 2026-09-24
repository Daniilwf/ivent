using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Accounts;

public sealed record LoginRequest([property: Required, MaxLength(64)] string Login, [property: Required, MaxLength(256)] string Password);

public sealed record CurrentUser(Guid Id, string Login, string Name, Role Role, bool MustChangePassword);

public sealed record AntiforgeryToken(string Token, string HeaderName);

/// <summary>Sign-in by login and password with a cookie (D-26). Accounts are created by the admin (task D8).</summary>
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

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.NoContent();
        });

        auth.MapGet("/me", async Task<Results<Ok<CurrentUser>, UnauthorizedHttpResult>> (
            ClaimsPrincipal principal, GameEventDbContext db, CancellationToken ct) =>
        {
            var user = await FindCurrentAsync(principal, db, ct);
            return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(ToCurrent(user));
        }).RequireAuthorization();
    }

    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static async Task<Results<Ok<CurrentUser>, UnauthorizedHttpResult, ValidationProblem, StatusCodeHttpResult>> LoginAsync(
        LoginRequest request, HttpContext http, GameEventDbContext db, Passwords passwords, LoginThrottle throttle, ILoggerFactory loggers, CancellationToken ct)
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
        if (!throttle.TryBegin(normalized, address))
        {
            LogLoginThrottled(logger, normalized, address);
            return TypedResults.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.NormalizedLogin == normalized && !u.IsDeleted, ct);
        if (!passwords.Verify(user, request.Password))
        {
            LogLoginFailed(logger, normalized, address);
            return TypedResults.Unauthorized();
        }

        throttle.Succeeded(normalized, address);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user!.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Login),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(WebSecurity.StampClaim, user.SecurityStamp),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return TypedResults.Ok(ToCurrent(user));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed sign-in for {Login} from {Address}")]
    private static partial void LogLoginFailed(ILogger logger, string login, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in for {Login} from {Address} throttled after repeated failures")]
    private static partial void LogLoginThrottled(ILogger logger, string login, string address);

    private static async Task<UserRecord?> FindCurrentAsync(ClaimsPrincipal principal, GameEventDbContext db, CancellationToken ct) =>
        principal.UserId() is { } id
            ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct)
            : null;

    private static CurrentUser ToCurrent(UserRecord user) => new(user.Id, user.Login, user.Name, user.Role, user.MustChangePassword);
}
