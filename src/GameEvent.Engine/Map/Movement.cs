namespace GameEvent.Engine.Map;

/// <summary>Movement along the map graph.</summary>
public static class Movement
{
    /// <summary>
    /// Path of <paramref name="steps"/> forward steps from <paramref name="from"/> along default forward edges.
    /// Stops at the finish: extra steps burn.
    /// </summary>
    public static IReadOnlyList<string> Forward(MapGraph map, string from, int steps) =>
        throw new NotImplementedException("B1");
}
