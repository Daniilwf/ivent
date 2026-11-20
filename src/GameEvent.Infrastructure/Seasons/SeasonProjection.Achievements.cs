using GameEvent.Engine.Achievements;
using GameEvent.Engine.Kernel;
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
}
