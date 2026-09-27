using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameEvent.Engine.Rulesets;
using GameEvent.Simulator.Cli;
using GameEvent.Simulator.Setup;
using GameEvent.Simulator.Tests.Support;

namespace GameEvent.Simulator.Tests.Setup;

/// <summary>SM6: the inputs — pool hours, the map, the bots' settings and the command line.</summary>
public class InputsTests
{
    [Fact]
    public void Games_keep_their_hours_and_the_others_get_the_same_generated_hours_every_time()
    {
        var pool = new PoolFile(
            [new PoolFileCategory("Puzzle", 1)],
            [new PoolFileGame("Portal", ["Puzzle"], 3), new PoolFileGame("Braid", ["Puzzle"]), new PoolFileGame("Fez", ["Puzzle"], 0)]);
        var missing = new MissingHours();

        var (first, generated) = SimulationInputs.BuildPool(pool, missing);
        var (second, _) = SimulationInputs.BuildPool(pool, missing);

        Assert.Equal(2, generated);
        Assert.Equal(3m, first.Games[0].Hours);
        Assert.All(first.Games, g => Assert.InRange(g.Hours!.Value, (decimal)missing.Min, (decimal)missing.Max));
        Assert.All(first.Games, g => Assert.Equal(0, (g.Hours!.Value * 2) % 1));
        Assert.Equal(first.Games, second.Games);
        Assert.Equal(3, first.Games.Select(g => g.Id).Distinct().Count());
    }

    [Fact]
    public void Generated_hours_follow_the_configured_median()
    {
        var (pool, generated) = SimulationInputs.BuildPool(SimulatorFixtures.DemoPool(), new MissingHours());

        var hours = pool.Games.Select(g => (double)g.Hours!.Value).Order().ToList();
        Assert.Equal(pool.Games.Count, generated);
        Assert.InRange(hours[hours.Count / 2], 9, 12.5);
    }

    [Fact]
    public void A_map_switches_the_ruleset_to_graph_mode()
    {
        var inputs = SimulatorFixtures.Inputs(map: SimulatorFixtures.Map("content/map.example.json"));

        Assert.Equal(MapMode.Graph, inputs.Ruleset.Features.MapMode);
        Assert.Equal(MapMode.Linear, SimulatorFixtures.Inputs().Ruleset.Features.MapMode);
    }

    [Fact]
    public void Inputs_that_cannot_be_played_name_every_problem()
    {
        var rules = RulesetJson.Default() with { Features = RulesetJson.Default().Features with { MapMode = MapMode.Graph } };
        var settings = new SimulationSettings
        {
            Players = [new("nobody", 2)],
            Behaviour = new BotBehaviour { TechRerollChance = 2, Difficulty = new Dictionary<string, double> { ["medium"] = 1 } },
        };

        var error = Assert.Throws<InvalidDataException>(() => SimulatorFixtures.Inputs(rules, settings: settings));

        Assert.Contains("--map", error.Message, StringComparison.Ordinal);
        Assert.Contains("players.nobody", error.Message, StringComparison.Ordinal);
        Assert.Contains("behaviour.techRerollChance", error.Message, StringComparison.Ordinal);
        Assert.Contains("behaviour.difficulty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_read_only_the_fields_they_name_and_refuse_a_typo()
    {
        var settings = SimulationSettings.Parse("""{ "players": [{ "profile": "busy", "count": 3 }], "admin": { "minDelayHours": 6 } }""");

        Assert.Equal([new PlayerGroup("busy", 3)], settings.Players);
        Assert.Equal(6, settings.Admin.MinDelayHours);
        Assert.Equal(new AdminBehaviour().CheckHours, settings.Admin.CheckHours);
        Assert.Equal(new BotBehaviour().RerollAboveHours, settings.Behaviour.RerollAboveHours);
        Assert.Throws<JsonException>(() => SimulationSettings.Parse("""{ "playrs": [] }"""));
    }

    [Fact]
    public void The_example_map_file_is_the_map_of_CONTENT_md()
    {
        // content/map.example.json runs the CONTENT.md example; it must not drift from the document
        var markdown = File.ReadAllText(SimulatorFixtures.PathOf("docs/CONTENT.md"));
        var block = Regex.Matches(markdown, "```json\\n(.*?)```", RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups[1].Value)
            .Single(b => b.Contains("\"edges\"", StringComparison.Ordinal));

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(block),
            JsonNode.Parse(File.ReadAllText(SimulatorFixtures.PathOf("content/map.example.json")))));
    }

    [Fact]
    public void Command_line_defaults_and_options()
    {
        var (defaults, _) = CliArguments.Parse([]);
        Assert.Equal(new CliArguments(1000, 21, null, null, "content/pool.demo.json", null, 1, "var/simulation", 0), defaults);

        var (given, error) = CliArguments.Parse(
            ["--runs", "2000", "--days", "28", "--ruleset", "r.json", "--map", "m.json", "--pool", "p.json", "--seed", "9", "--out", "o", "--threads", "2", "--settings", "s.json"]);
        Assert.Null(error);
        Assert.Equal(new CliArguments(2000, 28, "r.json", "m.json", "p.json", "s.json", 9, "o", 2), given);
    }

    [Theory]
    [InlineData("--runs", "0")]
    [InlineData("--runs", "many")]
    [InlineData("--days", "-3")]
    [InlineData("--seed", "-1")]
    [InlineData("--colour", "red")]
    public void Bad_options_are_refused_by_name(string name, string value)
    {
        var (arguments, error) = CliArguments.Parse([name, value]);

        Assert.Null(arguments);
        Assert.Contains(name, error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_without_a_value_is_refused()
    {
        var (arguments, error) = CliArguments.Parse(["--runs"]);

        Assert.Null(arguments);
        Assert.Equal("--runs needs a value", error);
    }
}
