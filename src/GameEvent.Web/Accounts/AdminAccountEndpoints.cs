using System.Security.Claims;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using AccountResult = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<GameEvent.Web.Accounts.AccountActionResponse>,
    Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
    Microsoft.AspNetCore.Http.HttpResults.ValidationProblem,
    Microsoft.AspNetCore.Http.HttpResults.NotFound,
    Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>;

namespace GameEvent.Web.Accounts;

/// <summary>An account as the admin sees it (D-106); <c>sessions</c> — how many devices it is signed in on (D-466).</summary>
public sealed record AccountView(Guid Id, string Login, string Name, Role Role, bool MustChangePassword, bool IsDeleted, DateTimeOffset CreatedAt, int Sessions);

/// <summary>The admin creates an account: login, name, role; the answer carries the temporary password once.</summary>
public sealed record CreateAccountRequest(Guid CommandId, string? Login, string? Name, Role? Role);

/// <summary>The admin renames an account or changes its role.</summary>
public sealed record ChangeAccountRequest(Guid CommandId, string? Name, Role? Role);

/// <summary>An admin action on an account with nothing else to say: reset the password, delete, restore.</summary>
public sealed record AccountActionRequest(Guid CommandId);

/// <summary>
/// The result of an account command. <c>temporaryPassword</c> — after a creation or a reset, shown once: a repeated
/// request (the same command id) gets <c>duplicate: true</c> and no password; reset again to get a new one (D-106).
/// </summary>
public sealed record AccountActionResponse(bool Duplicate, AccountView Account, string? TemporaryPassword);

/// <summary>A signed-in user changes their own password; required before anything else after a temporary one.</summary>
public sealed record ChangePasswordRequest(Guid CommandId, string? CurrentPassword, string? NewPassword);

/// <summary>Accounts (SPEC «Аккаунты», A1, D-66, D-106): the admin manages them; everyone changes their own password.</summary>
public static class AdminAccountEndpoints
{
    public static void MapAdminAccounts(this RouteGroupBuilder api)
    {
        var accounts = api.MapGroup("/admin/accounts").WithTags("Admin").RequireAuthorization(Policies.Admin);

        accounts.MapGet("", async Task<Ok<IReadOnlyList<AccountView>>> (GameEventDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                var sessions = await SessionEndpoints.LiveCountsAsync(db, time.GetUtcNow(), ct);
                IReadOnlyList<AccountView> list = [.. (await db.Users.AsNoTracking()
                    .OrderBy(u => u.IsDeleted).ThenBy(u => u.Login)
                    .ToListAsync(ct))
                    .Select(u => ViewOf(u, sessions.GetValueOrDefault(u.Id)))];
                return TypedResults.Ok(list);
            })
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        accounts.MapPost("", (CreateAccountRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, TimeProvider time, CancellationToken ct) =>
                request.Login is null || request.Name is null || request.Role is not { } role || !Enum.IsDefined(role)
                    ? Task.FromResult<AccountResult>(Invalid("login", "A login, a name and a known role are required."))
                    : SendAsync(request.CommandId, Guid.Empty, _ => new CreateAccount(request.Login.Trim(), request.Name, role), user, db, bus, time, ct))
            .WithAccountErrors();

        accounts.MapPost("/{userId:guid}", (Guid userId, ChangeAccountRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, TimeProvider time, CancellationToken ct) =>
                request.Name is null || request.Role is not { } role || !Enum.IsDefined(role)
                    ? Task.FromResult<AccountResult>(Invalid("name", "A name and a known role are required."))
                    : SendAsync(request.CommandId, userId, id => new ChangeAccount(id, request.Name, role), user, db, bus, time, ct))
            .WithAccountErrors();

        accounts.MapPost("/{userId:guid}/reset-password", (Guid userId, AccountActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, TimeProvider time, CancellationToken ct) =>
                SendAsync(request.CommandId, userId, id => new ResetPassword(id), user, db, bus, time, ct))
            .WithAccountErrors();

        accounts.MapPost("/{userId:guid}/delete", (Guid userId, AccountActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, TimeProvider time, CancellationToken ct) =>
                SendAsync(request.CommandId, userId, id => new DeleteAccount(id), user, db, bus, time, ct))
            .WithAccountErrors();

        // «Выйти отовсюду» (D-466): every session of the account ends, the password and the role stay
        accounts.MapPost("/{userId:guid}/end-sessions", (Guid userId, AccountActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, TimeProvider time, CancellationToken ct) =>
                SendAsync(request.CommandId, userId, id => new EndSessions(id, Engine.Accounts.SessionEndReason.Admin), user, db, bus, time, ct))
            .WithAccountErrors();

        accounts.MapPost("/{userId:guid}/restore", (Guid userId, AccountActionRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, TimeProvider time, CancellationToken ct) =>
                SendAsync(request.CommandId, userId, id => new RestoreAccount(id), user, db, bus, time, ct))
            .WithAccountErrors();

        // Every signed-in user, also one who must change the password first
        api.MapPost("/auth/password", ChangePasswordAsync)
            .WithTags("Auth")
            .RequireAuthorization()
            .RequireRateLimiting(AppSetup.LoginRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithAccountErrors();
    }

    private static async Task<AccountResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        GameEventDbContext db,
        Passwords passwords,
        LoginThrottle throttle,
        LoginDevices devices,
        ILoggerFactory loggers,
        CommandBus bus,
        TimeProvider time,
        CancellationToken ct)
    {
        if (request.CommandId == Guid.Empty || request.CurrentPassword is null || request.NewPassword is null
            || request.NewPassword.Length > AccountRules.MaxPasswordLength || request.CurrentPassword.Length > AccountRules.MaxPasswordLength)
        {
            return Invalid("newPassword", "A command id, the current password and a new one are required.");
        }

        if (principal.UserId() is not { } userId || await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct) is not { } user)
        {
            return TypedResults.Forbid();
        }

        // A request already handled (its answer lost, a double click) is said so before anything else: the password in it
        // may have changed since, and a secret is never confirmed (D-106)
        if (await db.Events.AnyAsync(e => e.SeasonId == Guid.Empty && e.CommandId == request.CommandId, ct))
        {
            return Repeat();
        }

        // The current password is guessed like a sign-in: the same limit per login and address, the same log (D-67). A live
        // session is a stronger proof than the device cookie, so a guess spread over other addresses does not hold the change
        // by the per-login ceiling, the forced one after a reset included (SEC-5, D-650)
        var address = WebSecurity.ClientKey(http.Connection.RemoteIpAddress);
        if (!throttle.TryBegin(user.NormalizedLogin, address, knownDevice: true))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many attempts.");
        }

        if (!passwords.Verify(user, request.CurrentPassword))
        {
            AccountEndpoints.LogPasswordCheckFailed(loggers.CreateLogger(typeof(AdminAccountEndpoints)), user.NormalizedLogin, address);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The command was rejected.",
                detail: "The current password is wrong.",
                extensions: new Dictionary<string, object?> { ["code"] = "account.currentPasswordWrong" });
        }

        throttle.Succeeded(user.NormalizedLogin, address);
        var current = principal.SessionId();
        var result = await SendAsync(request.CommandId, userId, id => new ChangeOwnPassword(id, request.NewPassword, user.SecurityStamp, current), principal, db, bus, time, ct);

        // The new security stamp ends every other session (SessionsEnded); this one goes on with the new stamp (D-67, D-467),
        // and this browser stays known with it (SEC-6, D-650)
        if (result.Result is Ok<AccountActionResponse>)
        {
            var fresh = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
            devices.Remember(http, fresh);
            if (current is { } session)
            {
                await AccountEndpoints.SignInAsync(http, fresh, session);
            }
        }

        return result;
    }

    private static Task<AccountResult> SendAsync(
        Guid commandId, Guid userId, Func<Guid, IAccountCommand> command, ClaimsPrincipal principal, GameEventDbContext db, CommandBus bus, TimeProvider time, CancellationToken ct) =>
        commandId == Guid.Empty
            ? Task.FromResult<AccountResult>(Invalid("commandId", "A command id is required."))
            : SendCheckedAsync(commandId, userId, command, principal, db, bus, time, ct);

    private static async Task<AccountResult> SendCheckedAsync(
        Guid commandId, Guid userId, Func<Guid, IAccountCommand> command, ClaimsPrincipal principal, GameEventDbContext db, CommandBus bus, TimeProvider time, CancellationToken ct)
    {
        var sent = command(userId);
        var outcome = await bus.SendAsync(new CommandEnvelope(commandId, Guid.Empty, sent, principal.UserId()), ct);

        // A repeat of a command with a secret cannot tell whether the secret was the same: never confirm it (D-106)
        if (outcome.IsDuplicate && sent is ISecretCommand)
        {
            return Repeat();
        }
        if (!outcome.IsAccepted)
        {
            var code = outcome.Rejection!.Code;
            return code == AccountRules.Unknown
                ? TypedResults.NotFound()
                : TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The command was rejected.",
                    detail: outcome.Rejection.Detail,
                    extensions: new Dictionary<string, object?> { ["code"] = code });
        }

        // A creation names the account the queue made (a repeat too)
        var id = sent is CreateAccount
            ? outcome.Events.Select(e => e.Event).OfType<Engine.Accounts.AccountCreated>().Single().UserId
            : userId;
        var account = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, ct);
        var sessions = await SessionEndpoints.LiveCountsAsync(db, time.GetUtcNow(), ct);
        return TypedResults.Ok(new AccountActionResponse(outcome.IsDuplicate, ViewOf(account, sessions.GetValueOrDefault(account.Id)), outcome.Secret));
    }

    private static AccountView ViewOf(UserRecord u, int sessions) =>
        new(u.Id, u.Login, u.Name, u.Role, u.MustChangePassword, u.IsDeleted, u.CreatedAt, sessions);

    private static ProblemHttpResult Repeat() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The command was rejected.",
            detail: "This request was already handled; send a new one if the password still has to change.",
            extensions: new Dictionary<string, object?> { ["code"] = "account.repeat" });

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static RouteHandlerBuilder WithAccountErrors(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
}
