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

    /// <summary>
    /// The content of the repository the site publishes into a season (D-533): /content/items and /content/events as objects,
    /// /content/wheels and /content/decks as wheels.
    /// </summary>
    public static ContentPack RepositoryContent() => SimulationInputs.ReadContent(PathOf("content"));

    /// <summary>The default ruleset with events on (and items, which events are, D-530): «ивенты вкл» of the simulator (task 5.15).</summary>
    public static Ruleset WithEvents()
    {
        var rules = RulesetJson.Default();
        return rules with { Features = rules.Features with { Items = true, Events = true } };
    }

    /// <summary>The inputs with <paramref name="content"/> for the season.</summary>
    public static SimulationInputs InputsWithContent(Ruleset ruleset, ContentPack? content, SimulationSettings? settings = null) =>
        SimulationInputs.Create(
            ruleset, null, DemoPool(), settings ?? new SimulationSettings(), "docs/ruleset.default.json", null, "content/pool.demo.json", content);

    public static Ruleset WithUncheckedLimit(int? limit)
    {
        var rules = RulesetJson.Default();
        return rules with { Season = rules.Season with { MaxUncheckedRuns = limit } };
    }
}
