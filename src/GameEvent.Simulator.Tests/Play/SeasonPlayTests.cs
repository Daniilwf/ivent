using GameEvent.Simulator.Cli;
using GameEvent.Simulator.Play;
using GameEvent.Simulator.Setup;
using GameEvent.Simulator.Tests.Support;

namespace GameEvent.Simulator.Tests.Play;

/// <summary>
/// SM3, SM4: seasons played through the engine end with a result, and what the simulator saw of the log adds up to the
/// engine's own numbers — on the linear map and on graph maps with forks, teleports, checkpoints and zones.
/// </summary>
public class SeasonPlayTests
{
    [Fact]
    public void A_linear_season_ends_with_a_full_result_that_matches_what_the_bots_saw()
    {
        var inputs = SimulatorFixtures.Inputs();

        var seasons = SimulationRunner.Play(inputs, 4, 21, 21);

        Assert.All(seasons, AssertConsistent);
        Assert.All(seasons, s => Assert.Empty(s.Branches));
        Assert.Contains(seasons.SelectMany(s => s.Runs), r => r.End == RunEnd.Dropped);
        Assert.Contains(seasons.SelectMany(s => s.Runs), r => r.End == RunEnd.Unfinished);
    }

    [Fact]
    public void Active_bots_play_more_than_busy_ones()
    {
        var seasons = SimulationRunner.Play(SimulatorFixtures.Inputs(), 4, 21, 4);
        var bots = seasons.SelectMany(s => s.Bots).ToList();

        Assert.True(
            bots.Where(b => b.Profile == "active").Average(b => b.PlayHours) > 2 * bots.Where(b => b.Profile == "busy").Average(b => b.PlayHours));
        Assert.All(bots, b => Assert.True(b.PlayHours <= b.FreeHours + 1e-6, $"bot {b.Index} played more than its free time"));
    }

    [Fact]
    public void Graph_map_season_chooses_branches_stops_on_cells_and_rolls_in_zones()
    {
        var inputs = SimulatorFixtures.Inputs(map: SimulatorFixtures.Map("content/map.demo.json"));

        var seasons = SimulationRunner.Play(inputs, 6, 21, 31);

        Assert.All(seasons, AssertConsistent);
        var branches = seasons.SelectMany(s => s.Branches).ToList();
        Assert.Equal(["f1", "f2"], branches.Select(b => b.Fork).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(4, branches.Select(b => (b.Fork, b.Option)).Distinct().Count());
        var zones = seasons.SelectMany(s => s.Runs).Select(r => r.Zone).OfType<string>().Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(["horror-swamp", "quick"], zones);
        Assert.Contains(seasons.SelectMany(s => s.Bots), b => b.Teleports > 0);
        Assert.Contains(seasons.SelectMany(s => s.Bots), b => b.CellBonus > 0);
    }

    [Fact]
    public void The_content_example_map_plays_to_a_result()
    {
        var inputs = SimulatorFixtures.Inputs(map: SimulatorFixtures.Map("content/map.example.json"));

        var seasons = SimulationRunner.Play(inputs, 3, 7, 41);

        Assert.All(seasons, AssertConsistent);
        Assert.All(seasons, s => Assert.Contains(s.Bots, b => b.IsFirst));
        Assert.Contains(seasons.SelectMany(s => s.Branches), b => b.Fork == "f");
    }

    [Fact]
    public void Every_branch_policy_is_played_by_every_profile_over_the_runs()
    {
        var seasons = SimulationRunner.Play(SimulatorFixtures.Inputs(map: SimulatorFixtures.Map("content/map.example.json")), 3, 3, 1);

        var pairs = seasons.SelectMany(s => s.Bots).Select(b => (b.Profile, b.BranchPolicy)).Distinct().Count();
        Assert.Equal(3 * 3, pairs);
    }

    // Points are the engine's; the sum of what the simulator saw per run and per bot must give the same
    private static void AssertConsistent(SeasonOutcome season)
    {
        Assert.Equal(new SimulationSettings().Players.Sum(p => p.Count), season.Bots.Count);
        Assert.Equal(0, season.GuardTrips);
        Assert.Equal(1, season.Bots.Min(b => b.Place));
        Assert.True(season.Bots.Count(b => b.IsFirst) <= 1);
        foreach (var bot in season.Bots)
        {
            var runs = season.Runs.Where(r => r.Bot == bot.Index).ToList();
            var fromRuns = runs.Where(r => r.End != RunEnd.Rejected).Sum(r => r.Points) - runs.Sum(r => r.Penalty);
            Assert.Equal(bot.Points, fromRuns + bot.CellBonus + bot.FinishBonus);
            Assert.Equal(bot.Completed, runs.Count(r => r.End == RunEnd.Completed));
            Assert.Equal(bot.Drops, runs.Count(r => r.End == RunEnd.Dropped));
            Assert.Equal(bot.FinishOrder is not null, bot.FinishedDay is not null);
        }
    }
}
