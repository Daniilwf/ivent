using GameEvent.Engine.Content;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Simulator.Setup;

namespace GameEvent.Simulator.Tests.Support;

/// <summary>Inputs of the simulator from the repository's files, with changes for a test.</summary>
public static class SimulatorFixtures
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameEvent.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("GameEvent.slnx not found above the test output.");
    }

    public static string PathOf(string relative) => Path.Combine(Root(), relative);

    public static PoolFile DemoPool() => SimulationInputs.ReadPool(File.ReadAllText(PathOf("content/pool.demo.json")));

    public static MapGraph Map(string file) => ContentJson.Parse<MapGraph>(File.ReadAllText(PathOf(file)));

    /// <summary>The default ruleset, the demo pool and the default bots, unless a test gives its own.</summary>
    public static SimulationInputs Inputs(
        Ruleset? ruleset = null, MapGraph? map = null, PoolFile? pool = null, SimulationSettings? settings = null) =>
        SimulationInputs.Create(
            ruleset ?? RulesetJson.Default(),
            map,
            pool ?? DemoPool(),
            settings ?? new SimulationSettings(),
            "docs/ruleset.default.json",
            map is null ? null : "map",
            "content/pool.demo.json");

    public static Ruleset WithUncheckedLimit(int? limit)
    {
        var rules = RulesetJson.Default();
        return rules with { Season = rules.Season with { MaxUncheckedRuns = limit } };
    }
}
