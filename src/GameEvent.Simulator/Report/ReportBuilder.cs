using System.Globalization;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Ranking;
using GameEvent.Simulator.Play;
using GameEvent.Simulator.Setup;

namespace GameEvent.Simulator.Report;

/// <summary>Aggregates the outcomes of many seasons into a <see cref="SimulationReport"/>, in the order of the runs.</summary>
public static class ReportBuilder
{
    public static SimulationReport Build(
        SimulationInputs inputs, int days, ulong seed, IReadOnlyList<SeasonOutcome> seasons, double? elapsedSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(seasons);
        if (seasons.Count == 0)
        {
            throw new ArgumentException("At least one season.", nameof(seasons));
        }

        var n = seasons.Count;
        var bots = seasons.SelectMany(s => s.Bots).ToList();
        var runs = seasons.SelectMany(s => s.Runs).ToList();
        var settings = inputs.Settings;

        return new SimulationReport(
            new ReportMeta(
                n, days, seed, settings.Players.Sum(p => p.Count), inputs.RulesetSource, inputs.MapSource, inputs.PoolSource,
                elapsedSeconds is { } e ? Round(e) : null),
            LengthBuckets(settings.LengthBuckets, runs, n),
            Drops(settings, bots, runs, n),
            Finish(seasons, bots),
            Profiles(settings, seasons, bots, runs),
            Gap(settings, seasons),
            Counts(seasons, runs),
            inputs.Map is null ? null : Branches(settings, seasons),
            Zones(runs, n),
            Map(inputs),
            Pool(inputs),
            JsonSerializer.SerializeToNode(inputs.Ruleset, EngineJson.Options)!,
            settings);
    }

    public static string Label(double from, double? to) =>
        to is { } t
            ? $"{from.ToString(CultureInfo.InvariantCulture)}–{t.ToString(CultureInfo.InvariantCulture)} ч"
            : $"{from.ToString(CultureInfo.InvariantCulture)}+ ч";

    private static IEnumerable<(string Label, double From, double? To)> Buckets(IReadOnlyList<double> edges)
    {
        var from = 0.0;
        foreach (var edge in edges)
        {
            yield return (Label(from, edge), from, edge);
            from = edge;
        }

        yield return (Label(from, null), from, null);
    }

    private static bool In(double hours, double from, double? to) => hours >= from && (to is not { } t || hours < t);

    // Completed runs that gave points: the first's free mode gives none
    private static bool Scored(RunOutcome r) => r.End == RunEnd.Completed && !r.FreeMode;

    private static List<LengthBucketRow> LengthBuckets(IReadOnlyList<double> edges, List<RunOutcome> runs, int n) =>
        [.. Buckets(edges).Select(b =>
        {
            var inBucket = runs.Where(r => In(r.Hours, b.From, b.To)).ToList();
            var scored = inBucket.Where(Scored).ToList();
            var dropped = inBucket.Count(r => r.End == RunEnd.Dropped);
            var points = scored.Sum(r => r.Points);
            return new LengthBucketRow(
                b.Label,
                b.From,
                b.To,
                Round((double)inBucket.Count / n),
                Round((double)scored.Count / n),
                Round((double)dropped / n),
                Ratio(dropped, inBucket.Count),
                Ratio(points, scored.Sum(r => r.PlayHours)),
                Ratio(points, scored.Sum(r => r.Hours)),
                Ratio(points, scored.Count),
                Ratio(scored.Sum(r => r.PlayHours), scored.Count));
        })];

    private static DropReport Drops(SimulationSettings settings, List<BotOutcome> bots, List<RunOutcome> runs, int n)
    {
        DropRow Row(string group, IReadOnlyList<BotOutcome> of)
        {
            var droppers = of.Where(b => b.Dropper).ToList();
            var keepers = of.Where(b => !b.Dropper).ToList();
            var dropperPoints = Mean(droppers.Select(b => (double)b.Points));
            var keeperPoints = Mean(keepers.Select(b => (double)b.Points));
            return new DropRow(
                group,
                droppers.Count,
                keepers.Count,
                dropperPoints,
                keeperPoints,
                Round(dropperPoints - keeperPoints),
                Mean(droppers.Select(b => (double)b.Place)),
                Mean(keepers.Select(b => (double)b.Place)),
                Mean(droppers.Select(b => (double)b.Drops)),
                Mean(keepers.Select(b => (double)b.Drops)),
                Mean(droppers.Select(b => (double)b.Completed)),
                Mean(keepers.Select(b => (double)b.Completed)));
        }

        var rows = new List<DropRow> { Row("все", bots) };
        rows.AddRange(ProfileNames(settings).Select(p => Row(p, [.. bots.Where(b => b.Profile == p)])));
        var drops = runs.Where(r => r.End == RunEnd.Dropped).ToList();
        return new DropReport(
            rows,
            Mean(drops.Select(r => (double)r.Penalty)),
            Mean(drops.Select(r => r.PlayHours)),
            Round((double)drops.Count / n));
    }

    private static FinishReport Finish(IReadOnlyList<SeasonOutcome> seasons, List<BotOutcome> bots)
    {
        var withFirst = seasons.Where(s => s.Bots.Any(b => b.IsFirst)).ToList();
        var firsts = withFirst.Select(s => s.Bots.First(b => b.IsFirst)).ToList();
        var byProfile = firsts
            .GroupBy(b => b.Profile)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Ratio(g.Count(), seasons.Count));
        return new FinishReport(
            Ratio(withFirst.Count, seasons.Count),
            Percentile(withFirst.Select(s => s.FirstFinishDay).OfType<double>()),
            Percentile(withFirst.Select(s => s.FirstFrozenDay).OfType<double>()),
            Ratio(bots.Count(b => b.FinishOrder is not null), seasons.Count),
            byProfile);
    }

    private static List<ProfileRow> Profiles(
        SimulationSettings settings, IReadOnlyList<SeasonOutcome> seasons, List<BotOutcome> bots, List<RunOutcome> runs)
    {
        // A place-1 tie shares the win
        var wins = new Dictionary<(int Season, int Bot), double>();
        foreach (var season in seasons)
        {
            var top = season.Bots.Where(b => b.Place == 1).ToList();
            foreach (var bot in top)
            {
                wins[(season.Index, bot.Index)] = 1.0 / top.Count;
            }
        }

        var profileOf = seasons.SelectMany(s => s.Bots.Select(b => (s.Index, b.Index, b.Profile)))
            .ToDictionary(x => (x.Item1, x.Item2), x => x.Profile);
        var runsBySeason = seasons.SelectMany(s => s.Runs.Select(r => (Season: s.Index, Run: r))).ToList();

        return [.. ProfileNames(settings).Select(profile =>
        {
            var of = bots.Where(b => b.Profile == profile).ToList();
            var scored = runsBySeason.Where(x => Scored(x.Run) && profileOf[(x.Season, x.Run.Bot)] == profile).Select(x => x.Run).ToList();
            var unfinished = runsBySeason
                .Where(x => x.Run.End == RunEnd.Unfinished && profileOf[(x.Season, x.Run.Bot)] == profile)
                .Sum(x => x.Run.PlayHours);
            var won = seasons.Sum(s => s.Bots.Where(b => b.Profile == profile).Sum(b => wins.GetValueOrDefault((s.Index, b.Index))));
            return new ProfileRow(
                profile,
                settings.Players.Where(g => g.Profile == profile).Sum(g => g.Count),
                Mean(of.Select(b => (double)b.Points)),
                Percentile(of.Select(b => (double)b.Points)) ?? new Percentiles(0, 0, 0, 0, 0, 0),
                Mean(of.Select(b => (double)b.Place)),
                Ratio(won, seasons.Count),
                Ratio(of.Count(b => b.Place <= 3), of.Count),
                Ratio(of.Count(b => b.IsFirst), of.Count),
                Ratio(of.Count(b => b.FinishOrder is not null), of.Count),
                Mean(of.Select(b => (double)b.Completed)),
                Mean(of.Select(b => (double)b.Drops)),
                Mean(of.Select(b => (double)b.FreeRerolls)),
                Mean(of.Select(b => (double)b.PaidRerolls)),
                Mean(of.Select(b => (double)b.AlreadyPlayed)),
                Mean(of.Select(b => (double)b.TechRerolls)),
                Mean(of.Select(b => b.PlayHours)),
                Mean(of.Select(b => b.FreeHours)),
                Mean(of.Select(b => b.BlockedHours)),
                Ratio(unfinished, of.Count),
                Ratio(scored.Sum(r => r.Points), scored.Sum(r => r.PlayHours)));
        })];
    }

    private static GapReport Gap(SimulationSettings settings, IReadOnlyList<SeasonOutcome> seasons)
    {
        var used = ProfileNames(settings).ToList();
        var active = used.OrderByDescending(p => settings.Profiles[p].HoursPerDay).ThenBy(p => p, StringComparer.Ordinal).First();
        var busy = used.OrderBy(p => settings.Profiles[p].HoursPerDay).ThenBy(p => p, StringComparer.Ordinal).First();
        var a = seasons.SelectMany(s => s.Bots).Where(b => b.Profile == active).ToList();
        var z = seasons.SelectMany(s => s.Bots).Where(b => b.Profile == busy).ToList();
        var activePoints = Mean(a.Select(b => (double)b.Points));
        var busyPoints = Mean(z.Select(b => (double)b.Points));

        // How often the best busy bot of a season places above its worst active bot
        var beats = seasons.Count(s =>
            s.Bots.Where(b => b.Profile == busy).Select(b => b.Place).DefaultIfEmpty(int.MaxValue).Min()
            < s.Bots.Where(b => b.Profile == active).Select(b => b.Place).DefaultIfEmpty(int.MinValue).Max());
        return new GapReport(
            active,
            busy,
            activePoints,
            busyPoints,
            Ratio(activePoints, busyPoints),
            Mean(a.Select(b => (double)b.Place)),
            Mean(z.Select(b => (double)b.Place)),
            Ratio(beats, seasons.Count));
    }

    private static CountsReport Counts(IReadOnlyList<SeasonOutcome> seasons, List<RunOutcome> runs)
    {
        var n = seasons.Count;
        double PerSeason(Func<BotOutcome, double> of) => Round(seasons.Sum(s => s.Bots.Sum(of)) / n);
        var codes = seasons.SelectMany(s => s.Rejections)
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Round((double)g.Sum(x => x.Value) / n), StringComparer.Ordinal);
        var unfinished = runs.Where(r => r.End == RunEnd.Unfinished).ToList();
        return new CountsReport(
            Round(seasons.Average(s => s.Commands)),
            Round(seasons.Average(s => s.Events)),
            Round(seasons.Average(s => s.Misses)),
            PerSeason(b => b.Completed),
            PerSeason(b => b.Drops),
            PerSeason(b => b.TechRerolls),
            PerSeason(b => b.AlreadyPlayed),
            PerSeason(b => b.FreeRerolls),
            PerSeason(b => b.PaidRerolls),
            Round((double)unfinished.Count / n),
            Round(unfinished.Sum(r => r.PlayHours) / n),
            PerSeason(b => b.BlockedHours),
            codes,
            seasons.Sum(s => s.GuardTrips));
    }

    private static BranchReport Branches(SimulationSettings settings, IReadOnlyList<SeasonOutcome> seasons)
    {
        var all = seasons.SelectMany(s => s.Branches).ToList();
        var byFork = all.GroupBy(b => b.Fork, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // The bot's first choice at each fork, with how the bot ended the season
        var firsts = seasons.SelectMany(s => s.Branches
                .GroupBy(b => (b.Bot, b.Fork))
                .Select(g => (Choice: g.First(), Bot: s.Bots[g.Key.Bot])))
            .ToList();
        var options = all
            .GroupBy(b => (b.Fork, b.Option))
            .OrderBy(g => g.Key.Fork, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Option, StringComparer.Ordinal)
            .Select(g =>
            {
                var chose = firsts.Where(x => x.Choice.Fork == g.Key.Fork && x.Choice.Option == g.Key.Option).Select(x => x.Bot).ToList();
                return new BranchRow(
                    g.Key.Fork,
                    g.Key.Option,
                    g.Count(),
                    Ratio(g.Count(), byFork[g.Key.Fork]),
                    chose.Count,
                    Mean(chose.Select(b => (double)b.Points)),
                    Mean(chose.Select(b => (double)b.Place)),
                    Ratio(chose.Count(b => b.FinishOrder is not null), chose.Count),
                    chose.Any(b => b.FinishedDay is not null) ? Mean(chose.Select(b => b.FinishedDay).OfType<double>()) : null);
            })
            .ToList();

        var bots = seasons.SelectMany(s => s.Bots).ToList();
        var policies = settings.Behaviour.BranchPolicies.Distinct()
            .Select(p =>
            {
                var of = bots.Where(b => b.BranchPolicy == p).ToList();
                return new PolicyRow(
                    p.ToString().ToLowerInvariant(),
                    of.Count,
                    Mean(of.Select(b => (double)b.Points)),
                    Mean(of.Select(b => (double)b.Place)),
                    Ratio(of.Count(b => b.FinishOrder is not null), of.Count),
                    Ratio(of.Count(b => b.IsFirst), of.Count),
                    of.Any(b => b.FinishedDay is not null) ? Mean(of.Select(b => b.FinishedDay).OfType<double>()) : null);
            })
            .ToList();
        return new BranchReport(options, policies);
    }

    private static List<ZoneRow> Zones(List<RunOutcome> runs, int n) =>
        [.. runs
            .GroupBy(r => r.Zone ?? "—", StringComparer.Ordinal)
            .OrderBy(g => g.Key == "—" ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var scored = g.Where(Scored).ToList();
                var dropped = g.Where(r => r.End == RunEnd.Dropped).ToList();
                return new ZoneRow(
                    g.Key,
                    Round((double)g.Count() / n),
                    Mean(g.Select(r => r.Hours)),
                    Ratio(g.Count(r => r.End == RunEnd.Completed), g.Count()),
                    Ratio(dropped.Count, g.Count()),
                    Ratio(scored.Sum(r => r.Points), scored.Sum(r => r.PlayHours)),
                    Ratio(scored.Sum(r => r.Points), scored.Count),
                    Mean(dropped.Select(r => (double)r.Penalty)));
            })];

    private static MapSummary Map(SimulationInputs inputs)
    {
        var map = inputs.Map ?? LinearMap.Generate(inputs.Ruleset.Map.LinearLength);
        var distances = Leaderboard.CellsToFinish(map);
        return new MapSummary(
            inputs.Map is null ? "linear" : "graph",
            map.Cells.Count,
            map.Edges.Count,
            map.Cells.Count(c => c.Type == CellType.Fork),
            map.Cells.Count(c => c.Type == CellType.Teleport),
            map.Cells.Count(c => c.Type == CellType.Checkpoint),
            map.Cells.Count(c => c.Type == CellType.PointsBonus),
            [.. map.Zones.Select(z => z.Id)],
            distances.TryGetValue(map.Start.Id, out var d) ? d : null);
    }

    private static PoolSummary Pool(SimulationInputs inputs)
    {
        var hours = inputs.Pool.Games.Select(g => (double)(g.Hours ?? 0)).ToList();
        return new PoolSummary(
            inputs.Pool.Games.Count,
            inputs.Pool.Categories.Count,
            inputs.GamesWithoutHours,
            Percentile(hours)!,
            [.. Buckets(inputs.Settings.LengthBuckets).Select(b => new BucketShare(b.Label, Ratio(hours.Count(h => In(h, b.From, b.To)), hours.Count)))]);
    }

    private static IEnumerable<string> ProfileNames(SimulationSettings settings) =>
        settings.Players.Where(g => g.Count > 0).Select(g => g.Profile).Distinct(StringComparer.Ordinal);

    public static Percentiles? Percentile(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        double At(double q)
        {
            var position = q * (sorted.Count - 1);
            var low = (int)Math.Floor(position);
            var high = Math.Min(low + 1, sorted.Count - 1);
            return Round(sorted[low] + ((sorted[high] - sorted[low]) * (position - low)));
        }

        return new Percentiles(At(0.1), At(0.25), At(0.5), At(0.75), At(0.9), Round(sorted.Average()));
    }

    private static double Mean(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0 : Round(list.Average());
    }

    private static double Ratio(double part, double whole) => whole == 0 ? 0 : Round(part / whole);

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
