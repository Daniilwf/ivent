using System.Collections.Immutable;
using System.Text.Json;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
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

        // The final table is written once, when the season finishes (D-101).
        if (before.Result is null && after.Result is { } result)
        {
            db.SeasonResults.AddRange(result.Select((r, i) => new SeasonResultRecord
            {
                SeasonId = after.SeasonId,
                Row = i,
                PlayerId = r.PlayerId,
                Place = r.Place,
                Points = r.Points,
                CellsToFinish = r.CellsToFinish,
                IsFirst = r.IsFirst,
                Provisional = r.Provisional,
            }));
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
            record.ChoiceJson = player.Choice is null ? null : JsonSerializer.Serialize(player.Choice, EngineJson.Options);
            record.ActiveRunId = player.ActiveRunId;
            record.RerollsThisRoll = player.RerollsThisRoll;
            record.FinishOrder = player.Finish?.Order;
            record.FinishRunId = player.Finish?.RunId;
            record.FinishedAt = player.Finish?.FinishedAt;
            record.Frozen = player.Finish?.Frozen ?? false;
            record.FinishBonus = player.Finish?.Bonus ?? 0;
            record.FinishSurplus = player.Finish?.Surplus ?? 0;
            record.FinishBonusRulesJson = player.Finish?.BonusRules is { } bonusRules ? JsonSerializer.Serialize(bonusRules, EngineJson.Options) : null;
            record.FinishApprovalRequired = player.Finish?.ApprovalRequired;
            record.PointsTick = player.PointsTick;

            // Exclusions grow within a season (D-08), a tech reroll turned into a drop changes its reason (D-11), an undo
            // takes back those its command added (D-104).
            var known = old?.Exclusions.ToDictionary(x => x.GameId, x => x.Reason) ?? [];
            foreach (var gone in known.Keys.Where(game => player.Exclusions.All(x => x.GameId != game)))
            {
                db.Exclusions.Remove(await db.Exclusions.FindAsync([id, gone], ct)
                    ?? throw new InvalidOperationException($"Exclusion of game {gone} for player {id} has no projection row."));
            }

            foreach (var exclusion in player.Exclusions)
            {
                if (!known.TryGetValue(exclusion.GameId, out var reason))
                {
                    db.Exclusions.Add(new PlayerGameExclusionRecord { PlayerId = id, GameId = exclusion.GameId, Reason = exclusion.Reason });
                }
                else if (reason != exclusion.Reason)
                {
                    var row = await db.Exclusions.FindAsync([id, exclusion.GameId], ct)
                        ?? throw new InvalidOperationException($"Exclusion of game {exclusion.GameId} for player {id} has no projection row.");
                    row.Reason = exclusion.Reason;
                }
            }
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
                record = new RunRecord { Id = id, SeasonId = after.SeasonId, SnapshotJson = "", DiceJson = "", ChallengeDiceJson = "" };
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
            record.ChallengeDiceJson = JsonSerializer.Serialize(run.ChallengeDice, EngineJson.Options);
            record.HoursSource = run.HoursSource;
            record.CompletedAt = run.CompletedAt;
            record.ReachedFinish = run.ReachedFinish;
            record.Moved = run.Moved;
            record.AfterFinish = run.AfterFinish;
            record.FreeMode = run.FreeMode;

            if (run.Proof is null && before.Runs.GetValueOrDefault(id)?.Proof is not null && await db.Proofs.FindAsync([id], ct) is { } undoneProof)
            {
                db.Proofs.Remove(undoneProof);
            }

            if (run.Proof is { } proof && proof != before.Runs.GetValueOrDefault(id)?.Proof)
            {
                var proofRow = await db.Proofs.FindAsync([id], ct);
                if (proofRow is null)
                {
                    proofRow = new ProofRecord { RunId = id, SeasonId = after.SeasonId, PlayerId = run.PlayerId, LinksJson = "[]" };
                    db.Proofs.Add(proofRow);
                }

                proofRow.Status = proof.Status;
                proofRow.LinksJson = JsonSerializer.Serialize(proof.Links, EngineJson.Options);
                proofRow.FilesJson = JsonSerializer.Serialize(proof.Files, EngineJson.Options);
                proofRow.Note = proof.Note;
                proofRow.WitnessId = proof.WitnessId;
                proofRow.SubmittedAt = proof.SubmittedAt;
                proofRow.Comment = proof.Comment;
            }

            var oldRun = before.Runs.GetValueOrDefault(id);
            if (run.Review is null && oldRun?.Review is not null && await db.Reviews.FindAsync([id], ct) is { } undoneReview)
            {
                db.Reviews.Remove(undoneReview);
            }

            if (run.Review is { } review && review != oldRun?.Review)
            {
                var row = await db.Reviews.FindAsync([id], ct);
                if (row is null)
                {
                    row = new ReviewRecord { RunId = id, SeasonId = after.SeasonId, PlayerId = run.PlayerId, GameId = run.GameId };
                    db.Reviews.Add(row);
                }

                row.Rating = review.Rating;
                row.Text = review.Text;
            }
        }

        // An undo removes what the undone command created (D-104): runs with their proof and review, then players.
        foreach (var id in before.Runs.Keys.Where(id => !after.Runs.ContainsKey(id)))
        {
            if (await db.Proofs.FindAsync([id], ct) is { } proofRow)
            {
                db.Proofs.Remove(proofRow);
            }

            if (await db.Reviews.FindAsync([id], ct) is { } reviewRow)
            {
                db.Reviews.Remove(reviewRow);
            }

            db.Runs.Remove(await db.Runs.FindAsync([id], ct) ?? throw new InvalidOperationException($"Run {id} has no projection row."));
        }

        foreach (var id in before.Players.Keys.Where(id => !after.Players.ContainsKey(id)))
        {
            db.Exclusions.RemoveRange(await db.Exclusions.Where(x => x.PlayerId == id).ToListAsync(ct));
            db.SeasonPlayers.Remove(await db.SeasonPlayers.FindAsync([id], ct)
                ?? throw new InvalidOperationException($"Player {id} has no projection row."));
        }

        // The table holds pending manual effects: created ones are added, resolved ones leave it.
        foreach (var (id, effect) in after.ManualEffects.Where(x => !before.ManualEffects.ContainsKey(x.Key)))
        {
            db.ManualEffects.Add(new PendingManualEffectRecord
            {
                Id = id,
                SeasonId = after.SeasonId,
                PlayerId = effect.PlayerId,
                DrawEvent = effect.DrawEvent,
                Source = effect.Source,
                RunId = effect.RunId,
            });
        }

        foreach (var id in before.ManualEffects.Keys.Where(id => !after.ManualEffects.ContainsKey(id)))
        {
            db.ManualEffects.Remove(await db.ManualEffects.FindAsync([id], ct)
                ?? throw new InvalidOperationException($"Manual effect {id} has no projection row."));
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
    /// The map, the count of finishes so far and the count of points changes are not projected; they are taken from
    /// <paramref name="replayed"/>.
    /// </summary>
    public static async Task<SeasonState> ReadAsync(GameEventDbContext db, SeasonState replayed, CancellationToken ct)
    {
        var season = await db.Seasons.AsNoTracking().SingleAsync(s => s.Id == replayed.SeasonId, ct);
        var players = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == replayed.SeasonId).ToListAsync(ct);
        var runs = await db.Runs.AsNoTracking().Where(r => r.SeasonId == replayed.SeasonId).ToListAsync(ct);
        var effects = await db.ManualEffects.AsNoTracking().Where(x => x.SeasonId == replayed.SeasonId).ToListAsync(ct);
        var reviews = await db.Reviews.AsNoTracking().Where(x => x.SeasonId == replayed.SeasonId).ToDictionaryAsync(x => x.RunId, ct);
        var proofs = await db.Proofs.AsNoTracking().Where(x => x.SeasonId == replayed.SeasonId).ToDictionaryAsync(x => x.RunId, ct);
        var result = await db.SeasonResults.AsNoTracking().Where(x => x.SeasonId == replayed.SeasonId).OrderBy(x => x.Row).ToListAsync(ct);
        var playerIds = players.Select(p => p.Id).ToList();
        var exclusions = (await db.Exclusions.AsNoTracking().Where(x => playerIds.Contains(x.PlayerId)).ToListAsync(ct))
            .ToLookup(x => x.PlayerId);

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
                    p.ChoiceJson is null ? null : JsonSerializer.Deserialize<PendingChoice>(p.ChoiceJson, EngineJson.Options),
                    [.. exclusions[p.Id].OrderBy(r => r.GameId).Select(r => new GameExclusion(r.GameId, r.Reason))],
                    p.RerollsThisRoll,
                    p.FinishOrder is { } order
                        ? new Engine.Finish.FinishState(
                            order, p.FinishRunId!.Value, p.FinishedAt!.Value, p.Frozen, p.FinishBonus, p.FinishSurplus,
                            p.FinishBonusRulesJson is null ? null : JsonSerializer.Deserialize<Engine.Finish.FinishBonusRules>(p.FinishBonusRulesJson, EngineJson.Options),
                            p.FinishApprovalRequired)
                        : null,
                    p.ActiveRunId,
                    p.PointsTick)),
            Runs = runs.ToImmutableSortedDictionary(
                r => r.Id,
                r => new RunState(
                    r.Id, r.PlayerId, r.GameId, r.Status,
                    JsonSerializer.Deserialize<RunSnapshot>(r.SnapshotJson, EngineJson.Options)!,
                    r.RolledAt, r.StartedAt, r.Difficulty, r.Hours,
                    JsonSerializer.Deserialize<EquatableArray<Die>>(r.DiceJson, EngineJson.Options),
                    JsonSerializer.Deserialize<EquatableArray<Die>>(r.ChallengeDiceJson, EngineJson.Options),
                    r.HoursSource,
                    reviews.TryGetValue(r.Id, out var review) ? new RunReview(review.Rating, review.Text) : null,
                    r.CompletedAt,
                    r.ReachedFinish,
                    proofs.TryGetValue(r.Id, out var proof)
                        ? new Engine.Proofs.ProofState(
                            proof.Status,
                            JsonSerializer.Deserialize<EquatableArray<string>>(proof.LinksJson, EngineJson.Options),
                            proof.Note,
                            proof.WitnessId,
                            proof.SubmittedAt,
                            proof.Comment,
                            JsonSerializer.Deserialize<EquatableArray<Guid>>(proof.FilesJson, EngineJson.Options))
                        : null,
                    r.Moved,
                    r.AfterFinish,
                    r.FreeMode)),
            ManualEffects = effects.ToImmutableSortedDictionary(
                x => x.Id,
                x => new PendingManualEffect(x.Id, x.PlayerId, x.DrawEvent, x.Source, x.RunId)),
            Result = result.Count == 0
                ? null
                : [.. result.Select(x => new Engine.Ranking.LeaderboardRow(x.PlayerId, x.Place, x.Points, x.CellsToFinish, x.IsFirst, x.Provisional))],
        };
    }
}
