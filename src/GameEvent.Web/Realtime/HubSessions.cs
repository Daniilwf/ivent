using System.Collections.Concurrent;
using GameEvent.Engine.Accounts;
using GameEvent.Infrastructure.Queue;
using Microsoft.AspNetCore.SignalR;

namespace GameEvent.Web.Realtime;

/// <summary>
/// The hub connections of each account (D-67, D-106): a new security stamp — password reset or change, role change,
/// deletion — closes them, so an ended session stops getting updates at once; the client reconnects with the cookie it
/// has and is refused if that session ended.
/// </summary>
public sealed class HubSessions : ICommittedEventsListener
{
    private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new();

    public void Add(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _connections[context.ConnectionId] = context;
    }

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>How many connections an account has open (for tests and diagnostics).</summary>
    public int CountFor(Guid userId) => _connections.Values.Count(c => c.UserIdentifier == userId.ToString());

    public Task OnCommittedAsync(IReadOnlyList<LoggedEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var e in events)
        {
            var userId = e.Event switch
            {
                AccountPasswordReset x => x.UserId,
                AccountPasswordChanged x => x.UserId,
                AccountChanged x => x.UserId,
                AccountDeleted x => x.UserId,
                _ => (Guid?)null,
            };
            if (userId is { } id)
            {
                foreach (var connection in _connections.Values.Where(c => c.UserIdentifier == id.ToString()).ToList())
                {
                    connection.Abort();
                }
            }
        }

        return Task.CompletedTask;
    }
}
