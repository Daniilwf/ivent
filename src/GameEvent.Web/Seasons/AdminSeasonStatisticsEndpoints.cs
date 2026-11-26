using GameEvent.Engine.Statistics;
using GameEvent.Infrastructure.Database;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>A season's attack and activity report for the admin.</summary>
public sealed record AdminSeasonStatisticsView(IReadOnlyList<AdminSeasonStatisticsPlayerView> Players);

/// <summary>Effective attacks and the latest command authored by a participant in this season.</summary>
public sealed record AdminSeasonStatisticsPlayerView(
    Guid PlayerId, string Name, int AttacksMade, int AttacksReceived, DateTimeOffset? LastActionAt);

internal static class AdminSeasonStatisticsEndpoints
{
    internal const string FeatureKey = "Site:AdminStatistics";

    internal static async Task<Results<Ok<AdminSeasonStatisticsView>, NotFound>> ReadAsync(
        Guid seasonId, IConfiguration configuration, HttpResponse response, GameEventDbContext db, CancellationToken ct)
    {
        response.Headers.CacheControl = "private, no-store";
        if (!configuration.GetValue<bool>(FeatureKey) || !await db.Seasons.AnyAsync(season => season.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        var players = await db.SeasonPlayers.AsNoTracking()
            .Where(player => player.SeasonId == seasonId)
            .Select(player => new { player.Id, player.UserId, player.Name })
            .ToListAsync(ct);
        if (players.Count == 0)
        {
            return TypedResults.Ok(new AdminSeasonStatisticsView([]));
        }

        var attacks = (await SeasonStatisticsEndpoints.ReadEffectiveAttacksAsync(db, [seasonId], ct))[seasonId];
        var made = attacks.ToLookup(attack => attack.FromPlayerId);
        // An attack from the target is already in their outgoing group.
        var received = attacks.Where(attack => attack.PlayerId != attack.FromPlayerId).ToLookup(attack => attack.PlayerId);
        var lastActions = await AdminPlayerEndpoints.ReadLastOwnActionsAsync(db, seasonId, players.Select(player => player.UserId), ct);

        IReadOnlyList<AdminSeasonStatisticsPlayerView> report = [.. players
            .InTokenOrder(player => player.Name, player => player.Id)
            .Select(player =>
            {
                var stats = AttackStatistics.ForPlayer(player.Id, made[player.Id].Concat(received[player.Id]));
                DateTimeOffset? lastAction = lastActions.TryGetValue(player.UserId, out var at) ? at : null;
                return new AdminSeasonStatisticsPlayerView(player.Id, player.Name, stats.AttacksMade, stats.AttacksReceived, lastAction);
            })];
        return TypedResults.Ok(new AdminSeasonStatisticsView(report));
    }
}
