using System.Text.Json;
using GameEvent.Engine.Content;
using GameEvent.Engine.Map;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Rulesets;
using GameEvent.Simulator.Play;

namespace GameEvent.Simulator.Setup;

/// <summary>A game of a pool file: the format of content/pool.*.json (title, tags, hours; other fields are ignored).</summary>
public sealed record PoolFileGame(string Title, IReadOnlyList<string> Tags, decimal? Hours = null);

public sealed record PoolFileCategory(string Name, int Weight);

public sealed record PoolFile(IReadOnlyList<PoolFileCategory> Categories, IReadOnlyList<PoolFileGame> Games);

/// <summary>
/// Everything one simulation plays with: the ruleset, the map (null — the linear map of the ruleset), the pool with
/// every game's hours, and the bots' settings. <see cref="GamesWithoutHours"/> — how many games got generated hours.
/// </summary>
public sealed record SimulationInputs(
    Ruleset Ruleset,
    MapGraph? Map,
    PoolSnapshot Pool,
    SimulationSettings Settings,
    int GamesWithoutHours,
    string RulesetSource,
    string? MapSource,
    string PoolSource)
{
    /// <summary>
    /// Builds the inputs: a map switches the ruleset to <c>features.mapMode = graph</c> (D-354). Throws
    /// <see cref="InvalidDataException"/> with every problem when the inputs cannot be played.
    /// </summary>
    public static SimulationInputs Create(
        Ruleset ruleset, MapGraph? map, PoolFile pool, SimulationSettings settings, string rulesetSource, string? mapSource, string poolSource)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(settings);

        var problems = new List<string>(settings.Problems());
        if (map is not null && ruleset.Features.MapMode != MapMode.Graph)
        {
            ruleset = ruleset with { Features = ruleset.Features with { MapMode = MapMode.Graph } };
        }

        if (map is null && ruleset.Features.MapMode == MapMode.Graph)
        {
            problems.Add("The ruleset plays a graph map: give it with --map.");
        }

        problems.AddRange(RulesetValidator.Validate(ruleset).Select(e => $"ruleset {e.Path}: {e.Message}"));
        if (map is not null)
        {
            problems.AddRange(MapValidator.Validate(map, ruleset).Select(e => $"map {e.Subject}: {e.Code} {e.Message}"));
        }

        if (pool.Games.Count == 0 || pool.Categories.Count == 0)
        {
            problems.Add("The pool needs games and categories.");
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, problems));
        }

        var (snapshot, generated) = BuildPool(pool, settings.MissingHours);
        return new SimulationInputs(ruleset, map, snapshot, settings, generated, rulesetSource, mapSource, poolSource);
    }

    /// <summary>Reads the files given on the command line; null paths take the defaults.</summary>
    public static SimulationInputs Load(string? rulesetPath, string? mapPath, string poolPath, string? settingsPath)
    {
        ArgumentNullException.ThrowIfNull(poolPath);
        var ruleset = rulesetPath is null ? RulesetJson.Default() : RulesetJson.Parse(File.ReadAllText(rulesetPath));
        var map = mapPath is null ? null : ContentJson.Parse<MapGraph>(File.ReadAllText(mapPath));
        var pool = ReadPool(File.ReadAllText(poolPath));
        var settings = settingsPath is null ? new SimulationSettings() : SimulationSettings.Parse(File.ReadAllText(settingsPath));
        return Create(ruleset, map, pool, settings, rulesetPath ?? "docs/ruleset.default.json", mapPath, poolPath);
    }

    private static readonly JsonSerializerOptions s_poolJson = new(JsonSerializerDefaults.Web);

    public static PoolFile ReadPool(string json) =>
        JsonSerializer.Deserialize<PoolFile>(json, s_poolJson)
            ?? throw new JsonException("Pool JSON is null.");

    /// <summary>The pool as the engine sees it: ids by position, hours generated where the file has none (D-353).</summary>
    public static (PoolSnapshot Pool, int Generated) BuildPool(PoolFile pool, MissingHours missing)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(missing);
        var random = new SimRandom(missing.Seed);
        var generated = 0;
        var games = new List<Game>();
        for (var i = 0; i < pool.Games.Count; i++)
        {
            var game = pool.Games[i];
            var hours = game.Hours;
            if (hours is not > 0)
            {
                var drawn = Math.Clamp(random.LogNormal(missing.Median, missing.Sigma), missing.Min, missing.Max);
                hours = Math.Max((decimal)missing.Min, Math.Round((decimal)drawn * 2, MidpointRounding.AwayFromZero) / 2);
                generated++;
            }

            games.Add(new Game(SimIds.Make(0x9a3e0000, i + 1), game.Title, [.. game.Tags], hours));
        }

        var categories = pool.Categories.Select(c => new Category(c.Name, c.Weight)).ToList();
        return (new PoolSnapshot(games, categories), generated);
    }
}
