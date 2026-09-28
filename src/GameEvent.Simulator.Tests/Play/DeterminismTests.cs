using GameEvent.Simulator.Cli;
using GameEvent.Simulator.Report;
using GameEvent.Simulator.Tests.Support;

namespace GameEvent.Simulator.Tests.Play;

/// <summary>SM1: a seed gives the same seasons and the same report, however many threads play them.</summary>
public class DeterminismTests
{
    [Fact]
    public void Same_seed_gives_the_same_report()
    {
        var inputs = SimulatorFixtures.Inputs();

        var first = SimulationRunner.ToJson(ReportBuilder.Build(inputs, 21, 7, SimulationRunner.Play(inputs, 6, 21, 7)));
        var second = SimulationRunner.ToJson(ReportBuilder.Build(inputs, 21, 7, SimulationRunner.Play(inputs, 6, 21, 7)));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Threads_do_not_change_the_seasons()
    {
        var inputs = SimulatorFixtures.Inputs();

        var one = SimulationRunner.Play(inputs, 5, 14, 3, threads: 1);
        var many = SimulationRunner.Play(inputs, 5, 14, 3, threads: 4);

        Assert.Equal(
            SimulationRunner.ToJson(ReportBuilder.Build(inputs, 14, 3, one)),
            SimulationRunner.ToJson(ReportBuilder.Build(inputs, 14, 3, many)));
        Assert.Equal(one.Select(s => s.Commands), many.Select(s => s.Commands));
    }

    [Fact]
    public void Another_seed_gives_other_seasons()
    {
        var inputs = SimulatorFixtures.Inputs();

        var first = SimulationRunner.Play(inputs, 3, 21, 1);
        var second = SimulationRunner.Play(inputs, 3, 21, 2);

        Assert.NotEqual(
            first.SelectMany(s => s.Bots.Select(b => b.Points)),
            second.SelectMany(s => s.Bots.Select(b => b.Points)));
    }

    [Fact]
    public void A_graph_map_season_is_deterministic_too()
    {
        var inputs = SimulatorFixtures.Inputs(map: SimulatorFixtures.Map("content/map.demo.json"));

        var first = SimulationRunner.Play(inputs, 3, 21, 11);
        var second = SimulationRunner.Play(inputs, 3, 21, 11);

        Assert.Equal(first.SelectMany(s => s.Branches), second.SelectMany(s => s.Branches));
        Assert.Equal(
            SimulationRunner.ToJson(ReportBuilder.Build(inputs, 21, 11, first)),
            SimulationRunner.ToJson(ReportBuilder.Build(inputs, 21, 11, second)));
    }
}
