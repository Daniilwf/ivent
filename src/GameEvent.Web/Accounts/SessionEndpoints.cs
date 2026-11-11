using System.Collections.Concurrent;
using System.Security.Claims;
using GameEvent.Engine.Accounts;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Accounts;

/// <summary>
/// A live session of the signed-in user (D-466): <c>userAgent</c> — the browser's own description at the sign-in, which
/// the page turns into a device's name; <c>current</c> — the session of this request.
/// </summary>
public sealed record SessionView(Guid Id, string? UserAgent, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool Current);

/// <summary>Ends one session or the others: the command id makes a repeated click the same request.</summary>
public sealed record EndSessionsRequest(Guid CommandId);

/// <summary>How many sessions a request ended.</summary>
public sealed record EndedSessions(int Ended);

/// <summary>
/// The visits of live sessions (D-466): one touch at a time per session, so a burst of requests after a quiet while writes
/// one, not one per request. A refused touch (the session ended meanwhile, maintenance) is no error: the next request
/// checks the session itself.
/// </summary>
public sealed class SessionTouches(CommandBus bus, IIdGenerator ids)
{
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    public async Task TouchAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_running.TryAdd(sessionId, 0))
        {
            return;
        }

        try
        {
            await bus.SendAsync(new CommandEnvelope(ids.NewId(), Guid.Empty, new TouchSession(sessionId), AuthorId: null), ct);
        }
        finally
        {
            _running.TryRemove(sessionId, out _);
        }
    }
}

/// <summary>«Где я вошёл» (D-466): the signed-in user's live sessions; ending one, or all but this one.</summary>
public static class SessionEndpoints
{
    public static void MapSessions(this RouteGroupBuilder api)
    {
        var sessions = api.MapGroup("/auth/sessions").WithTags("Auth").RequireAuthorization();

        sessions.MapGet("", async Task<Results<Ok<IReadOnlyList<SessionView>>, UnauthorizedHttpResult>> (
            ClaimsPrincipal principal, GameEventDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (principal.UserId() is not { } userId)
            {
                return TypedResults.Unauthorized();
            }

            var now = time.GetUtcNow();
            var current = principal.SessionId();
            IReadOnlyList<SessionView> list = [.. (await db.Sessions.AsNoTracking().Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct))
                .Where(s => s.IsActive(now))
                .OrderByDescending(s => s.Id == current)
                .ThenByDescending(s => s.LastSeenAt)
                .Select(s => new SessionView(s.Id, s.UserAgent, s.CreatedAt, s.LastSeenAt, s.Id == current))];
            return TypedResults.Ok(list);
        });

        // Someone else's session and an unknown one answer alike: a 404 tells nothing about other accounts
        sessions.MapPost("/{sessionId:guid}/end", async Task<Results<NoContent, NotFound, ValidationProblem, UnauthorizedHttpResult>> (
            Guid sessionId, EndSessionsRequest request, ClaimsPrincipal principal, HttpContext http, CommandBus bus, CancellationToken ct) =>
        {
            if (request.CommandId == Guid.Empty)
            {
                return Invalid();
            }

            if (principal.UserId() is not { } userId)
            {
                return TypedResults.Unauthorized();
            }

            var outcome = await bus.SendAsync(
                new CommandEnvelope(request.CommandId, Guid.Empty, new EndSessions(userId, SessionEndReason.SignedOut, Only: sessionId), userId), ct);
            if (!outcome.IsAccepted)
            {
                return TypedResults.NotFound();
            }

            // Ending this very session is signing out here
            if (sessionId == principal.SessionId())
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }

            return TypedResults.NoContent();
        })
            .ProducesProblem(StatusCodes.Status404NotFound);

        sessions.MapPost("/end-others", async Task<Results<Ok<EndedSessions>, ValidationProblem, UnauthorizedHttpResult>> (
            EndSessionsRequest request, ClaimsPrincipal principal, CommandBus bus, CancellationToken ct) =>
        {
            if (request.CommandId == Guid.Empty)
            {
                return Invalid();
            }

            if (principal.UserId() is not { } userId || principal.SessionId() is not { } current)
            {
                return TypedResults.Unauthorized();
            }

            var outcome = await bus.SendAsync(
                new CommandEnvelope(request.CommandId, Guid.Empty, new EndSessions(userId, SessionEndReason.OtherDevices, Keep: current), userId), ct);
            return TypedResults.Ok(new EndedSessions(Ended(outcome)));
        });
    }

    /// <summary>How many sessions a command ended (a repeat names the same).</summary>
    public static int Ended(CommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Events.Select(e => e.Event).OfType<SessionsEnded>().Sum(e => e.SessionIds.Count);
    }

    /// <summary>Live sessions per account, for the admin's list.</summary>
    public static async Task<Dictionary<Guid, int>> LiveCountsAsync(GameEventDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return (await db.Sessions.AsNoTracking().Where(s => s.RevokedAt == null).ToListAsync(ct))
            .Where(s => s.IsActive(now))
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static ValidationProblem Invalid() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["commandId"] = ["A command id is required."] });
}
