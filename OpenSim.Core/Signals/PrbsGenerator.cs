namespace OpenSim.Core.Signals;

/// <summary>
/// Pseudo-random binary sequences from a Fibonacci LFSR with the ITU-standard primitive
/// polynomials x^n + x^m + 1 (PRBS-7/9/11/15/23/31). Seed-deterministic: the same
/// (order, seed) always yields the same bits — SI eye diagrams must be reproducible.
/// One period is 2^n − 1 bits with 2^(n−1) ones and 2^(n−1) − 1 zeros (the m-sequence
/// balance property, test-gated along with the run-length distribution).
/// </summary>
public static class PrbsGenerator
{
    /// <summary>order → the second feedback tap m of x^n + x^m + 1 (ITU-T O.150 family).</summary>
    private static readonly IReadOnlyDictionary<int, int> Taps = new Dictionary<int, int>
    {
        [7] = 6, [9] = 5, [11] = 9, [15] = 14, [23] = 18, [31] = 28
    };

    public static IReadOnlyList<int> SupportedOrders { get; } =
        Taps.Keys.OrderBy(k => k).ToArray();

    /// <summary>One period of PRBS-<paramref name="order"/> is 2^order − 1 bits.</summary>
    public static long PeriodBits(int order)
    {
        RequireSupported(order);
        return (1L << order) - 1;
    }

    /// <summary>Generates <paramref name="count"/> bits. The all-zero LFSR state is its
    /// fixed point, so a seed that masks to zero is coerced to 1 rather than emitting a
    /// silent constant stream.</summary>
    public static bool[] Generate(int order, int count, uint seed = 1)
    {
        RequireSupported(order);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        int tap = Taps[order];
        uint mask = (1u << order) - 1;
        uint state = seed & mask;
        if (state == 0) state = 1;

        var bits = new bool[count];
        for (int i = 0; i < count; i++)
        {
            uint newBit = ((state >> (order - 1)) ^ (state >> (tap - 1))) & 1u;
            bits[i] = newBit == 1;
            state = ((state << 1) | newBit) & mask;
        }
        return bits;
    }

    private static void RequireSupported(int order)
    {
        if (!Taps.ContainsKey(order))
            throw new ArgumentException(
                $"PRBS order {order} is not supported; use one of "
                + string.Join(", ", Taps.Keys.OrderBy(k => k)) + ".");
    }
}
