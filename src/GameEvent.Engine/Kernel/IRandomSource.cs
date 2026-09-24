namespace GameEvent.Engine.Kernel;

/// <summary>The only source of randomness for the engine. Results are stored in events, never re-rolled on replay.</summary>
public interface IRandomSource
{
    /// <summary>Returns an integer in [minInclusive, maxExclusive).</summary>
    int NextInt(int minInclusive, int maxExclusive);
}
