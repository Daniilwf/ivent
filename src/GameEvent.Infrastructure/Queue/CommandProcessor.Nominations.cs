using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GameEvent.Infrastructure.Queue;

public sealed partial class CommandProcessor
{
    private static readonly string s_attackEventType = EventCatalog.Describe(typeof(HostileReceived)).Name;

    private static async Task<IReadOnlySet<Guid>?> ReadNominationCandidatesAsync(
        GameEventDbContext db, SeasonState state, ICommand command, CancellationToken ct)
    {
        if (command is not StartNominationPoll start || state.Status != SeasonStatus.Closing
            || state.Ruleset is not { Features.SeasonNominations: true, Features.Polls: true } rules
            || !rules.Nominations.Voted.Contains(start.Category)
            || rules.Nominations is not { MinCandidates: { } minimum, MaxCandidates: { } maximum }
            || start.CandidateIds.Count < minimum || start.CandidateIds.Count > maximum)
        {
            return null;
        }

        var candidates = await NominationCandidates.ListAsync(db, state.SeasonId, start.Category,
            rules.Features.Gallery, [.. start.CandidateIds], ct);
        return candidates.Select(candidate => candidate.Id).ToHashSet();
    }

    private static async Task<IReadOnlyList<HostileReceived>?> ReadNominationAttacksAsync(
        GameEventDbContext db, SeasonState state, ICommand command, CancellationToken ct)
    {
        if (command is not ChangeSeasonStatus { To: SeasonStatus.Finished }
            || state.Ruleset is not { Features.SeasonNominations: true } rules
            || !rules.Nominations.Auto.Contains(AutoNomination.MostAttacks))
        {
            return null;
        }

        var rows = await db.Events.AsNoTracking()
            .Where(row => row.SeasonId == state.SeasonId && row.Type == s_attackEventType && row.UndoneByEventId == null)
            .Select(row => new { row.Type, row.Version, row.Data })
            .ToListAsync(ct);
        return [.. rows.Select(row => (HostileReceived)EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)))];
    }

    // The global deletion and each affected season have their own commands. The sweep runs before any restore,
    // so even a restart between the deletion and the last cancellation cannot make a ballot active again.
    private async Task ReconcileDeletedNominationGamesAsync(
        Guid? seasonId, Guid? gameId, bool bestEffort, CancellationToken ct)
    {
        if (maintenance?.IsOn == true)
        {
            return;
        }

        try
        {
            foreach (var affected in await ReadDeletedNominationGamesAsync(seasonId, gameId, ct))
            {
                var fingerprint = $"nomination-cancellation:{affected.SeasonId:D}:{affected.GameId:D}:{affected.FirstPollId:D}";
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)).AsSpan(0, 16));
                try
                {
                    var outcome = await ProcessAsync(new CommandEnvelope(id, affected.SeasonId,
                        new CancelNominationsForDeletedGame(affected.GameId), AuthorId: null), ct);
                    if (!outcome.IsAccepted)
                    {
                        throw new InvalidOperationException($"Could not cancel the ballot: {outcome.Rejection?.Code}.");
                    }
                }
                catch
                {
                    _cache.Remove(affected.SeasonId);
                    throw;
                }
            }
        }
#pragma warning disable CA1031 // The pool change is already committed. Reconciliation retries before votes, results or restoration.
        catch (Exception exception) when (bestEffort && exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogNominationReconciliationFailed(logger, exception, seasonId, gameId);
        }
    }

    private async Task<IReadOnlyList<DeletedNominationGame>> ReadDeletedNominationGamesAsync(
        Guid? seasonId, Guid? gameId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await (
            from poll in db.Polls.AsNoTracking()
            join season in db.Seasons.AsNoTracking() on poll.SeasonId equals season.Id
            where season.Status == SeasonStatus.Closing && (seasonId == null || season.Id == seasonId)
                && poll.Status != PollStatus.Cancelled
            select new { poll.Id, poll.SeasonId, poll.DataJson }).ToListAsync(ct);
        var polls = pending
            .Select(row => new { row.Id, row.SeasonId, Nomination = JsonSerializer.Deserialize<Poll>(row.DataJson, EngineJson.Options)?.Nomination })
            .Where(row => row.Nomination is VotedNomination.GameOfSeason or VotedNomination.BestReview)
            .ToDictionary(row => row.Id);
        if (polls.Count == 0)
        {
            return [];
        }

        var ids = polls.Keys.ToArray();
        var options = await db.PollOptions.AsNoTracking().Where(option => ids.Contains(option.PollId))
            .Select(option => new { option.PollId, option.OptionId }).ToListAsync(ct);
        var candidates = options.Select(option => new
        {
            Poll = polls[option.PollId],
            CandidateId = Guid.Parse(option.OptionId),
        }).ToList();
        var reviewIds = candidates.Where(row => row.Poll.Nomination == VotedNomination.BestReview).Select(row => row.CandidateId).Distinct().ToArray();
        var reviewedGames = await db.Runs.AsNoTracking().Where(run => reviewIds.Contains(run.Id))
            .ToDictionaryAsync(run => run.Id, run => run.GameId, ct);
        var references = candidates.Select(row => new
        {
            row.Poll,
            GameId = row.Poll.Nomination == VotedNomination.GameOfSeason
                ? row.CandidateId
                : reviewedGames.GetValueOrDefault(row.CandidateId),
        }).Where(row => row.GameId != Guid.Empty && (gameId == null || row.GameId == gameId)).ToList();
        var gameIds = references.Select(row => row.GameId).Distinct().ToArray();
        var hidden = (await db.Games.AsNoTracking().Where(game => game.IsDeleted && gameIds.Contains(game.Id))
            .Select(game => game.Id).ToListAsync(ct)).ToHashSet();

        return [.. references
            .Where(row => hidden.Contains(row.GameId))
            .GroupBy(row => (row.Poll.SeasonId, row.GameId))
            .OrderBy(group => group.Key.SeasonId).ThenBy(group => group.Key.GameId)
            .Select(group => new DeletedNominationGame(group.Key.SeasonId, group.Key.GameId, group.Min(row => row.Poll.Id)))];
    }

    private sealed record DeletedNominationGame(Guid SeasonId, Guid GameId, Guid FirstPollId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation of deleted nomination games failed for season {SeasonId} and game {GameId}")]
    private static partial void LogNominationReconciliationFailed(ILogger logger, Exception exception, Guid? seasonId, Guid? gameId);
}
