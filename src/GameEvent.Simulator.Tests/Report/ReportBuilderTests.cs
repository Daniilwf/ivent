using GameEvent.Engine.Rulesets;
using GameEvent.Simulator.Play;
using GameEvent.Simulator.Report;
using GameEvent.Simulator.Setup;
using GameEvent.Simulator.Tests.Support;

namespace GameEvent.Simulator.Tests.Report;

/// <summary>
/// SM5: the report's aggregates on two tiny seasons, every number checked by hand. Season 0: the active dropper finishes
/// first; season 1: the busy keeper wins on points, the active one has a run unfinished at the deadline and a tech
/// reroll, the busy one a run in the first's free mode (no points: left out of the points per hour).
/// </summary>
public class ReportBuilderTests
{
    private static readonly SimulationSettings s_settings = new()
    {
        Players = [new("active", 1), new("busy", 1)],
        LengthBuckets = [5],
    };

    private static readonly SimulationReport s_report = ReportBuilder.Build(Inputs(), days: 21, seed: 1, Seasons());

    [Fact]
    public void Length_buckets_count_runs_and_points_per_hour()
    {
        var (shortGames, longGames) = (s_report.LengthBuckets[0], s_report.LengthBuckets[1]);

        Assert.Equal(("0–5 ч", "5+ ч"), (shortGames.Label, longGames.Label));

        // Short: 6 runs of 3 h; 4 scored completions give 25 points over 16 h of play and 12 h of length; with the 2 h of
        // the unfinished run 25 points over 18 h (the free-mode run counts nowhere)
        Assert.Equal(3, shortGames.StartedPerSeason);
        Assert.Equal(2, shortGames.CompletedPerSeason);
        Assert.Equal(0, shortGames.DropShare);
        Assert.Equal(1.563, shortGames.PointsPerPlayHour);
        Assert.Equal(2.083, shortGames.PointsPerGameHour);
        Assert.Equal(1.389, shortGames.PointsPerAllPlayHours);
        Assert.Equal(6.25, shortGames.MeanPointsPerCompletion);
        Assert.Equal(4, shortGames.MeanPlayHoursPerCompletion);

        // Long: 4 runs of 12 h, one dropped (penalty 5), one tech-rerolled; 22 points over 32 h of play and 24 h of length;
        // 22 − 5 points over all 35 h
        Assert.Equal(2, longGames.StartedPerSeason);
        Assert.Equal(1, longGames.CompletedPerSeason);
        Assert.Equal(0.5, longGames.DroppedPerSeason);
        Assert.Equal(0.25, longGames.DropShare);
        Assert.Equal(0.688, longGames.PointsPerPlayHour);
        Assert.Equal(0.917, longGames.PointsPerGameHour);
        Assert.Equal(0.486, longGames.PointsPerAllPlayHours);
        Assert.Equal(11, longGames.MeanPointsPerCompletion);
    }

    [Fact]
    public void Drops_compare_droppers_with_keepers()
    {
        var all = s_report.Drops.Rows[0];

        Assert.Equal("все", all.Group);
        Assert.Equal((2, 2), (all.DropperBots, all.KeeperBots));
        Assert.Equal((15, 9.5, 5.5), (all.DropperMeanPoints, all.KeeperMeanPoints, all.PointsDifference));
        Assert.Equal((1.5, 1.5), (all.DropperMeanPlace, all.KeeperMeanPlace));
        Assert.Equal((0.5, 0), (all.DropperMeanDrops, all.KeeperMeanDrops));
        Assert.Equal((5, 2, 0.5), (s_report.Drops.MeanPenaltyPerDrop, s_report.Drops.MeanHoursPerDrop, s_report.Drops.DropsPerSeason));
        Assert.Equal(["все", "active", "busy"], s_report.Drops.Rows.Select(r => r.Group));
    }

    [Fact]
    public void Finish_takes_the_seasons_with_a_first()
    {
        var finish = s_report.Finish;

        Assert.Equal(0.5, finish.SeasonsWithFirst);
        Assert.Equal(10, finish.FirstFinishDay!.P50);
        Assert.Equal(11, finish.FirstFrozenDay!.P90);
        Assert.Equal(0.5, finish.FinishersPerSeason);
        Assert.Equal(new Dictionary<string, double> { ["active"] = 0.5 }, finish.FirstByProfile);
    }

    [Fact]
    public void Profiles_and_the_gap_between_them()
    {
        var (active, busy) = (s_report.Profiles[0], s_report.Profiles[1]);

        Assert.Equal((15, 1.5, 0.5, 1.0), (active.MeanPoints, active.MeanPlace, active.WinShare, active.Top3Share));
        Assert.Equal((0.5, 0.5), (active.FirstShare, active.FinishShare));

        // 28 points of completions over their 36 h; 30 points of the seasons over 50 h played; 2 h unfinished in 2 seasons
        Assert.Equal((0.778, 0.6, 1), (active.PointsPerPlayHour, active.SeasonPointsPerPlayHour, active.MeanUnfinishedHours));
        Assert.Equal((9.5, 0.5, 1.583, 1.583, 0), (busy.MeanPoints, busy.WinShare, busy.PointsPerPlayHour, busy.SeasonPointsPerPlayHour, busy.MeanUnfinishedHours));
        Assert.Equal((25, 6), (active.MeanPlayHours, busy.MeanPlayHours));

        var gap = s_report.Gap!;
        Assert.Equal(("active", "busy"), (gap.Active, gap.Busy));
        Assert.Equal((15, 9.5, 1.579), (gap.ActiveMeanPoints, gap.BusyMeanPoints, gap.PointsRatio));
        Assert.Equal(0.5, gap.BusyBeatsActiveShare);
    }

    [Fact]
    public void No_gap_with_a_single_profile()
    {
        var single = new SimulationSettings { Players = [new("active", 2)] };
        var inputs = SimulationInputs.Create(RulesetJson.Default(), null, Pool(), single, "ruleset", null, "pool");
        var season = new SeasonOutcome(
            0, 1, [Bot(0, "active", true, 3, 1, false, null, 1, 0, 2), Bot(1, "active", false, 2, 2, false, null, 1, 0, 2)], [], [], null, null,
            new Dictionary<string, int>(), 0, 0, 0, 0);

        Assert.Null(ReportBuilder.Build(inputs, 21, 1, [season]).Gap);
    }

    [Fact]
    public void Zones_branches_policies_and_counts()
    {
        Assert.Equal(["z", "—"], s_report.Zones.Select(z => z.Zone));
        var zone = s_report.Zones[0];
        Assert.Equal((0.5, 12, 1, 0.5), (zone.StartedPerSeason, zone.MeanGameHours, zone.CompletedShare, zone.PointsPerPlayHour));
        var outside = s_report.Zones[1];
        Assert.Equal(
            (4.5, 6, 0.667, 0.111, 1.321, 5),
            (outside.StartedPerSeason, outside.MeanGameHours, outside.CompletedShare, outside.DropShare, outside.PointsPerPlayHour, outside.MeanPenaltyPerDrop));

        // First choices: the active bot b1 (then c1 again, not counted), the busy bot c1
        var options = s_report.Branches!.Options;
        Assert.Equal(
            [("f", "b1", 1, 0.333, 1, 20.0, 1.0, (double?)10), ("f", "c1", 2, 0.667, 1, 5.0, 0.0, null)],
            options.Select(o => (o.Fork, o.Option, o.Chosen, o.Share, o.FirstChoiceBots, o.MeanPoints, o.FinishShare, o.MeanFinishDay)));
        Assert.Equal(
            [("shortest", 2, 15.0, 1.5, 0.5, 0.5, (double?)10), ("default", 0, 0, 0, 0, 0, null), ("random", 2, 9.5, 1.5, 0, 0, null)],
            s_report.Branches.Policies.Select(p => (p.Policy, p.Bots, p.MeanPoints, p.MeanPlace, p.FinishShare, p.FirstShare, p.MeanFinishDay)));

        var counts = s_report.Counts;
        Assert.Equal((3, 2.5, 0.5), (counts.CompletedPerSeason, counts.CommandsPerSeason, counts.RejectionsPerSeason["roll.tooManyUnchecked"]));
        Assert.Equal((0.5, 1, 0.5), (counts.UnfinishedAtDeadlinePerSeason, counts.UnfinishedHoursPerSeason, counts.BlockedHoursPerSeason));
    }

    private static PoolFile Pool() =>
        new([new PoolFileCategory("Puzzle", 1)], [new PoolFileGame("Portal", ["Puzzle"], 3), new PoolFileGame("Braid", ["Puzzle"], 12)]);

    private static SimulationInputs Inputs() =>
        SimulationInputs.Create(RulesetJson.Default(), SimulatorFixtures.Map("content/map.example.json"), Pool(), s_settings, "ruleset", "map", "pool");

    private static BotOutcome Bot(
        int index, string profile, bool dropper, int points, int place, bool first, int? order, int completed, int drops, double play, double blocked = 0) =>
        new(index, profile, dropper, dropper ? BranchPolicy.Shortest : BranchPolicy.Random, points, place, first, order, order is null ? null : 10,
            completed, drops, TechRerolls: 0, AlreadyPlayed: 0, FreeRerolls: 0, PaidRerolls: 0, play, FreeHours: play + 5, blocked, Rejected: 0,
            CellBonus: 0, FinishBonus: 0, OtherPoints: 0, Teleports: 0);

    private static SeasonOutcome[] Seasons() =>
    [
        new SeasonOutcome(
            0,
            1,
            [Bot(0, "active", dropper: true, 20, 1, first: true, order: 1, 2, 1, 18, blocked: 1), Bot(1, "busy", dropper: false, 5, 2, first: false, null, 1, 0, 4)],
            [
                new RunOutcome(0, 3, 4, RunEnd.Completed, 6, 0, null, false),
                new RunOutcome(0, 12, 12, RunEnd.Completed, 12, 0, null, false),
                new RunOutcome(0, 12, 2, RunEnd.Dropped, 0, 5, null, false),
                new RunOutcome(1, 3, 4, RunEnd.Completed, 5, 0, null, false),
            ],
            [new BranchOutcome(0, "f", "b1"), new BranchOutcome(0, "f", "c1"), new BranchOutcome(1, "f", "c1")],
            10,
            11,
            new Dictionary<string, int> { ["roll.tooManyUnchecked"] = 1 },
            Commands: 3,
            Events: 10,
            Misses: 0,
            GuardTrips: 0),
        new SeasonOutcome(
            1,
            2,
            [Bot(0, "active", dropper: true, 10, 2, first: false, null, 1, 0, 32), Bot(1, "busy", dropper: false, 14, 1, first: false, null, 2, 0, 8)],
            [
                new RunOutcome(0, 12, 20, RunEnd.Completed, 10, 0, "z", false),
                new RunOutcome(0, 12, 1, RunEnd.TechRerolled, 0, 0, null, false),
                new RunOutcome(0, 3, 2, RunEnd.Unfinished, 0, 0, null, false),
                new RunOutcome(1, 3, 3, RunEnd.Completed, 7, 0, null, false),
                new RunOutcome(1, 3, 5, RunEnd.Completed, 7, 0, null, false),
                new RunOutcome(1, 3, 3, RunEnd.Completed, 0, 0, null, FreeMode: true),
            ],
            [],
            null,
            null,
            new Dictionary<string, int>(),
            Commands: 2,
            Events: 8,
            Misses: 0,
            GuardTrips: 0),
    ];
}
