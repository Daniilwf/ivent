using GameEvent.Engine.Kernel;

namespace GameEvent.Simulator.Play;

/// <summary>
/// A seeded SplitMix64 generator: the engine's dice and the bots' choices. Its own algorithm, not <see cref="Random"/>,
/// so a seed gives the same season on every .NET version and machine (D-351).
/// </summary>
public sealed class SimRandom(ulong seed) : IRandomSource
{
    private ulong _state = seed;

    /// <summary>A seed derived from <paramref name="seed"/> and <paramref name="stream"/>: independent streams of one run.</summary>
    public static ulong Mix(ulong seed, ulong stream)
    {
        var z = seed + (0x9E3779B97F4A7C15UL * (stream + 1));
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextULong()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), $"Empty range [{minInclusive}, {maxExclusive}).");
        }

        // Rejection sampling: no modulo bias, whatever the range.
        var range = (ulong)((long)maxExclusive - minInclusive);
        var limit = ulong.MaxValue - (ulong.MaxValue % range);
        ulong value;
        do
        {
            value = NextULong();
        }
        while (value >= limit);

        return (int)((long)minInclusive + (long)(value % range));
    }

    /// <summary>A double in [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public bool Chance(double probability) => probability > 0 && NextDouble() < probability;

    /// <summary>A standard normal value (Box–Muller).</summary>
    public double Normal()
    {
        var u1 = 1.0 - NextDouble();
        var u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>A log-normal value with the given median: <c>median × e^(σZ)</c>.</summary>
    public double LogNormal(double median, double sigma) => median * Math.Exp(sigma * Normal());

    /// <summary>An index drawn by the weights (all non-negative, at least one positive).</summary>
    public int Weighted(IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var total = weights.Sum();
        var ticket = NextDouble() * total;
        for (var i = 0; i < weights.Count; i++)
        {
            if (ticket < weights[i])
            {
                return i;
            }

            ticket -= weights[i];
        }

        return weights.Count - 1;
    }
}
