using System.Globalization;

namespace GameEvent.Simulator.Cli;

/// <summary>
/// The command line: <c>--runs N --days N [--ruleset path] [--map path] [--pool path] [--settings path] [--seed n]
/// [--out folder] [--threads n]</c>.
/// </summary>
public sealed record CliArguments(
    int Runs,
    int Days,
    string? Ruleset,
    string? Map,
    string Pool,
    string? Settings,
    ulong Seed,
    string Out,
    int Threads)
{
    public const string Usage =
        """
        Usage: dotnet run --project src/GameEvent.Simulator -- [options]
          --runs N          seasons to play (default 1000)
          --days N          season length in days, the deadline (default 21)
          --ruleset path    ruleset JSON (default docs/ruleset.default.json, built into the engine)
          --map path        graph map JSON (switches the ruleset to features.mapMode = graph)
          --pool path       pool JSON (default content/pool.demo.json)
          --settings path   bots' settings JSON (default: built-in profiles, see docs/SIMULATION.md)
          --seed n          base seed (default 1): the same seed gives the same report
          --out folder      where report.md and report.json go (default var/simulation)
          --threads n       parallel seasons (default: all cores)
        """;

    /// <summary>Parses <paramref name="args"/>; the error names the bad option.</summary>
    public static (CliArguments? Arguments, string? Error) Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = new CliArguments(1000, 21, null, null, "content/pool.demo.json", null, 1, "var/simulation", 0);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name is "--help" or "-h")
            {
                return (null, null);
            }

            if (i + 1 >= args.Count)
            {
                return (null, $"{name} needs a value");
            }

            var value = args[++i];
            switch (name)
            {
                case "--runs" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var runs) && runs is >= 1 and <= 1_000_000:
                    result = result with { Runs = runs };
                    break;
                case "--days" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 365:
                    result = result with { Days = days };
                    break;
                case "--seed" when ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seed):
                    result = result with { Seed = seed };
                    break;
                case "--threads" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var threads) && threads <= 256:
                    result = result with { Threads = threads };
                    break;
                case "--ruleset":
                    result = result with { Ruleset = value };
                    break;
                case "--map":
                    result = result with { Map = value };
                    break;
                case "--pool":
                    result = result with { Pool = value };
                    break;
                case "--settings":
                    result = result with { Settings = value };
                    break;
                case "--out":
                    result = result with { Out = value };
                    break;
                default:
                    return (null, $"bad option {name} {value}");
            }
        }

        return (result, null);
    }
}
