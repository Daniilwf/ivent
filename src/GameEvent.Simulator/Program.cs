using System.Globalization;
using System.Text.Json;
using GameEvent.Simulator.Cli;
using GameEvent.Simulator.Setup;

// The balance simulator (stage 3): bots play seasons through the same engine as the site and a report shows the balance.
var (arguments, error) = CliArguments.Parse(args);
if (arguments is null)
{
    Console.Error.WriteLine(error is null ? CliArguments.Usage : $"{error}{Environment.NewLine}{CliArguments.Usage}");
    return error is null ? 0 : 2;
}

SimulationInputs inputs;
try
{
    inputs = SimulationInputs.Load(arguments.Ruleset, arguments.Map, arguments.Pool, arguments.Settings);
}
catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"The inputs cannot be played:{Environment.NewLine}{e.Message}");
    return 1;
}

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Simulating {arguments.Runs} seasons of {arguments.Days} days, seed {arguments.Seed}, {inputs.Settings.Players.Sum(p => p.Count)} bots, {(inputs.Map is null ? "linear map" : "graph map " + arguments.Map)}…"));
var report = SimulationRunner.Simulate(inputs, arguments.Runs, arguments.Days, arguments.Seed, arguments.Threads);
var (markdown, json) = SimulationRunner.Write(report, arguments.Out);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Done in {report.Meta.ElapsedSeconds:F1} s: {markdown}, {json}"));
return 0;
