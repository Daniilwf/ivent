using System.Threading.Channels;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameEvent.Web.Realtime;

/// <summary>Sent to everyone watching a season after each committed command: refetch what you show.</summary>
public sealed record SeasonUpdate(Guid SeasonId, long FromSequence, long ToSequence, IReadOnlyList<string> Types);

/// <summary>Real-time updates: a client joins the group of the season it shows. The season log is public.</summary>
[Authorize]
public sealed class SeasonHub : Hub
{
    public const string Path = "/hubs/season";
    public const string UpdateMethod = "seasonUpdated";

    public static string Group(Guid seasonId) => $"season:{seasonId}";

    public Task Join(Guid seasonId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(seasonId));

    public Task Leave(Guid seasonId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(seasonId));
}

/// <summary>
/// Hands committed events to <see cref="SeasonBroadcastWorker"/> without waiting: a slow client connection
/// must never hold up the command queue or the HTTP answer.
/// </summary>
public sealed class SeasonBroadcaster : ICommittedEventsListener
{
    private readonly Channel<SeasonUpdate> _updates = Channel.CreateUnbounded<SeasonUpdate>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    internal ChannelReader<SeasonUpdate> Updates => _updates.Reader;

    public Task OnCommittedAsync(IReadOnlyList<LoggedEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count > 0)
        {
            _updates.Writer.TryWrite(new SeasonUpdate(
                events[0].SeasonId,
                events[0].Sequence,
                events[^1].Sequence,
                [.. events.Select(e => EventCatalog.Describe(e.Event.GetType()).Name)]));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Sends season updates to hub groups, in commit order, off the command queue.</summary>
public sealed partial class SeasonBroadcastWorker(
    SeasonBroadcaster broadcaster, IHubContext<SeasonHub> hub, ILogger<SeasonBroadcastWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var update in broadcaster.Updates.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await hub.Clients.Group(SeasonHub.Group(update.SeasonId)).SendAsync(SeasonHub.UpdateMethod, update, stoppingToken);
                }
#pragma warning disable CA1031 // a failed broadcast is logged; clients catch up on reconnect (E3)
                catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogBroadcastFailed(logger, e, update.SeasonId, update.ToSequence);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broadcast of season {SeasonId} up to {Sequence} failed")]
    private static partial void LogBroadcastFailed(ILogger logger, Exception exception, Guid seasonId, long sequence);
}
