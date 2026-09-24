using System.Collections.Immutable;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Seasons;

/// <summary>
/// Writes the difference between season states into the projection tables, inside the command's transaction.
/// Only changed players and runs are touched.
/// </summary>
internal static class SeasonProjection
{
    public static async Task WriteAsync(
        GameEventDbContext db, SeasonState before, SeasonState after, string rulesetJson, int rulesetVersion, DateTimeOffset now, CancellationToken ct)
    {
        if (!before.IsCreated && after.IsCreated)
        {
            db.Seasons.Add(new SeasonRecord
            {
                Id = after.SeasonId,
                Status = "Active",
                RulesetVersion = rulesetVersion,
                RulesetJson = rulesetJson,
                CreatedAt = now,
            });
        }

        foreach (var (id, player) in after.Players)
        {
            if (before.Players.TryGetValue(id, out var old) && old == player)
            {
                continue;
            }

            var record = await db.SeasonPlayers.FindAsync([id], ct);
            if (record is null)
            {
                record = new SeasonPlayerRecord { Id = id, SeasonId = after.SeasonId, UserId = player.UserId, Name = player.Name, CellId = player.CellId };
                db.SeasonPlayers.Add(record);
            }

            EnsureSameSeason(record.SeasonId, after.SeasonId, "player", id);

            record.Name = player.Name;
            record.CellId = player.CellId;
            record.Points = player.Points;
            record.Phase = player.Phase;
            record.OfferJson = player.Offer is null ? null : JsonSerializer.Serialize(player.Offer, EngineJson.Options);
            record.ActiveRunId = player.ActiveRunId;
        }

        foreach (var (id, run) in after.Runs)
        {
            if (before.Runs.TryGetValue(id, out var old) && old == run)
            {
                continue;
            }

            var record = await db.Runs.FindAsync([id], ct);
            if (record is null)
            {
                record = new RunRecord { Id = id, SeasonId = after.SeasonId, SnapshotJson = "", DiceJson = "" };
                db.Runs.Add(record);
            }

            EnsureSameSeason(record.SeasonId, after.SeasonId, "run", id);

            record.PlayerId = run.PlayerId;
            record.GameId = run.GameId;
            record.Status = run.Status;
            record.SnapshotJson = JsonSerializer.Serialize(run.Snapshot, EngineJson.Options);
            record.RolledAt = run.RolledAt;
            record.StartedAt = run.StartedAt;
            record.Difficulty = run.Difficulty;
            record.Hours = run.Hours;
            record.DiceJson = JsonSerializer.Serialize(run.Dice, EngineJson.Options);
        }
    }

    // Player ids are participation ids (one per season), never user ids: a clash means a bug upstream.
    private static void EnsureSameSeason(Guid stored, Guid current, string what, Guid id)
    {
        if (stored != current)
        {
            throw new InvalidOperationException($"The {what} {id} belongs to season {stored}, not {current}.");
        }
    }

    /// <summary>
    /// Rebuilds the state a projection describes, for the integrity check: it must equal the fold of the log.
    /// The map is not projected; it is taken from <paramref name="replayed"/>.
    /// </summary>
    public static async Task<SeasonState> ReadAsync(GameEventDbContext db, SeasonState replayed, CancellationToken ct)
    {
        var players = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == replayed.SeasonId).ToListAsync(ct);
        var runs = await db.Runs.AsNoTracking().Where(r => r.SeasonId == replayed.SeasonId).ToListAsync(ct);

        return replayed with
        {
            Players = players.ToImmutableSortedDictionary(
                p => p.Id,
                p => new SeasonPlayer(
                    p.Id, p.UserId, p.Name, p.CellId, p.Points, p.Phase,
                    p.OfferJson is null ? null : JsonSerializer.Deserialize<RollOffer>(p.OfferJson, EngineJson.Options),
                    p.ActiveRunId)),
            Runs = runs.ToImmutableSortedDictionary(
                r => r.Id,
                r => new RunState(
                    r.Id, r.PlayerId, r.GameId, r.Status,
                    JsonSerializer.Deserialize<RunSnapshot>(r.SnapshotJson, EngineJson.Options)!,
                    r.RolledAt, r.StartedAt, r.Difficulty, r.Hours,
                    JsonSerializer.Deserialize<EquatableArray<Die>>(r.DiceJson, EngineJson.Options))),
        };
    }
}
