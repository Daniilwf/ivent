using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>
/// A split coop run as the partner's own (D-716, D-793): the run, its partner, when he joined it (where it stands among his
/// own runs, as <see cref="DropStreak.Before"/> places it) and its status now.
/// </summary>
internal sealed record CoopPartnerRun(Guid SeasonId, Guid RunId, Guid PartnerId, DateTimeOffset JoinedAt, RunStatus Status);

/// <summary>
/// The split coop runs of the partners, for the screens that read the projection (D-716, D-793): the run table names only
/// the roller, so the partner comes from the log — the run's <c>coop-reward-split</c> not undone, and the latest
/// <c>coop-joined</c> of that partner on it. One place for the season screen, the turn card and the profile, so they count
/// as the engine does.
/// </summary>
internal static class CoopPartnerRuns
{
    /// <summary>The split coop runs of <paramref name="seasonIds"/>, of the partners <paramref name="partnerIds"/> (all when null).</summary>
    public static async Task<List<CoopPartnerRun>> LoadAsync(
        GameEventDbContext db, IReadOnlyCollection<Guid> seasonIds, IReadOnlyCollection<Guid>? partnerIds, CancellationToken ct)
    {
        var rows = await db.Events.AsNoTracking()
            .Where(e => seasonIds.Contains(e.SeasonId) && (e.Type == "coop-reward-split" || e.Type == "coop-joined") && e.UndoneByEventId == null)
            .OrderBy(e => e.SeasonId).ThenBy(e => e.Sequence)
            .Select(e => new { e.SeasonId, e.Type, e.Version, e.Data })
            .ToListAsync(ct);
        var events = rows.Select(r => (r.SeasonId, Event: EventCodec.Decode(new StoredEvent(r.Type, r.Version, r.Data)))).ToList();
        var joined = new Dictionary<(Guid RunId, Guid PartnerId), DateTimeOffset>();
        foreach (var join in events.Select(x => x.Event).OfType<CoopJoined>())
        {
            joined[(join.RunId, join.PlayerId)] = join.JoinedAt;
        }

        var splits = events
            .Where(x => x.Event is CoopRewardSplit split && (partnerIds is null || partnerIds.Contains(split.PartnerId)))
            .Select(x => (x.SeasonId, Split: (CoopRewardSplit)x.Event))
            .DistinctBy(x => x.Split.RunId)
            .ToList();
        if (splits.Count == 0)
        {
            return [];
        }

        var runIds = splits.Select(x => x.Split.RunId).ToList();
        var status = await db.Runs.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Status, ct);
        return [.. splits
            .Where(x => status.ContainsKey(x.Split.RunId) && joined.ContainsKey((x.Split.RunId, x.Split.PartnerId)))
            .Select(x => new CoopPartnerRun(
                x.SeasonId, x.Split.RunId, x.Split.PartnerId, joined[(x.Split.RunId, x.Split.PartnerId)], status[x.Split.RunId]))];
    }
}
