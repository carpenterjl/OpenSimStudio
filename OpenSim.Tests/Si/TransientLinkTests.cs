using OpenSim.Core.Signals;
using OpenSim.Rf.Si;

namespace OpenSim.Tests.Si;

/// <summary>
/// The periodic-steady-state transient gates (SI Stage S5). The engine's exactness
/// claims are identities, not tolerances: a matched lossless line is a pure circular
/// shift of the source (the delay is chosen as an integer number of samples, so the
/// discrete spectrum reproduces it EXACTLY at every bin); the response to a PRBS
/// pattern is EXACTLY the bit-weighted superposition of the single-bit pulse response
/// (trapezoid NRZ is a shifted-pulse sum and the network is linear); multi-source
/// solves superpose. The reflection staircase and RC receiver carry the textbook
/// physics with small bands for the finite settling of the periodic square.
/// </summary>
public class TransientLinkTests
{
    private const double C0 = 299792458.0;
    private static readonly double AirL = 50.0 / C0;
    private static readonly double AirC = 1.0 / (50.0 * C0);

    private static RlgcResult SingleLine(double l, double c)
        => new(1, new[,] { { c } }, new[,] { { 0.0 } }, new[,] { { c } },
            new[,] { { l } }, new[] { 0.0 }, new[] { 0.0 }, Array.Empty<string>());

    private static RlgcResult CoupledPair(double l, double lm, double c, double cm)
        => new(2, new[,] { { c, -cm }, { -cm, c } }, new double[2, 2],
            new[,] { { c, -cm }, { -cm, c } }, new[,] { { l, lm }, { lm, l } },
            new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 }, Array.Empty<string>());

    [Fact]
    public void MatchedLine_IsAPureCircularShiftOfHalfTheSource()
    {
        const int spu = 32;
        const double bitRate = 1e9;
        double dt = 1.0 / (bitRate * spu);
        double delay = 32 * dt;                                   // exactly 32 samples
        double length = C0 * delay;

        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), length) });
        var bits = PrbsGenerator.Generate(7, 127);
        var source = SourceWaveform.Trapezoid(bits, spu, riseFractionOfUi: 0);
        var result = TransientLink.SolvePeriodic(network,
            new[] { new LineTermination(50, 50) }, 0, source, dt);

        int n = source.Length;
        for (int s = 0; s < n; s++)
        {
            double expected = 0.5 * source[(s - 32 + n) % n];
            Assert.True(Math.Abs(result.FarVoltages[0][s] - expected) < 1e-9,
                $"sample {s}: {result.FarVoltages[0][s]} vs {expected}");
        }
    }

    [Fact]
    public void PrbsResponse_IsTheBitWeightedPulseResponseSuperposition()
    {
        // A deliberately mismatched, reflective channel (Rs 20 Ω, load 200 Ω): heavy
        // ISI, and the identity must STILL be machine-level — pure linearity.
        const int spu = 16, bitCount = 31;
        const double dt = 1.0 / (2e9 * spu);
        double length = C0 * 24 * dt;                             // an awkward, ISI-making delay
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), length) });
        var terms = new[] { new LineTermination(20, 200) };

        var bits = PrbsGenerator.Generate(9, bitCount);
        var prbs = SourceWaveform.Trapezoid(bits, spu, riseFractionOfUi: 0.3);
        var prbsResponse = TransientLink.SolvePeriodic(network, terms, 0, prbs, dt)
            .FarVoltages[0];

        var singleBit = new bool[bitCount];
        singleBit[0] = true;
        var pulse = SourceWaveform.Trapezoid(singleBit, spu, riseFractionOfUi: 0.3);
        var pulseResponse = TransientLink.SolvePeriodic(network, terms, 0, pulse, dt)
            .FarVoltages[0];

        int n = prbs.Length;
        var superposed = new double[n];
        for (int k = 0; k < bitCount; k++)
        {
            if (!bits[k]) continue;
            int shift = k * spu;
            for (int s = 0; s < n; s++)
                superposed[(s + shift) % n] += pulseResponse[s];
        }
        for (int s = 0; s < n; s++)
            Assert.True(Math.Abs(prbsResponse[s] - superposed[s]) < 1e-9,
                $"sample {s}: {prbsResponse[s]} vs superposed {superposed[s]}");
    }

    [Fact]
    public void OpenLine_ShowsTheTextbookReflectionStaircase()
    {
        // Rs = 3·Z0 (Γ_source = 0.5), open far end: after the k-th arrival the far
        // voltage sits at 1 − 2^{−k} (0.5, 0.75, 0.875, …) — the reflection staircase.
        const int total = 4096;
        const int delaySamples = 64;
        const double dt = 25e-12;
        double length = C0 * delaySamples * dt;
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), length) });
        var square = SourceWaveform.Pulse(total, 0, total / 2);   // long half-periods: settled
        var far = TransientLink.SolvePeriodic(network,
            new[] { new LineTermination(150, double.PositiveInfinity) }, 0, square, dt)
            .FarVoltages[0];

        for (int k = 1; k <= 4; k++)
        {
            // Mid-plateau after the k-th arrival: t = (2k − 1)·T + T/2.
            int sample = (2 * k - 1) * delaySamples + delaySamples / 2;
            double expected = 1 - Math.Pow(2, -k);
            Assert.True(Math.Abs(far[sample] - expected) < 1e-3,
                $"arrival {k}: {far[sample]} vs staircase level {expected}");
        }
    }

    [Fact]
    public void CapacitiveReceiver_FollowsTheRcExponential()
    {
        // A transparent line into an RC receiver: the high half-period is the classic
        // 1 − e^{−t/τ}, τ = Rs·C (open R). Band 2%: the finite periodic square vs the
        // ideal step, plus the trig-interpolant edge.
        const int total = 2048;
        const double dt = 10e-12;
        double tau = 16 * dt;
        double capacitance = tau / 50;
        double length = C0 * dt * 1e-3;                           // transparent
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), length) });
        var square = SourceWaveform.Pulse(total, 0, total / 2);
        var far = TransientLink.SolvePeriodic(network,
            new[] { new LineTermination(50, double.PositiveInfinity, capacitance) },
            0, square, dt).FarVoltages[0];

        foreach (int sample in new[] { 16, 32, 64, 128, 512 })
        {
            double expected = 1 - Math.Exp(-sample * dt / tau);
            Assert.True(Math.Abs(far[sample] - expected) < 0.02,
                $"t = {sample} samples: {far[sample]} vs RC {expected}");
        }
    }

    [Fact]
    public void MultiSource_SuperposesExactly()
    {
        const int spu = 16;
        const double dt = 1.0 / (1e9 * spu);
        double length = C0 * 40 * dt;
        var network = new MtlNetwork(new[]
            { new MtlSection(CoupledPair(3.5e-7, 5e-8, 1.3e-10, 1.2e-11), length) });
        var terms = new[] { new LineTermination(50, 75), new LineTermination(50, 75) };

        var victim = SourceWaveform.Trapezoid(PrbsGenerator.Generate(7, 31), spu, 0.25);
        var aggressor = SourceWaveform.Trapezoid(PrbsGenerator.Generate(7, 31, seed: 77), spu, 0.25);

        var both = TransientLink.SolvePeriodic(network, terms,
            new[] { victim, aggressor }, dt);
        var victimOnly = TransientLink.SolvePeriodic(network, terms, 0, victim, dt);
        var aggressorOnly = TransientLink.SolvePeriodic(network, terms, 1, aggressor, dt);

        for (int s = 0; s < victim.Length; s++)
        {
            double sum = victimOnly.FarVoltages[0][s] + aggressorOnly.FarVoltages[0][s];
            Assert.True(Math.Abs(both.FarVoltages[0][s] - sum) < 1e-10, "superposition");
        }
        // And the aggressor genuinely couples — the victim's quiet-line response is real.
        Assert.True(aggressorOnly.FarVoltages[0].Max(Math.Abs) > 1e-4,
            "the coupled pair must produce visible crosstalk");
    }

    [Fact]
    public void TypedFailures_NameTheProblem()
    {
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(AirL, AirC), 0.01) });
        var terms = new[] { new LineTermination(50, 50) };
        Assert.Throws<ArgumentException>(() => TransientLink.SolvePeriodic(
            network, terms, new double[]?[] { null }, 1e-12));
        var pair = new MtlNetwork(new[]
            { new MtlSection(CoupledPair(3.5e-7, 5e-8, 1.3e-10, 1.2e-11), 0.01) });
        Assert.Throws<ArgumentException>(() => TransientLink.SolvePeriodic(
            pair, new[] { new LineTermination(50, 50), new LineTermination(50, 50) },
            new[] { new double[64], new double[128] }, 1e-12));
    }
}

/// <summary>Eye-diagram gates: the ideal channel's eye is EXACT (height ≡ swing,
/// width ≡ UI, jitter ≡ 0), the folded traces cover every bit, and a crosstalk-loaded
/// victim eye closes relative to its quiet self — the visible SI physics.</summary>
public class EyeDiagramTests
{
    [Fact]
    public void IdealChannel_HasThePerfectEye()
    {
        const int spu = 32;
        var bits = PrbsGenerator.Generate(7, 127);
        var wave = SourceWaveform.Trapezoid(bits, spu, riseFractionOfUi: 0.25,
            highLevel: 0.8, lowLevel: -0.8);
        var eye = EyeDiagram.Fold(wave, spu, sampleIntervalSeconds: 31.25e-12);

        Assert.Equal(127, eye.Traces.Count);
        Assert.Equal(1.6, eye.EyeHeight, 9);                       // full swing
        Assert.Equal(eye.UnitIntervalSeconds, eye.EyeWidthSeconds, 12);
        Assert.True(eye.JitterPeakToPeakSeconds < 1e-15, "symmetric edges ⇒ zero jitter");
    }

    [Fact]
    public void ConstantPattern_DegradesToTheFullSwingWithoutCrossings()
    {
        var wave = new double[8 * 16];
        for (int i = 0; i < wave.Length; i++) wave[i] = 0.7;
        var eye = EyeDiagram.Fold(wave, 16, 1e-12);
        Assert.Equal(0.0, eye.EyeHeight, 12);                      // zero swing
        Assert.Equal(eye.UnitIntervalSeconds, eye.EyeWidthSeconds, 15);
    }

    [Fact]
    public void CrosstalkClosesTheVictimEye()
    {
        const double c0 = 299792458.0;
        const int spu = 16;
        const double dt = 1.0 / (2e9 * spu);
        double length = c0 * 50 * dt;
        var rlgc = new RlgcResult(2,
            new[,] { { 1.3e-10, -2.2e-11 }, { -2.2e-11, 1.3e-10 } }, new double[2, 2],
            new[,] { { 1.3e-10, -2.2e-11 }, { -2.2e-11, 1.3e-10 } },
            new[,] { { 3.5e-7, 8e-8 }, { 8e-8, 3.5e-7 } },
            new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 }, Array.Empty<string>());
        var network = new MtlNetwork(new[] { new MtlSection(rlgc, length) });
        var terms = new[] { new LineTermination(50, 60), new LineTermination(50, 60) };

        var victim = SourceWaveform.Trapezoid(PrbsGenerator.Generate(7, 127), spu, 0.3);
        var aggressor = SourceWaveform.Trapezoid(
            PrbsGenerator.Generate(7, 127, seed: 45), spu, 0.3);

        var quiet = TransientLink.SolvePeriodic(network, terms, 0, victim, dt);
        var loud = TransientLink.SolvePeriodic(network, terms,
            new[] { victim, aggressor }, dt);

        var quietEye = EyeDiagram.Fold(quiet.FarVoltages[0], spu, dt);
        var loudEye = EyeDiagram.Fold(loud.FarVoltages[0], spu, dt);
        Assert.True(loudEye.EyeHeight < quietEye.EyeHeight,
            $"aggressor must close the eye: {loudEye.EyeHeight:g4} vs quiet {quietEye.EyeHeight:g4}");
        Assert.True(quietEye.EyeHeight > 0.1, "the quiet eye must be open to begin with");
    }

    [Fact]
    public void DensityMap_CountsEveryTraceSample()
    {
        var bits = PrbsGenerator.Generate(7, 31);
        var wave = SourceWaveform.Trapezoid(bits, 16, 0.2);
        var eye = EyeDiagram.Fold(wave, 16, 1e-12);
        var map = eye.DensityMap(64);
        int count = 0;
        foreach (var v in map) count += v;
        Assert.Equal(31 * 32, count);                              // bits × (2·spu)
    }

    [Fact]
    public void TypedFailures_NameTheProblem()
    {
        Assert.Throws<ArgumentException>(() => EyeDiagram.Fold(new double[100], 16, 1e-12));
        Assert.Throws<ArgumentOutOfRangeException>(() => EyeDiagram.Fold(new double[64], 2, 1e-12));
    }
}
