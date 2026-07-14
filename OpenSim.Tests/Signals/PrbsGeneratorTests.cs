using OpenSim.Core.Signals;

namespace OpenSim.Tests.Signals;

/// <summary>
/// The PRBS LFSR (SI Stage S2), gated by the m-sequence structure theorems rather than
/// spot values: period exactly 2^n − 1, balance (2^(n−1) ones per period), and the full
/// run-length distribution — together these pin "is a maximal-length sequence" for the
/// implemented taps. Orders 23/31 are gated on determinism and non-degeneracy only
/// (their full periods are 8M/2G bits — the structure gates on 7–15 cover the shared
/// LFSR code path).
/// </summary>
public class PrbsGeneratorTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(15)]
    public void Period_IsExactlyTwoToTheNMinusOne(int order)
    {
        int period = (int)PrbsGenerator.PeriodBits(order);
        // The sequence must repeat after the period and at no earlier divisor-free
        // point; comparing one full period against the next catches both.
        var bits = PrbsGenerator.Generate(order, 2 * period);
        for (int i = 0; i < period; i++)
            Assert.Equal(bits[i], bits[i + period]);
        // Not repeating earlier: some index differs for every proper prefix shift.
        foreach (int early in new[] { period / 7, period / 3, period / 2 })
        {
            if (early < 1) continue;
            bool differs = false;
            for (int i = 0; i < period - early && !differs; i++)
                differs = bits[i] != bits[i + early];
            Assert.True(differs, $"order {order}: repeated early at shift {early}");
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(15)]
    public void Balance_OnesOutnumberZerosByExactlyOne(int order)
    {
        int period = (int)PrbsGenerator.PeriodBits(order);
        var bits = PrbsGenerator.Generate(order, period);
        int ones = bits.Count(b => b);
        Assert.Equal(1 << (order - 1), ones);
        Assert.Equal((1 << (order - 1)) - 1, period - ones);
    }

    [Fact]
    public void RunLengths_MatchTheMSequenceDistribution()
    {
        // Cyclic run structure of an order-n m-sequence: one run of n ones, one run of
        // n−1 zeros, and 2^(n−2−k) runs of EACH polarity for lengths 1 ≤ k ≤ n−2.
        const int order = 7;
        int period = (int)PrbsGenerator.PeriodBits(order);
        var bits = PrbsGenerator.Generate(order, period);

        var runs = new List<(bool Value, int Length)>();
        int start = 0;
        while (start < period && bits[(start + period - 1) % period] == bits[start]) start++;
        Assert.True(start < period, "constant sequence — not an m-sequence at all");
        int i0 = start, count = 0;
        bool current = bits[start];
        for (int s = 0; s < period; s++)
        {
            bool b = bits[(i0 + s) % period];
            if (b == current) count++;
            else
            {
                runs.Add((current, count));
                current = b;
                count = 1;
            }
        }
        runs.Add((current, count));

        Assert.Equal(1 << (order - 1), runs.Count);
        Assert.Equal(1, runs.Count(r => r.Value && r.Length == order));
        Assert.Equal(1, runs.Count(r => !r.Value && r.Length == order - 1));
        for (int k = 1; k <= order - 2; k++)
        {
            int expected = 1 << (order - 2 - k);
            Assert.Equal(expected, runs.Count(r => r.Value && r.Length == k));
            Assert.Equal(expected, runs.Count(r => !r.Value && r.Length == k));
        }
    }

    [Theory]
    [InlineData(23)]
    [InlineData(31)]
    public void HighOrders_AreDeterministicAndNonDegenerate(int order)
    {
        var a = PrbsGenerator.Generate(order, 4096, seed: 123);
        var b = PrbsGenerator.Generate(order, 4096, seed: 123);
        Assert.Equal(a, b);                                  // seed-deterministic
        Assert.Contains(true, a);
        Assert.Contains(false, a);                           // not stuck
        var c = PrbsGenerator.Generate(order, 4096, seed: 456);
        Assert.NotEqual(a.ToList(), c.ToList());             // seed actually matters
    }

    [Fact]
    public void ZeroSeed_IsCoercedOffTheLfsrFixedPoint()
    {
        var bits = PrbsGenerator.Generate(7, 256, seed: 0);
        Assert.Contains(true, bits);
    }

    [Fact]
    public void UnsupportedOrder_IsATypedFailure()
    {
        var ex = Assert.Throws<ArgumentException>(() => PrbsGenerator.Generate(8, 10));
        Assert.Contains("not supported", ex.Message);
    }
}
