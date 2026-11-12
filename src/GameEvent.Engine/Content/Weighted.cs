using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Content;

/// <summary>A pick by weight (SPEC «Колёса и колоды — одна сущность»): one ticket over the sum of the weights, entries in order.</summary>
internal static class Weighted
{
    public static WheelEntrySpec Pick(IReadOnlyList<WheelEntrySpec> entries, IRandomSource random)
    {
        var ticket = random.NextInt(0, entries.Sum(e => e.Weight));
        return entries.First(e => (ticket -= e.Weight) < 0);
    }
}
