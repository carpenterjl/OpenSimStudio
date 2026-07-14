using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Signals;

/// <summary>
/// The first-party FFT (SI Stage S2). The oracle is the naive O(N²) DFT — every length
/// class the SI pipeline uses is pinned against it: powers of two (radix-2), a prime
/// (pure Bluestein), a composite non-power-of-two, and the PRBS-7 pattern length
/// 127·64 = 8128 (the eye-diagram workload). Identities (Parseval, shift, round-trip)
/// hold at 1e-12 relative — the level the periodic-steady-state transient leans on.
/// </summary>
public class FftTests
{
    private static Complex[] NaiveDft(IReadOnlyList<Complex> x, bool inverse)
    {
        int n = x.Count;
        double sign = inverse ? 1 : -1;
        var result = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            Complex sum = Complex.Zero;
            for (int m = 0; m < n; m++)
            {
                double angle = sign * 2 * Math.PI * ((long)k * m % n) / n;
                sum += x[m] * new Complex(Math.Cos(angle), Math.Sin(angle));
            }
            result[k] = inverse ? sum / n : sum;
        }
        return result;
    }

    private static Complex[] RandomSignal(int n, int seed)
    {
        var rng = new Random(seed);
        var x = new Complex[n];
        for (int i = 0; i < n; i++)
            x[i] = new Complex(2 * rng.NextDouble() - 1, 2 * rng.NextDouble() - 1);
        return x;
    }

    private static double RelativeError(IReadOnlyList<Complex> got, IReadOnlyList<Complex> want)
    {
        double err = 0, norm = 0;
        for (int i = 0; i < got.Count; i++)
        {
            err = Math.Max(err, (got[i] - want[i]).Magnitude);
            norm = Math.Max(norm, want[i].Magnitude);
        }
        return err / Math.Max(norm, 1e-300);
    }

    [Theory]
    [InlineData(8)]      // radix-2
    [InlineData(64)]     // radix-2
    [InlineData(12)]     // composite non-power-of-two → Bluestein
    [InlineData(127)]    // prime → Bluestein (one PRBS-7 period)
    [InlineData(8128)]   // 127·64, the eye-diagram sample count → Bluestein
    public void Forward_MatchesNaiveDftOracle(int n)
    {
        var x = RandomSignal(n, seed: 20260713 + n);
        Assert.True(RelativeError(Fft.Forward(x), NaiveDft(x, inverse: false)) < 1e-12);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(127)]
    public void Inverse_MatchesNaiveDftOracle(int n)
    {
        var x = RandomSignal(n, seed: 7 + n);
        Assert.True(RelativeError(Fft.Inverse(x), NaiveDft(x, inverse: true)) < 1e-12);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(381)]   // 3·127
    public void RoundTrip_RecoversTheSignal(int n)
    {
        var x = RandomSignal(n, seed: 42);
        Assert.True(RelativeError(Fft.Inverse(Fft.Forward(x)), x) < 1e-12);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(127)]
    public void Parseval_EnergyIsConserved(int n)
    {
        var x = RandomSignal(n, seed: 99);
        var spectrum = Fft.Forward(x);
        double time = x.Sum(v => v.Magnitude * v.Magnitude);
        double freq = spectrum.Sum(v => v.Magnitude * v.Magnitude) / n;
        Assert.True(Math.Abs(time - freq) / time < 1e-12);
    }

    [Fact]
    public void Delta_HasFlatUnitSpectrum()
    {
        var x = new Complex[64];
        x[0] = Complex.One;
        foreach (var bin in Fft.Forward(x))
            Assert.True((bin - Complex.One).Magnitude < 1e-13);
    }

    [Theory]
    [InlineData(64, 5)]
    [InlineData(127, 11)]
    public void ShiftTheorem_Holds(int n, int shift)
    {
        var x = RandomSignal(n, seed: 3);
        var shifted = new Complex[n];
        for (int i = 0; i < n; i++) shifted[i] = x[(i + n - shift) % n];

        var spectrum = Fft.Forward(x);
        var expected = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            double angle = -2 * Math.PI * ((long)k * shift % n) / n;
            expected[k] = spectrum[k] * new Complex(Math.Cos(angle), Math.Sin(angle));
        }
        Assert.True(RelativeError(Fft.Forward(shifted), expected) < 1e-12);
    }

    [Fact]
    public void EmptyInput_IsATypedFailure()
        => Assert.Throws<ArgumentException>(() => Fft.Forward(Array.Empty<Complex>()));
}
