using OpenSim.Core.Signals;
using OpenSim.Rf.Si;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Si;

/// <summary>
/// Fix 13 — the eye height is measured against the bits that were SENT.
///
/// <para>Sorting the received samples into "high" and "low" by their own level measures the gap
/// around the threshold, which is positive whatever the channel does. A single-pole channel has
/// a closed-form worst case: after a long run of zeros a one reaches 1 − e^{−T/τ} by the end of
/// its bit, after a long run of ones a zero has only fallen to e^{−T/τ}, so the opening is
/// 1 − 2e^{−T/τ} of the swing — negative once τ exceeds T/ln 2 = 1.44 UI.</para>
/// </summary>
public class EyeMetricsTests
{
    private readonly ITestOutputHelper _output;
    public EyeMetricsTests(ITestOutputHelper output) => _output = output;

    private const int Spu = 32;
    private const double Dt = 31.25e-12;

    /// <summary>The periodic steady state of an ideal 0/1 NRZ pattern through a single-pole
    /// low-pass of the given time constant (in UI), by the exact per-sample exponential.</summary>
    private static double[] SinglePole(bool[] bits, double tauUi)
    {
        double decay = Math.Exp(-1.0 / (tauUi * Spu));
        int total = bits.Length * Spu;
        var wave = new double[total];
        double y = 0.5;
        for (int pass = 0; pass < 6; pass++)
            for (int n = 0; n < total; n++)
            {
                double x = bits[n / Spu] ? 1.0 : 0.0;
                y = x + (y - x) * decay;        // value at the END of sample n
                wave[n] = y;
            }
        return wave;
    }

    [Fact]
    public void ASinglePoleChannelOfThreeUi_ReportsAClosedEye()
    {
        // The plan's gate. 1 − 2e^{−1/3} = −0.433 for unlimited run lengths; PRBS-7's longest
        // runs (7 ones, 6 zeros) stop well short of the rails at this time constant, so the
        // worst one starts from about 0.12 and the worst zero from about 0.91: measured −0.288
        // at the best sampling phase.
        var bits = PrbsGenerator.Generate(7, 127);
        var wave = SinglePole(bits, tauUi: 3.0);
        var eye = EyeDiagram.Fold(wave, Spu, Dt, bits);
        _output.WriteLine($"height {eye.EyeHeight:g4}, width {eye.EyeWidthSeconds:g4}, "
            + $"latency {eye.LatencySeconds / (Spu * Dt):g4} UI, phase {eye.SamplingPhaseUi:g3}");

        Assert.True(eye.ClassifiedByTransmittedBits);
        Assert.True(eye.IsClosed);
        Assert.InRange(eye.EyeHeight, 1 - 2 * Math.Exp(-1.0 / 3) - 1e-9, -0.20);
        Assert.Equal(0.0, eye.EyeWidthSeconds);

        // The level-classified measurement on the same waveform: a positive "opening".
        var byLevel = EyeDiagram.Fold(wave, Spu, Dt);
        Assert.False(byLevel.ClassifiedByTransmittedBits);
        Assert.False(byLevel.IsClosed);
        Assert.True(byLevel.EyeHeight >= 0);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void ASinglePoleChannel_OpensByTheClosedForm(double tauUi)
    {
        // Open eyes: 1 − 2e^{−T/τ} sampled at the end of the bit. PRBS-7's finite runs leave the
        // worst one and the worst zero e^{−6T/τ}-close to the unlimited-run values.
        var bits = PrbsGenerator.Generate(7, 127);
        var eye = EyeDiagram.Fold(SinglePole(bits, tauUi), Spu, Dt, bits);
        double expected = 1 - 2 * Math.Exp(-1.0 / tauUi);
        _output.WriteLine($"τ = {tauUi} UI: height {eye.EyeHeight:g5}, closed form {expected:g5}");

        Assert.False(eye.IsClosed);
        Assert.InRange(eye.EyeHeight, expected, expected + 2.5 * Math.Exp(-6.0 / tauUi) + 1e-9);
        Assert.True(eye.EyeWidthSeconds > 0);
    }

    [Fact]
    public void ALosslessMatchedLine_ReportsTheFullSwing()
    {
        // The plan's second gate. A lossless line with matched source and load delivers the
        // source waveform delayed and halved: nothing closes the eye, so its height is the whole
        // received swing and its width the whole unit interval.
        const double c0 = 299792458.0;
        var bits = PrbsGenerator.Generate(7, 127);
        var source = SourceWaveform.Trapezoid(bits, Spu, riseFractionOfUi: 0.25, highLevel: 1.0, lowLevel: 0.0);
        double capacitance = 1.0 / (50 * c0), inductance = 50 / c0;
        var rlgc = new RlgcResult(1, new[,] { { capacitance } }, new double[1, 1],
            new[,] { { capacitance } }, new[,] { { inductance } },
            new[] { 0.0 }, new[] { 0.0 }, Array.Empty<string>());
        var network = new MtlNetwork(new[] { new MtlSection(rlgc, c0 * 53.4 * Dt) });   // 1.67 UI long
        var solved = TransientLink.SolvePeriodic(network, new[] { new LineTermination(50, 50) },
            0, source, Dt);

        var eye = EyeDiagram.Fold(solved.FarVoltages[0], Spu, Dt, bits);
        _output.WriteLine($"height {eye.EyeHeight:g6} of swing {eye.High - eye.Low:g6}; "
            + $"latency {eye.LatencySeconds / Dt:g5} samples");
        // (The periodic solve of a quarter-UI edge rings by 0.3 % of the swing at the corners.)
        Assert.Equal(0.5, eye.High - eye.Low, 2);
        Assert.InRange(eye.EyeHeight / (eye.High - eye.Low), 0.99, 1.0);
        Assert.Equal(eye.UnitIntervalSeconds, eye.EyeWidthSeconds, 13);
        // The correlation finds the line's delay: 53.4 samples of line plus the 4 samples to
        // the middle of the 8-sample edge, which is where a bit's energy is centred.
        Assert.InRange(eye.LatencySeconds / Dt, 57.4 - 1, 57.4 + 1);
    }

    [Fact]
    public void TheLatency_IsFoundWhateverTheDelay()
    {
        // Ideal square edges, so the bit boundaries of the waveform are exactly where the
        // delay put them.
        var bits = PrbsGenerator.Generate(7, 127);
        var wave = SourceWaveform.Trapezoid(bits, Spu, 0, 0.8, -0.8);
        foreach (int delay in new[] { 0, 5, 37, 127 * Spu - 9 })
        {
            var shifted = new double[wave.Length];
            for (int n = 0; n < wave.Length; n++) shifted[(n + delay) % wave.Length] = wave[n];
            var eye = EyeDiagram.Fold(shifted, Spu, Dt, bits);
            Assert.Equal(1.6, eye.EyeHeight, 9);
            Assert.Equal(delay * Dt, eye.LatencySeconds, 15);
        }
    }

    [Fact]
    public void ASampleExactlyOnTheThreshold_IsStillACrossing()
    {
        // ±1 levels with a two-sample edge passing through exactly 0, the mid-level. The strict
        // sign test dropped both sample pairs around that zero, so no crossing was found at all.
        var bits = new[] { true, false, true, true, false, false, true, false };
        const int spu = 8;
        var wave = new double[bits.Length * spu];
        for (int n = 0; n < wave.Length; n++)
        {
            bool now = bits[n / spu], before = bits[(n / spu + bits.Length - 1) % bits.Length];
            wave[n] = n % spu == 0 && now != before ? 0.0 : now ? 1.0 : -1.0;
        }
        int transitions = Enumerable.Range(0, bits.Length)
            .Count(b => bits[b] != bits[(b + bits.Length - 1) % bits.Length]);

        var eye = EyeDiagram.Fold(wave, spu, 1e-12, bits);
        Assert.Equal(transitions, eye.CrossingCount);
        Assert.True(eye.JitterPeakToPeakSeconds < 1e-18);
        Assert.Equal(2.0, eye.EyeHeight, 12);
        Assert.Equal(transitions, EyeDiagram.Fold(wave, spu, 1e-12).CrossingCount);
    }

    [Fact]
    public void TheBitsMustBeThePatternTheWaveformHolds()
    {
        var wave = new double[64 * 16];
        Assert.Throws<ArgumentException>(() => EyeDiagram.Fold(wave, 16, 1e-12, new bool[63]));
        Assert.Throws<ArgumentNullException>(() => EyeDiagram.Fold(wave, 16, 1e-12, null!));
    }

    [Fact]
    public void APatternLongerThanTheChannelFft_RunsThroughTheNPortEngine()
    {
        // PRBS-9 at 32 samples per UI is 16 352 samples, twice the 8192-point channel FFT. The
        // N-port engine refused it, so the IBIS eye threw for two of the three PRBS lengths the
        // UI offers. The FFT bounds the channel's memory, not the pattern. An all-linear run
        // with ONE warm-up period (what the app uses for long patterns) against the exact
        // periodic solver, and the eye it folds to.
        const double c0 = 299792458.0;
        var bits = PrbsGenerator.Generate(9, 511);
        var source = SourceWaveform.Trapezoid(bits, Spu, riseFractionOfUi: 0.25);
        Assert.True(source.Length > 8192);
        double capacitance = 1.0 / (50 * c0), inductance = 50 / c0;
        var rlgc = new RlgcResult(1, new[,] { { capacitance } }, new double[1, 1],
            new[,] { { capacitance } }, new[,] { { inductance } },
            new[] { 0.0 }, new[] { 0.0 }, Array.Empty<string>());
        var network = new MtlNetwork(new[] { new MtlSection(rlgc, 0.05) });

        var exact = TransientLink.SolvePeriodic(network, new[] { new LineTermination(40, 75) },
            0, source, Dt);
        var nport = NonlinearLink.SolveNPort(network,
            new INonlinearDriver[]
            {
                new LinearTheveninDriver(t => source[Math.Clamp((int)Math.Round(t / Dt), 0, source.Length - 1)], 40)
            },
            new INonlinearDriver[] { new LinearLoadElement(75) }, source.Length, Dt, warmupPeriods: 1);

        double worst = 0;
        for (int n = 0; n < source.Length; n++)
            worst = Math.Max(worst, Math.Abs(exact.FarVoltages[0][n] - nport.FarVolts[0][n]));
        _output.WriteLine($"worst far-end difference {worst * 1e3:g3} mV; FIR {nport.ChannelMemorySamples} taps");
        // Measured 16.8 mV of a 1 V source: the engine's own discretization at this step (the
        // same with four warm-up periods, below), not something the long period adds.
        Assert.True(worst < 0.02, $"far end differs by {worst:g3} V");

        var fourWarmups = NonlinearLink.SolveNPort(network,
            new INonlinearDriver[]
            {
                new LinearTheveninDriver(t => source[Math.Clamp((int)Math.Round(t / Dt), 0, source.Length - 1)], 40)
            },
            new INonlinearDriver[] { new LinearLoadElement(75) }, source.Length, Dt, warmupPeriods: 4);
        double warmupDifference = 0;
        for (int n = 0; n < source.Length; n++)
            warmupDifference = Math.Max(warmupDifference,
                Math.Abs(fourWarmups.FarVolts[0][n] - nport.FarVolts[0][n]));
        // Measured 0.22 mV of a 1 V source.
        Assert.True(warmupDifference < 1e-3,
            $"one warm-up period is not the steady state: {warmupDifference:g3} V from four");

        var eye = EyeDiagram.Fold(nport.FarVolts[0], Spu, Dt, bits);
        Assert.Equal(511, eye.Traces.Count);
        Assert.False(eye.IsClosed);
    }
}
