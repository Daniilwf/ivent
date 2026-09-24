using System.Collections.Immutable;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
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
        GameEventDbContext db, SeasonState before, SeasonState after, DateTimeOffset now, Guid? authorId, CancellationToken ct)
    {
        if (before.RulesetVersion != after.RulesetVersion)
        {
            db.Rulesets.Add(new RulesetRecord
            {
                SeasonId = after.SeasonId,
                Version = after.RulesetVersion,
                Json = JsonSerializer.Serialize(after.Rules, EngineJson.Options),
                CreatedAt = now,
                AuthorId = authorId,
            });
        }

        if (!before.IsCreated && after.IsCreated)
        {
            db.Seasons.Add(new SeasonRecord
            {
                Id = after.SeasonId,
                Name = after.Name,
                Status = after.Status,
                Deadline = after.Deadline,
                RulesetVersion = after.RulesetVersion,
                RulesetJson = JsonSerializer.Serialize(after.Rules, EngineJson.Options),
                CreatedAt = now,
            });
        }
        else if (before.RulesetVersion != after.RulesetVersion || before.Status != after.Status
            || before.Deadline != after.Deadline || before.Name != after.Name)
        {
            var season = await db.Seasons.FindAsync([after.SeasonId], ct)
                ?? throw new InvalidOperationException($"Season {after.SeasonId} has no projection row.");
            season.Name = after.Name;
            season.Status = after.Status;
            season.Deadline = after.Deadline;
            if (season.RulesetVersion != after.RulesetVersion)
            {
                season.RulesetVersion = after.RulesetVersion;
                season.RulesetJson = JsonSerializer.Serialize(after.Rules, EngineJson.Options);
            }
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
                record = new SeasonPlayerRecord
                {
                    Id = id,
                    SeasonId = after.SeasonId,
                    UserId = player.UserId,
                    Name = player.Name,
                    CellId = player.CellId,
                    ResourcesJson = JsonSerializer.Serialize(player.Resources, EngineJson.Options),
                    PathJson = JsonSerializer.Serialize(player.Path, EngineJson.Options),
                };
                db.SeasonPlayers.Add(record);
            }

            EnsureSameSeason(record.SeasonId, after.SeasonId, "player", id);

            record.Name = player.Name;
            record.CellId = player.CellId;
            record.Points = player.Points;
            record.Coins = player.Coins;
            record.ResourcesJson = JsonSerializer.Serialize(player.Resources, EngineJson.Options);
            record.IsInactive = player.IsInactive;
            record.PathJson = JsonSerializer.Serialize(player.Path, EngineJson.Options);
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
        var season = await db.Seasons.AsNoTracking().SingleAsync(s => s.Id == replayed.SeasonId, ct);
        var players = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == replayed.SeasonId).ToListAsync(ct);
        var runs = await db.Runs.AsNoTracking().Where(r => r.SeasonId == replayed.SeasonId).ToListAsync(ct);

        return replayed with
        {
            Name = season.Name,
            Status = season.Status,
            Deadline = season.Deadline,
            RulesetVersion = season.RulesetVersion,
            Ruleset = JsonSerializer.Deserialize<Ruleset>(season.RulesetJson, EngineJson.Options),
            Players = players.ToImmutableSortedDictionary(
                p => p.Id,
                p => new SeasonPlayer(
                    p.Id, p.UserId, p.Name, p.CellId, p.Points, p.Coins,
                    JsonSerializer.Deserialize<ResourceBag>(p.ResourcesJson, EngineJson.Options), p.IsInactive,
                    JsonSerializer.Deserialize<PlayerPath>(p.PathJson, EngineJson.Options)!, p.Phase,
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
