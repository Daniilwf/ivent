using GameEvent.Engine.Achievements;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Seasons;

internal static partial class SeasonProjection
{
    private static async Task WriteAchievementsAsync(
        GameEventDbContext db, Guid playerId, EquatableArray<PlayerAchievement> before,
        EquatableArray<PlayerAchievement> after, CancellationToken ct)
    {
        if (before == after)
        {
            return;
        }

        var stored = (await db.PlayerAchievements.Where(row => row.PlayerId == playerId).ToListAsync(ct))
            .ToDictionary(row => row.ObjectId, StringComparer.Ordinal);
        for (var position = 0; position < after.Count; position++)
        {
            var award = after[position];
            if (stored.Remove(award.ObjectId, out var row))
            {
                row.Name = award.Name;
                row.Description = award.Description;
                row.EarnedAt = award.EarnedAt;
                row.Position = position;
                continue;
            }

            db.PlayerAchievements.Add(new PlayerAchievementRecord
            {
                PlayerId = playerId,
                ObjectId = award.ObjectId,
                Name = award.Name,
                Description = award.Description,
                EarnedAt = award.EarnedAt,
                Position = position,
            });
        }

        db.PlayerAchievements.RemoveRange(stored.Values);
    }

    // Imports write only the final replay. A grant that has already been undone is not a conflict.
    public static async Task WriteImportedLifetimeAchievementsAsync(GameEventDbContext db, SeasonState state, CancellationToken ct)
    {
        foreach (var player in state.Players.Values)
        {
            await WriteLifetimeAchievementsAsync(db, state.SeasonId, player, default, ct);
        }
    }

    private static async Task WriteLifetimeAchievementsAsync(
        GameEventDbContext db, Guid seasonId, SeasonPlayer player, EquatableArray<PlayerAchievement> before, CancellationToken ct)
    {
        var after = player.LifetimeAchievements;
        if (before == after)
        {
            return;
        }

        var stored = await db.AccountAchievements.Where(row => row.UserId == player.UserId)
            .ToDictionaryAsync(row => row.ObjectId, StringComparer.Ordinal, ct);
        for (var position = 0; position < after.Count; position++)
        {
            var award = after[position];
            if (stored.Remove(award.ObjectId, out var row))
            {
                if (row.SeasonId != seasonId || row.PlayerId != player.PlayerId)
                {
                    throw new InvalidDataException(
                        $"The account already has an active lifetime achievement «{award.ObjectId}» from season {row.SeasonId}.");
                }

                row.Name = award.Name;
                row.Description = award.Description;
                row.EarnedAt = award.EarnedAt;
                row.Position = position;
                continue;
            }

            db.AccountAchievements.Add(new AccountAchievementRecord
            {
                UserId = player.UserId,
                ObjectId = award.ObjectId,
                SeasonId = seasonId,
                PlayerId = player.PlayerId,
                Name = award.Name,
                Description = award.Description,
                EarnedAt = award.EarnedAt,
                Position = position,
            });
        }

        db.AccountAchievements.RemoveRange(stored.Values.Where(row => row.SeasonId == seasonId && row.PlayerId == player.PlayerId));
    }
}
