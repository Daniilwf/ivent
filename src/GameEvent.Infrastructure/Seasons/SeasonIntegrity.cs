using System.Collections;
using System.Reflection;
using GameEvent.Engine.Files;
using GameEvent.Engine.Gallery;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Seasons;

/// <summary>
/// The result of an integrity check: the log's last event, what the stored state has that the log does not, and whether
/// the log stood still while it was read (<see cref="Settled"/> false: commands kept coming — check again).
/// </summary>
public sealed record IntegrityReport(Guid SeasonId, long LastSequence, IReadOnlyList<string> Differences, bool Settled = true)
{
    public bool IsIntact => Differences.Count == 0;
}

/// <summary>
/// The integrity check (SPEC «Расхождение лога и состояния», L4, D-105): the season state folded from the log is compared
/// with the one the projection tables describe. Read only. A command committed between the two reads would look like a
/// difference, so the check reads again until the log has not moved under it.
/// </summary>
public static class SeasonIntegrity
{
    private const int Attempts = 3;

    public static async Task<IntegrityReport?> CheckAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        for (var attempt = 1; ; attempt++)
        {
            var (replayed, last) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
            if (!replayed.IsCreated)
            {
                return null;
            }

            var stored = await SeasonProjection.ReadAsync(db, replayed, ct);
            var differences = Differences(replayed, stored)
                .Concat(await TimerDifferencesAsync(db, replayed, ct))
                .Concat(await RulesetHistoryDifferencesAsync(db, seasonId, ct))
                .Concat(await GalleryMediaDifferencesAsync(db, seasonId, ct))
                .ToList();
            var lastAfter = await db.Events.AsNoTracking().Where(e => e.SeasonId == seasonId).MaxAsync(e => e.Sequence, ct);
            if (lastAfter == last || attempt == Attempts)
            {
                return new IntegrityReport(seasonId, last, differences, Settled: lastAfter == last);
            }
        }
    }

    /// <summary>What differs between the state of the log and the stored one, entity by entity and field by field.</summary>
    public static IReadOnlyList<string> Differences(SeasonState fromLog, SeasonState stored)
    {
        ArgumentNullException.ThrowIfNull(fromLog);
        ArgumentNullException.ThrowIfNull(stored);
        var differences = new List<string>();
        foreach (var field in new[] { "Name", "Status", "Deadline", "Ruleset", "RulesetVersion", "Result" })
        {
            if (!SameValue(Get(fromLog, field), Get(stored, field)))
            {
                differences.Add($"season.{Camel(field)}");
            }
        }

        Compare("player", fromLog.Players, stored.Players, differences);
        Compare("run", fromLog.Runs, stored.Runs, differences);
        Compare("manualEffect", fromLog.ManualEffects, stored.ManualEffects, differences);

        // The stage 5 mechanics (D-810); duels have no table of their own (D-730)
        Compare("poll", fromLog.Polls, stored.Polls, differences);
        Compare("globalEvent", fromLog.GlobalEvents, stored.GlobalEvents, differences);
        Compare("interaction", fromLog.Interactions, stored.Interactions, differences);
        Compare("partnerRequest", fromLog.PartnerRequests, stored.PartnerRequests, differences);
        Compare("weeklyChallenge", fromLog.WeeklyChallenges, stored.WeeklyChallenges, differences);
        Compare("suggestion", fromLog.Suggestions, stored.Suggestions, differences);
        Compare("comment", fromLog.Comments, stored.Comments, differences);
        Compare("galleryMoment", fromLog.GalleryItems, stored.GalleryItems, differences);
        if (!fromLog.ReviewReactions.SetEquals(stored.ReviewReactions))
        {
            differences.Add("reviewReactions");
        }

        if (!fromLog.FeedReactions.SetEquals(stored.FeedReactions))
        {
            differences.Add("feedReactions");
        }

        if (!fromLog.GalleryReactions.SetEquals(stored.GalleryReactions))
        {
            differences.Add("galleryReactions");
        }

        return differences;
    }

    private static async Task<IEnumerable<string>> GalleryMediaDifferencesAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        var bindings = await db.GalleryMediaBindings.AsNoTracking().Where(binding => binding.SeasonId == seasonId).ToListAsync(ct);
        var rows = await db.Events.AsNoTracking().Where(e => e.SeasonId == seasonId && e.Type == GalleryMomentPublished.Type)
            .Select(e => new StoredEvent(e.Type, e.Version, e.Data)).ToListAsync(ct);
        var publications = rows.Select(EventCodec.Decode).OfType<GalleryMomentPublished>().ToLookup(publication => publication.FileId);
        var ids = bindings.Select(binding => binding.FileId).Concat(publications.Select(group => group.Key)).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var files = await db.Files.AsNoTracking().Where(file => ids.Contains(file.Id)).Select(file => new
        {
            file.Id,
            file.IsDeleted,
            file.Kind,
            file.OwnerId,
            HasBinding = db.GalleryMediaBindings.Any(binding => binding.FileId == file.Id),
        }).ToDictionaryAsync(file => file.Id, ct);
        var invalid = bindings.Where(binding => !files.TryGetValue(binding.FileId, out var file)
            || file.IsDeleted || file.Kind != FileKind.GalleryImage
            || !publications[binding.FileId].Any(publication => publication.ItemId == binding.ItemId && publication.UserId == file.OwnerId))
            .Select(binding => binding.FileId);

        // A protected file is registered atomically with its binding. An imported publication can name a file absent
        // here, or one legitimately bound to a different season; neither gives the archive its own file association.
        var detached = files.Values.Where(file => file.Kind == FileKind.GalleryImage && !file.HasBinding).Select(file => file.Id);
        return invalid.Concat(detached).Order().Select(fileId => $"galleryMedia {fileId}");
    }

    // The timer columns the scheduler reads (D-404, D-812): the season's own moments and each player's are those of the
    // log's state, so a stale column cannot leave a poll or a global event open past its term unnoticed (EV36).
    private static async Task<IEnumerable<string>> TimerDifferencesAsync(GameEventDbContext db, SeasonState replayed, CancellationToken ct)
    {
        var differences = new List<string>();
        var season = await db.Seasons.AsNoTracking().Where(s => s.Id == replayed.SeasonId).Select(s => new { s.NextTimerAt }).SingleOrDefaultAsync(ct);
        if (season is not null && season.NextTimerAt != Engine.Economy.Timers.NextOfSeason(replayed))
        {
            differences.Add("season.nextTimerAt");
        }

        var players = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == replayed.SeasonId).Select(p => new { p.Id, p.NextTimerAt }).ToListAsync(ct);
        foreach (var row in players.OrderBy(p => p.Id))
        {
            if (replayed.Players.TryGetValue(row.Id, out var player) && row.NextTimerAt != Engine.Economy.Timers.Next(replayed, player))
            {
                differences.Add($"player {row.Id}: nextTimerAt");
            }
        }

        return differences;
    }

    // Every version of the rules the log went through is a stored row with those rules (D-82, D-104).
    private static async Task<IEnumerable<string>> RulesetHistoryDifferencesAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        var expected = new SortedDictionary<int, Engine.Rulesets.Ruleset>();
        var state = SeasonState.Empty;
        foreach (var e in await EventLogReader.ReadSeasonAsync(db, seasonId, ct))
        {
            var version = state.RulesetVersion;
            state = SeasonEngine.Apply(state, e);
            if (state.RulesetVersion != version && state.Ruleset is { } rules)
            {
                expected[state.RulesetVersion] = rules;
            }
        }

        var rows = await db.Rulesets.AsNoTracking().Where(r => r.SeasonId == seasonId).ToListAsync(ct);
        var stored = rows.ToDictionary(r => r.Version, r => System.Text.Json.JsonSerializer.Deserialize<Engine.Rulesets.Ruleset>(r.Json, Engine.Kernel.EngineJson.Options));
        return expected.Keys.Union(stored.Keys).Order()
            .Where(v => !(expected.TryGetValue(v, out var a) && stored.TryGetValue(v, out var b) && a == b))
            .Select(v => $"ruleset v{v}: {(expected.ContainsKey(v) ? stored.ContainsKey(v) ? "differs" : "in the log, not stored" : "stored, not in the log")}");
    }

    private static void Compare<T>(
        string kind, IReadOnlyDictionary<Guid, T> fromLog, IReadOnlyDictionary<Guid, T> stored, List<string> differences)
        where T : notnull
    {
        foreach (var id in fromLog.Keys.Union(stored.Keys).Order())
        {
            if (!stored.TryGetValue(id, out var s))
            {
                differences.Add($"{kind} {id}: in the log, not stored");
            }
            else if (!fromLog.TryGetValue(id, out var l))
            {
                differences.Add($"{kind} {id}: stored, not in the log");
            }
            else if (!Equals(l, s))
            {
                var fields = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0 && !SameValue(p.GetValue(l), p.GetValue(s)))
                    .Select(p => Camel(p.Name));
                var list = string.Join(", ", fields);
                differences.Add($"{kind} {id}: {(list.Length == 0 ? "differs" : list)}");
            }
        }
    }

    // Records compare by value; collections without value equality compare item by item.
    private static bool SameValue(object? a, object? b) =>
        Equals(a, b) || (a is IEnumerable x && b is IEnumerable y && a is not string && x.Cast<object?>().SequenceEqual(y.Cast<object?>()));

    private static object? Get(SeasonState state, string field) => typeof(SeasonState).GetProperty(field)!.GetValue(state);

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
