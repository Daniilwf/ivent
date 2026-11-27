using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Statistics;

namespace GameEvent.Web.Seasons;

internal static class SavedRoll
{
    public static RollMeasurement? Measure(string diceJson, string challengeDiceJson, string? diceModsJson)
    {
        var dice = JsonSerializer.Deserialize<EquatableArray<Die>>(diceJson, EngineJson.Options);
        var challenge = JsonSerializer.Deserialize<EquatableArray<Die>>(challengeDiceJson, EngineJson.Options);
        var mods = diceModsJson is null ? null : JsonSerializer.Deserialize<RunDiceMods>(diceModsJson, EngineJson.Options);
        return RollStatistics.Measure(dice, challenge, mods);
    }
}
