using System.Text.Json.Nodes;
using GameEvent.Simulator.Setup;

namespace GameEvent.Simulator.Report;

/// <summary>
/// The balance report of a simulation (D-356): aggregates over every run, with the inputs it played. Means are per
/// season or per bot as named; shares are 0…1. <see cref="Meta"/> holds the only field that differs between two runs of
/// the same seed — the elapsed time.
/// </summary>
public sealed record SimulationReport(
    ReportMeta Meta,
    IReadOnlyList<LengthBucketRow> LengthBuckets,
    DropReport Drops,
    FinishReport Finish,
    IReadOnlyList<ProfileRow> Profiles,
    GapReport? Gap,
    CountsReport Counts,
    BranchReport? Branches,
    IReadOnlyList<ZoneRow> Zones,
    MapSummary Map,
    PoolSummary Pool,
    JsonNode Ruleset,
    SimulationSettings Settings);

public sealed record ReportMeta(
    int Runs, int Days, ulong Seed, int Players, string RulesetSource, string? MapSource, string PoolSource, double? ElapsedSeconds);

/// <summary>
/// Runs by the game's hours: started, completed, dropped per season; points per hour of play and per hour of the game's
/// length over completed runs (the first's free mode left out); <see cref="PointsPerAllPlayHours"/> — the points of the
/// completions less the drop penalties, over every hour played on the bucket's games, dropped, tech-rerolled and
/// unfinished at the deadline included.
/// </summary>
public sealed record LengthBucketRow(
    string Label,
    double From,
    double? To,
    double StartedPerSeason,
    double CompletedPerSeason,
    double DroppedPerSeason,
    double DropShare,
    double PointsPerPlayHour,
    double PointsPerGameHour,
    double PointsPerAllPlayHours,
    double MeanPointsPerCompletion,
    double MeanPlayHoursPerCompletion);

/// <summary>Bots that drop long games against bots that keep them, overall and per profile, with the cost of one drop.</summary>
public sealed record DropReport(IReadOnlyList<DropRow> Rows, double MeanPenaltyPerDrop, double MeanHoursPerDrop, double DropsPerSeason);

public sealed record DropRow(
    string Group,
    int DropperBots,
    int KeeperBots,
    double DropperMeanPoints,
    double KeeperMeanPoints,
    double PointsDifference,
    double DropperMeanPlace,
    double KeeperMeanPlace,
    double DropperMeanDrops,
    double KeeperMeanDrops,
    double DropperMeanCompleted,
    double KeeperMeanCompleted);

/// <summary>
/// The finish: the share of seasons with a first, when he reached the finish and was frozen (days from the start),
/// finishers per season and who the first was.
/// </summary>
public sealed record FinishReport(
    double SeasonsWithFirst,
    Percentiles? FirstFinishDay,
    Percentiles? FirstFrozenDay,
    double FinishersPerSeason,
    IReadOnlyDictionary<string, double> FirstByProfile);

public sealed record Percentiles(double P10, double P25, double P50, double P75, double P90, double Mean);

/// <summary>
/// A profile's bots, per bot: <see cref="WinShare"/> — place 1 (a tie shares it), <see cref="PointsPerPlayHour"/> — over
/// its completed runs, <see cref="SeasonPointsPerPlayHour"/> — the season's points over every hour played.
/// </summary>
public sealed record ProfileRow(
    string Profile,
    int BotsPerSeason,
    double MeanPoints,
    Percentiles Points,
    double MeanPlace,
    double WinShare,
    double Top3Share,
    double FirstShare,
    double FinishShare,
    double MeanCompleted,
    double MeanDrops,
    double MeanFreeRerolls,
    double MeanPaidRerolls,
    double MeanAlreadyPlayed,
    double MeanTechRerolls,
    double MeanPlayHours,
    double MeanFreeHours,
    double MeanBlockedHours,
    double MeanUnfinishedHours,
    double PointsPerPlayHour,
    double SeasonPointsPerPlayHour);

/// <summary>The profile with the most free time against the one with the least.</summary>
public sealed record GapReport(
    string Active,
    string Busy,
    double ActiveMeanPoints,
    double BusyMeanPoints,
    double PointsRatio,
    double ActiveMeanPlace,
    double BusyMeanPlace,
    double BusyBeatsActiveShare);

/// <summary>Per-season means of what happened, and the engine's refusals by code.</summary>
public sealed record CountsReport(
    double CommandsPerSeason,
    double EventsPerSeason,
    double MissesPerSeason,
    double CompletedPerSeason,
    double DropsPerSeason,
    double TechRerollsPerSeason,
    double AlreadyPlayedPerSeason,
    double FreeRerollsPerSeason,
    double PaidRerollsPerSeason,
    double UnfinishedAtDeadlinePerSeason,
    double UnfinishedHoursPerSeason,
    double BlockedHoursPerSeason,
    IReadOnlyDictionary<string, double> RejectionsPerSeason,
    int GuardTrips);

/// <summary>Graph maps: each branch of each fork, and each branch policy.</summary>
public sealed record BranchReport(IReadOnlyList<BranchRow> Options, IReadOnlyList<PolicyRow> Policies);

/// <summary>
/// A branch: how often it was taken, its share at the fork, and how the bots whose first choice at this fork it was
/// ended the season.
/// </summary>
public sealed record BranchRow(
    string Fork,
    string Option,
    int Chosen,
    double Share,
    int FirstChoiceBots,
    double MeanPoints,
    double MeanPlace,
    double FinishShare,
    double? MeanFinishDay);

public sealed record PolicyRow(
    string Policy, int Bots, double MeanPoints, double MeanPlace, double FinishShare, double FirstShare, double? MeanFinishDay);

/// <summary>Runs by the zone they were rolled in («—» — outside any zone).</summary>
public sealed record ZoneRow(
    string Zone,
    double StartedPerSeason,
    double MeanGameHours,
    double CompletedShare,
    double DropShare,
    double PointsPerPlayHour,
    double MeanPointsPerCompletion,
    double MeanPenaltyPerDrop);

public sealed record MapSummary(
    string Mode, int Cells, int Edges, int Forks, int Teleports, int Checkpoints, int PointsBonuses, IReadOnlyList<string> Zones, int? StartToFinish);

public sealed record PoolSummary(
    int Games, int Categories, int GeneratedHours, Percentiles Hours, IReadOnlyList<BucketShare> ByBucket);

public sealed record BucketShare(string Bucket, double Share);
