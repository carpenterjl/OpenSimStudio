namespace OpenSim.Core.Numerics;

/// <summary>
/// SplitMix64 (Steele, Lea and Flood 2014): a 64-bit generator whose every output is a fixed
/// function of the seed and the draw count, so a Monte Carlo plan reproduces its points on
/// any runtime — <see cref="Random"/> makes no such promise across .NET versions.
/// <para>
/// For parallel work, give each work item its own stream with <see cref="ForStream"/>. A stream
/// depends only on the seed and the item's index, never on which thread ran it or in what order,
/// so the results are the same at any degree of parallelism.
/// </para>
/// </summary>
public sealed class SplitMix64
{
    private ulong _state;

    public SplitMix64(long seed) => _state = (ulong)seed;

    private SplitMix64(ulong state) => _state = state;

    /// <summary>
    /// The generator for work item <paramref name="stream"/> of a run with the given seed. Its
    /// starting state is Mix(Mix(seed) xor stream), where Mix is the generator's own output
    /// function. Mix is one-to-one, so two different streams of one seed never start at the same
    /// state. All streams walk the same cycle of 2⁶⁴ states from scattered starting points; two
    /// streams of length L overlap with probability about L / 2⁶³, which is negligible for any
    /// run that fits in memory.
    /// </summary>
    public static SplitMix64 ForStream(long seed, long stream) =>
        new(Mix(Mix((ulong)seed) ^ (ulong)stream));

    public ulong Next()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform on [0, 1): the top 53 bits as a double.</summary>
    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Standard normal by Box–Muller (one deviate per two uniforms, the other discarded,
    /// so each draw depends on a fixed pair of outputs).</summary>
    public double NextNormal()
    {
        double u1 = 1.0 - NextDouble();                       // (0, 1]: the log is finite
        double u2 = NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>The output function of <see cref="Next"/> applied to a value directly.</summary>
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
