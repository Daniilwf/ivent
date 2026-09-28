using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using GameEvent.Simulator.Play;
using GameEvent.Simulator.Report;
using GameEvent.Simulator.Setup;

namespace GameEvent.Simulator.Cli;

/// <summary>Runs many seasons in parallel; run <c>i</c> plays with a seed derived from the base seed and <c>i</c> (D-351).</summary>
public static class SimulationRunner
{
    public static JsonSerializerOptions ReportJson { get; } = CreateOptions();

    /// <summary>The seed of run <paramref name="index"/> of a simulation with <paramref name="seed"/>.</summary>
    public static ulong SeedOf(ulong seed, int index) => SimRandom.Mix(seed, (ulong)index + 1000);

    public static IReadOnlyList<SeasonOutcome> Play(SimulationInputs inputs, int runs, int days, ulong seed, int threads = 0)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);
        var outcomes = new SeasonOutcome[runs];
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount };
        Parallel.For(0, runs, options, i => outcomes[i] = new SeasonSimulation(inputs, days, i, SeedOf(seed, i)).Run());
        return outcomes;
    }

    /// <summary>Plays and builds the report, with the elapsed time.</summary>
    public static SimulationReport Simulate(SimulationInputs inputs, int runs, int days, ulong seed, int threads = 0)
    {
        var watch = Stopwatch.StartNew();
        var outcomes = Play(inputs, runs, days, seed, threads);
        return ReportBuilder.Build(inputs, days, seed, outcomes, watch.Elapsed.TotalSeconds);
    }

    public static string ToJson(SimulationReport report) => JsonSerializer.Serialize(report, ReportJson);

    /// <summary>Writes report.md and report.json into <paramref name="folder"/>.</summary>
    public static (string Markdown, string Json) Write(SimulationReport report, string folder)
    {
        Directory.CreateDirectory(folder);
        var markdown = Path.Combine(folder, "report.md");
        var json = Path.Combine(folder, "report.json");
        File.WriteAllText(markdown, MarkdownReport.Render(report));
        File.WriteAllText(json, ToJson(report) + "\n");
        return (markdown, json);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly();
        return options;
    }
}
