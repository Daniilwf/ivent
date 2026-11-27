using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>Credited completion rewards and saved hours for the administrator's points-per-hour report (D-938).</summary>
internal static class AdminCompletionStatistics
{
    private static readonly string s_pointsType = EventCatalog.Describe(typeof(PointsChanged)).Name;

    internal readonly record struct Totals(long Points, decimal ExactHours, double? PromotedHours = null)
    {
        public double Hours => PromotedHours ?? (double)ExactHours;

        // A legal tiny decimal duration must not overflow the derived rate.
        public double? PointsPerHour => Hours > 0 ? (double)Points / Hours : null;

        public Totals AddHours(decimal hours)
        {
            if (PromotedHours is { } promoted)
            {
                return this with { PromotedHours = promoted + (double)hours };
            }

            try
            {
                // Keep ordinary sums such as 0.1 + 0.2 decimal-exact before serializing the aggregate.
                return this with { ExactHours = checked(ExactHours + hours) };
            }
            catch (OverflowException)
            {
                // Imported or queued historical durations can exceed the aggregate decimal range.
                return this with { PromotedHours = (double)ExactHours + (double)hours };
            }
        }
    }

    internal static async Task<Dictionary<Guid, Totals>> ReadAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            // A deferred WAL snapshot keeps hours and awarded points from different corrections from being combined,
            // without reserving the writer's lock for this report.
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            await using var snapshot = connection.BeginTransaction(deferred: true);
            await using var enlistment = await db.Database.UseTransactionAsync(snapshot, ct);
            var runs = await (
                from run in db.Runs.AsNoTracking()
                join proof in db.Proofs.AsNoTracking() on run.Id equals proof.RunId
                where run.SeasonId == seasonId && proof.SeasonId == seasonId
                    && run.Status == RunStatus.Completed && !run.FreeMode && proof.Status == ProofStatus.Approved
                select new { run.Id, run.PlayerId, run.Hours, run.PartnerId, run.CoopJson }).ToListAsync(ct);
            if (runs.Count == 0)
            {
                await snapshot.CommitAsync(ct);
                return [];
            }

            var changes = await db.Events.AsNoTracking()
                .Where(row => row.SeasonId == seasonId && row.Type == s_pointsType && row.UndoneByEventId == null)
                .Select(row => new { row.Type, row.Version, row.Data }).ToListAsync(ct);
            await snapshot.CommitAsync(ct);

            var participants = new HashSet<(Guid RunId, Guid PlayerId)>();
            var totals = new Dictionary<Guid, Totals>();
            foreach (var run in runs)
            {
                // Legacy records without a usable duration cannot contribute a rate or its numerator.
                if (run.Hours is not { } hours || hours <= 0)
                {
                    continue;
                }

                AddRun(run.Id, run.PlayerId, hours);
                if (run.PartnerId is { } partnerId && run.CoopJson is { } coopJson
                    && JsonSerializer.Deserialize<CoopPartner>(coopJson, EngineJson.Options)?.Shares is not null)
                {
                    AddRun(run.Id, partnerId, hours);
                }
            }

            foreach (var row in changes)
            {
                var change = (PointsChanged)EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data));
                if (change.RunId is not { } runId || !participants.Contains((runId, change.PlayerId))
                    || change.Reason is not (PointsReason.CompletionRoll or PointsReason.RunCorrection or PointsReason.DiceModified))
                {
                    continue;
                }

                var previous = totals[change.PlayerId];
                totals[change.PlayerId] = previous with { Points = previous.Points + change.Delta };
            }

            return totals;

            void AddRun(Guid runId, Guid playerId, decimal hours)
            {
                if (!participants.Add((runId, playerId)))
                {
                    return;
                }

                var previous = totals.GetValueOrDefault(playerId);
                totals[playerId] = previous.AddHours(hours);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
