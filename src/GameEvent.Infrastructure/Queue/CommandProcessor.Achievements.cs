using GameEvent.Engine.Achievements;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Queue;

public sealed partial class CommandProcessor
{
    private static async Task<IReadOnlySet<AccountAchievementKey>?> ReadAccountAchievementsAsync(
        GameEventDbContext db, SeasonState state, ICommand command, CancellationToken ct)
    {
        if (command is not CompleteRun completion || state.Ruleset?.Features.Achievements != true
            || !state.Players.TryGetValue(completion.PlayerId, out var player)
            || !state.Catalog.Objects.Values.Any(entry => entry is
            { Deleted: false, Definition: { Kind: ObjectKind.Achievement, Scope: AchievementScope.AllTime } }))
        {
            return null;
        }

        var objects = await db.AccountAchievements.AsNoTracking().Where(row => row.UserId == player.UserId)
            .Select(row => row.ObjectId).ToListAsync(ct);
        return objects.Select(objectId => new AccountAchievementKey(player.UserId, objectId)).ToHashSet();
    }
}
