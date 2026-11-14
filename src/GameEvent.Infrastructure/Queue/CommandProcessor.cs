using System.Security.Cryptography;
using System.Text.Json;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameEvent.Infrastructure.Queue;

/// <summary>
/// The only consumer of the command queue. One command, one transaction: its events go into the log
/// and the projection tables change together, or nothing is written (invariant 3).
/// Season state is cached in memory and replaced only after a successful commit.
/// </summary>
public sealed partial class CommandProcessor(
    CommandBus queue,
    IDbContextFactory<GameEventDbContext> dbFactory,
    IClock clock,
    IRandomSource random,
    IIdGenerator ids,
    Accounts.IPasswords passwords,
    IEnumerable<ICommittedEventsListener> listeners,
    ILogger<CommandProcessor> logger,
    Site.MaintenanceMode? maintenance = null,
    TimeProvider? time = null,
    IReadOnlyList<Engine.Effects.ITriggerHandler>? triggers = null) : BackgroundService
{
    // triggers: test handlers that react to every command's events (D-24); none in the product

    /// <summary>
    /// The time of sessions (D-469): the wall clock the cookie's own expiry uses, not the game's <see cref="IClock"/>, which
    /// the test copy moves by months — a moved game clock must not sign the admin out.
    /// </summary>
    private DateTimeOffset SessionNow => (time ?? TimeProvider.System).GetUtcNow();

    private readonly Dictionary<Guid, (SeasonState State, long LastSequence)> _cache = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        queue.Consuming(true);
        try
        {
            await SettleLeftPendingAsync(stoppingToken);
            await foreach (var pending in queue.Reader.ReadAllAsync(stoppingToken))
            {
                queue.Taken();
                queue.Running(true);
                try
                {
                    await HandleAsync(pending, stoppingToken);
                }
                finally
                {
                    queue.Running(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: commands still waiting are cancelled below, none is left hanging.
        }
        finally
        {
            queue.Consuming(false);
            while (queue.Reader.TryRead(out var left))
            {
                queue.Taken();
                left.Completion.TrySetCanceled(CancellationToken.None);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        return base.StopAsync(cancellationToken);
    }

    private async Task HandleAsync(CommandBus.Pending pending, CancellationToken ct)
    {
        try
        {
            var outcome = await ProcessAsync(pending.Envelope, ct);

            // An approved game suggestion is in the pool by the time the admin's approval answers (D-572)
            if (outcome.IsAccepted && !outcome.IsDuplicate && pending.Envelope.SeasonId != Guid.Empty)
            {
                await AddApprovedGamesAsync(pending.Envelope.SeasonId, outcome, ct);
            }

            pending.Completion.TrySetResult(outcome);

            // What the command queued for everyone is settled right after it, before the next command (D-537)
            if (outcome.IsAccepted && !outcome.IsDuplicate && pending.Envelope.SeasonId != Guid.Empty)
            {
                await SettleForAllAsync(pending.Envelope.SeasonId, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown in the middle: the transaction was not committed, the caller may retry.
            _cache.Remove(pending.Envelope.SeasonId);
            pending.Completion.TrySetCanceled(ct);
        }
#pragma warning disable CA1031 // one failed command must not stop the queue; the caller gets the exception
        catch (Exception e)
#pragma warning restore CA1031
        {
            // If the failure came from the commit itself, the database may hold the command after all:
            // the next command of the season rebuilds its state from the log instead of trusting the cache.
            _cache.Remove(pending.Envelope.SeasonId);
            LogCommandFailed(logger, e, pending.Envelope.CommandId, pending.Envelope.Command.GetType().Name);
            pending.Completion.TrySetException(e);
        }
    }

    private async Task<CommandOutcome> ProcessAsync(CommandEnvelope envelope, CancellationToken ct)
    {
        // While the site only reads (D-121) nothing is written: not a player's action, not the scheduler's, not the admin's
        // Signing in and out stays open (D-468): the admin signs in to end the mode, and an ended session must end
        if (maintenance?.IsOn == true && envelope.Command is not Accounts.IOpenInMaintenance)
        {
            return Rejected(Site.MaintenanceMode.Code, "The site is under maintenance and only reads; try again in a minute.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var commandType = envelope.Command.GetType().Name;
        if (Hash(envelope.Command) is not { } commandHash)
        {
            // A null in a non-nullable field: the endpoints refuse such input first; anything else is a clean rejection.
            return Rejected(RejectionCodes.CommandInvalid, $"Command {commandType} cannot be serialized.");
        }

        var earlier = await db.Events.AsNoTracking()
            .Where(e => e.CommandId == envelope.CommandId)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        if (earlier.Count > 0)
        {
            // A repeat is the same command, with the same body, by the same author for the same season;
            // anything else reusing the id is refused.
            if (!earlier.All(e => e.SeasonId == envelope.SeasonId && e.CommandType == commandType
                    && e.CommandHash == commandHash && e.AuthorId == envelope.AuthorId))
            {
                return Rejected(RejectionCodes.CommandIdReused, $"Command id {envelope.CommandId} was used by another command.");
            }

            // The same command, but undone since (D-104): repeating it does not bring it back.
            return earlier.All(e => e.UndoneByEventId is not null)
                ? Rejected(RejectionCodes.CommandUndone, $"Command {envelope.CommandId} was undone; send it again with a new id.")
                : new CommandOutcome(true, true, null, [.. earlier.Select(ToLogged)]);
        }

        // Account and file commands belong to the global log (D-106, D-108); season commands to their season.
        if (envelope.Command is IGlobalCommand global)
        {
            return envelope.SeasonId == Guid.Empty
                ? await ProcessGlobalAsync(db, envelope, global, commandType, commandHash, ct)
                : Rejected(RejectionCodes.SeasonMismatch, "Account and file commands go to the global log (no season).");
        }

        if (envelope.SeasonId == Guid.Empty)
        {
            return Rejected(RejectionCodes.SeasonMismatch, "Season commands need a season id; Guid.Empty is the global log.");
        }

        if (envelope.Command is CreateSeason create && create.SeasonId != envelope.SeasonId)
        {
            return Rejected(RejectionCodes.SeasonMismatch, $"CreateSeason {create.SeasonId} sent to season {envelope.SeasonId}.");
        }

        if (!_cache.TryGetValue(envelope.SeasonId, out var cached))
        {
            cached = await EventLogReader.ReplaySeasonAsync(db, envelope.SeasonId, ct);
        }

        // A settlement for everyone left pending (the server stopped between its cause and its batches) goes first (D-537)
        if (envelope.Command is not SettleForAll && ForAllSettlements.Next(cached.State) is not null)
        {
            _cache[envelope.SeasonId] = cached;
            await SettleForAllAsync(envelope.SeasonId, ct);
            if (!_cache.TryGetValue(envelope.SeasonId, out cached))
            {
                cached = await EventLogReader.ReplaySeasonAsync(db, envelope.SeasonId, ct);
            }
        }

        // The rules come from the season's own log (D-82); a new season brings them in CreateSeason.
        // An undo decides on the whole log by command (D-104); other commands need only the state.
        var history = envelope.Command is Engine.Undo.UndoCommand ? await EventLogReader.ReadCommandsAsync(db, envelope.SeasonId, ct) : null;
        var context = new EngineContext(
            clock, random, ids, await PoolReader.LoadAsync(db, ct), Triggers: triggers, History: history, TimeZones: Kernel.SystemTimeZones.Instance);

        var result = SeasonEngine.Execute(cached.State, envelope.Command, context);
        if (!result.IsAccepted)
        {
            return new CommandOutcome(false, false, result.Rejection, []);
        }

        if (result.State.SeasonId != envelope.SeasonId)
        {
            throw new InvalidOperationException(
                $"Command {commandType} produced state of season {result.State.SeasonId} in the log of {envelope.SeasonId}.");
        }

        var now = clock.UtcNow;
        var sequence = cached.LastSequence;
        var records = result.Events.Select(e =>
        {
            var stored = EventCodec.Encode(e);
            return new GameEventRecord
            {
                SeasonId = envelope.SeasonId,
                Sequence = ++sequence,
                CommandId = envelope.CommandId,
                CommandType = commandType,
                CommandHash = commandHash,
                Type = stored.Type,
                Version = stored.Version,
                Data = stored.Data,
                AuthorId = envelope.AuthorId,
                OccurredAt = now,
            };
        }).ToList();

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            db.Events.AddRange(records);
            await SeasonProjection.WriteAsync(db, cached.State, result.State, now, envelope.AuthorId, ct);
            await db.SaveChangesAsync(ct);

            // The undone command's events stay in the log, marked with the event that undid them (D-104).
            foreach (var (record, undone) in records.Zip(result.Events).Where(x => x.Second is Engine.Undo.CommandUndone)
                .Select(x => (x.First, (Engine.Undo.CommandUndone)x.Second)))
            {
                await db.Events
                    .Where(e => e.SeasonId == envelope.SeasonId && e.CommandId == undone.CommandId)
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.UndoneByEventId, record.Id), ct);
            }

            await transaction.CommitAsync(ct);
        }

        _cache[envelope.SeasonId] = (result.State, sequence);

        var logged = records.Zip(result.Events, (r, e) => new LoggedEvent(r.SeasonId, r.Sequence, r.CommandId, e, r.OccurredAt)).ToList();
        await NotifyAsync(envelope, logged, ct);
        return new CommandOutcome(true, false, null, logged);
    }

    private async Task NotifyAsync(CommandEnvelope envelope, IReadOnlyList<LoggedEvent> logged, CancellationToken ct)
    {
        foreach (var listener in listeners)
        {
            // The command is committed: a failing listener (a broadcast) must not turn it into a failure.
            try
            {
                await listener.OnCommittedAsync(logged, ct);
            }
#pragma warning disable CA1031
            catch (Exception e)
#pragma warning restore CA1031
            {
                LogListenerFailed(logger, e, envelope.CommandId, listener.GetType().Name);
            }
        }
    }

    /// <summary>
    /// The batches of the season's pending settlements for everyone (D-537): each a system command of its own (no author)
    /// through this same consumer, one transaction each, until none waits. The id comes from the settlement and the batch,
    /// so a batch is never written twice. A refused batch (the site under maintenance, a player choosing a branch) stops
    /// here; the next command of the season tries again first.
    /// </summary>
    private async Task SettleForAllAsync(Guid seasonId, CancellationToken ct)
    {
        for (var batches = 0; batches < MaxBatchesInARow; batches++)
        {
            if (!_cache.TryGetValue(seasonId, out var cached) || ForAllSettlements.Next(cached.State) is not { } settlementId)
            {
                return;
            }

            var batch = cached.State.ForAll[settlementId].Batches + 1;
            var envelope = new CommandEnvelope(SettleCommandId(seasonId, settlementId, batch), seasonId, new SettleForAll(settlementId), AuthorId: null);
            CommandOutcome outcome;
            try
            {
                outcome = await ProcessAsync(envelope, ct);
            }
#pragma warning disable CA1031 // a failed batch must not fail its cause, which is committed: the next command tries again
            catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
            {
                _cache.Remove(seasonId);
                LogCommandFailed(logger, e, envelope.CommandId, nameof(SettleForAll));
                return;
            }

            if (!outcome.IsAccepted || outcome.IsDuplicate)
            {
                LogSettlementStopped(logger, seasonId, settlementId, outcome.Rejection?.Code);
                return;
            }
        }
    }

    /// <summary>
    /// At start, before the first command: the settlements for everyone a stop left between their cause and their batches
    /// are settled at once (D-702), so a quiet season does not wait for its next command.
    /// </summary>
    private async Task SettleLeftPendingAsync(CancellationToken ct)
    {
        List<Guid> running;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            running = await db.Seasons.AsNoTracking()
                .Where(s => s.Status == SeasonStatus.Active || s.Status == SeasonStatus.Closing)
                .Select(s => s.Id)
                .ToListAsync(ct);
            foreach (var seasonId in running)
            {
                var replayed = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
                if (ForAllSettlements.Next(replayed.State) is not null)
                {
                    _cache[seasonId] = replayed;
                    await SettleForAllAsync(seasonId, ct);
                }
            }
        }
#pragma warning disable CA1031 // a failed check at start must not stop the queue: the next command of the season settles first
        catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _cache.Clear();
            LogStartSettlementFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Pending settlements for everyone were not settled at start; the next command of each season settles them")]
    private static partial void LogStartSettlementFailed(ILogger logger, Exception exception);

    // A settlement of the 50 events a batch takes ends in a few batches; this only stops a loop that never ends
    private const int MaxBatchesInARow = 1000;

    private static Guid SettleCommandId(Guid seasonId, Guid settlementId, int batch) =>
        new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"settle-for-all:{seasonId:N}:{settlementId:N}:{batch}")).AsSpan(0, 16));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settlement {SettlementId} of season {SeasonId} stopped: {Code}")]
    private static partial void LogSettlementStopped(ILogger logger, Guid seasonId, Guid settlementId, string? code);

    private static CommandOutcome Rejected(string code, string detail) => new(false, false, new Rejection(code, detail), []);

    private static LoggedEvent ToLogged(GameEventRecord r) =>
        new(r.SeasonId, r.Sequence, r.CommandId, EventCodec.Decode(new StoredEvent(r.Type, r.Version, r.Data)), r.OccurredAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listener {Listener} failed after command {CommandId} was committed")]
    private static partial void LogListenerFailed(ILogger logger, Exception exception, Guid commandId, string listener);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {CommandId} ({CommandType}) failed; its transaction was not confirmed, the season will be reloaded from the log")]
    private static partial void LogCommandFailed(ILogger logger, Exception exception, Guid commandId, string commandType);

    // The command's JSON is deterministic within one build (D-95); a retry across a deploy may be refused, never doubled.
    private static string? Hash(ICommand command)
    {
        try
        {
            // A secret never goes into the fingerprint: an unsalted hash of a password would sit in the log (D-106)
            var hashed = command is Accounts.ISecretCommand secret ? secret.WithoutSecret() : command;
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hashed, hashed.GetType(), EngineJson.Options)));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
