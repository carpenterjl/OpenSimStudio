using System.Numerics;
using OpenSim.Core.Signals;
using OpenSim.Rf.Channel;
using OpenSim.Rf.Si;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Si;

/// <summary>
/// Feature 9: channels read from Touchstone — interpolation, mixed-mode S, TDR, eyes.
/// The references are lossless lines in closed form and this program's own line solver
/// run directly on the model the file was written from.
/// </summary>
public class ChannelTests
{
    private readonly ITestOutputHelper _out;
    public ChannelTests(ITestOutputHelper output) => _out = output;

    private const double L = 3.5e-7, Lm = 8e-8, C = 1.3e-10, Cm = 2.2e-11;

    private static RlgcResult SingleLine(double l, double c)
        => new(1, new[,] { { c } }, new[,] { { 0.0 } }, new[,] { { c } },
            new[,] { { l } }, new[] { 0.0 }, new[] { 0.0 }, Array.Empty<string>());

    private static RlgcResult Pair(double c2 = C)
        => new(2, new[,] { { C, -Cm }, { -Cm, c2 } }, new double[2, 2],
            new[,] { { C, -Cm }, { -Cm, c2 } }, new[,] { { L, Lm }, { Lm, L } },
            new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 }, Array.Empty<string>());

    /// <summary>S11 and S21 of a lossless line of impedance zc and delay tau, in z0.</summary>
    private static (Complex S11, Complex S21) LineS(double zc, double tau, double f, double z0)
    {
        double theta = 2 * Math.PI * f * tau;
        Complex denominator = new(2 * zc * z0 * Math.Cos(theta), (zc * zc + z0 * z0) * Math.Sin(theta));
        return (new Complex(0, (zc * zc - z0 * z0) * Math.Sin(theta)) / denominator, 2 * zc * z0 / denominator);
    }

    private static TouchstoneData Sample(MtlNetwork network, double start, double stop, int points, double z0 = 50)
    {
        var frequencies = Enumerable.Range(0, points).Select(k => start + (stop - start) * k / (points - 1)).ToList();
        return new TouchstoneData(frequencies, frequencies.Select(f => network.Scattering(f, z0)).ToList(), z0);
    }

    /// <summary>The same data through Touchstone text, as a file would bring it.</summary>
    private static ImportedChannel ThroughFile(TouchstoneData data) =>
        new(TouchstoneReader.Read(TouchstoneWriter.Write(data.FrequenciesHz, data.Scattering, data.ReferenceOhms), data.Ports));

    // ---------------- Mixed mode ----------------

    [Fact]
    public void MixedMode_SymmetricPair_IsTheOddAndEvenLines()
    {
        const double length = 0.08;
        var network = new MtlNetwork(new[] { new MtlSection(Pair(), length) });
        double zOdd = Math.Sqrt((L - Lm) / (C + Cm)), zEven = Math.Sqrt((L + Lm) / (C - Cm));
        double tauOdd = length * Math.Sqrt((L - Lm) * (C + Cm)), tauEven = length * Math.Sqrt((L + Lm) * (C - Cm));
        var pairing = DifferentialPairing.For(PortOrder.NearFirstHalf);
        double worst = 0, conversion = 0;
        foreach (double f in new[] { 0.3e9, 1.1e9, 2.7e9, 5.3e9, 9.1e9 })
        {
            var mixed = MixedMode.FromSingleEnded(network.Scattering(f, 50), pairing);
            var dd = MixedMode.Block(mixed, MixedMode.Differential, MixedMode.Differential);
            var cc = MixedMode.Block(mixed, MixedMode.Common, MixedMode.Common);
            // Differential port in 100 Ω on a 2·Z_odd line ≡ the odd line in 50 Ω; likewise even.
            var odd = LineS(zOdd, tauOdd, f, 50);
            var even = LineS(zEven, tauEven, f, 50);
            worst = Math.Max(worst, (dd[0, 0] - odd.S11).Magnitude);
            worst = Math.Max(worst, (dd[1, 0] - odd.S21).Magnitude);
            worst = Math.Max(worst, (cc[0, 0] - even.S11).Magnitude);
            worst = Math.Max(worst, (cc[1, 0] - even.S21).Magnitude);
            foreach (var block in new[] { MixedMode.Block(mixed, MixedMode.Common, MixedMode.Differential),
                         MixedMode.Block(mixed, MixedMode.Differential, MixedMode.Common) })
                foreach (Complex value in block) conversion = Math.Max(conversion, value.Magnitude);
        }
        _out.WriteLine($"Z_odd {zOdd:f2} Ω, Z_even {zEven:f2} Ω: worst |S_dd, S_cc − mode line| {worst:e2}; mode conversion {conversion:e2}");
        Assert.True(worst < 1e-9, $"mixed-mode blocks differ from the mode lines by {worst:e2}");
        Assert.True(conversion < 1e-12, $"a symmetric pair converted modes: {conversion:e2}");
    }

    [Fact]
    public void MixedMode_UnbalancedPair_ConvertsModes_AndPortOrderIsDetected()
    {
        var network = new MtlNetwork(new[] { new MtlSection(Pair(c2: 1.15 * C), 0.08) });
        var data = Sample(network, 0, 10e9, 201);
        var channel = new ImportedChannel(data);
        Assert.Equal(PortOrder.NearFirstHalf, channel.DetectOrder());
        var mixed = MixedMode.FromSingleEnded(channel.At(3e9), DifferentialPairing.For(PortOrder.NearFirstHalf));
        double scd21 = mixed[3, 0].Magnitude;
        _out.WriteLine($"15 % more capacitance on one leg: |S_cd21| = {scd21:f4} at 3 GHz");
        Assert.True(scd21 > 0.01);

        // The same network with its ports renumbered near, far, near, far.
        var order = new[] { 0, 2, 1, 3 };   // new port p is old port order[p]
        var renumbered = new TouchstoneData(data.FrequenciesHz, data.Scattering.Select(s =>
        {
            var m = new Complex[4, 4];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++) m[i, j] = s[order[i], order[j]];
            return m;
        }).ToList(), 50);
        var other = new ImportedChannel(renumbered);
        Assert.Equal(PortOrder.NearOddFarEven, other.DetectOrder());
        var same = MixedMode.FromSingleEnded(other.At(3e9), DifferentialPairing.For(PortOrder.NearOddFarEven));
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True((mixed[i, j] - same[i, j]).Magnitude < 1e-12);

        // Mixed-mode Touchstone reads back as the same matrices.
        var text = MixedMode.ToTouchstone(channel, DifferentialPairing.For(PortOrder.NearFirstHalf));
        var back = TouchstoneReader.Read(text, 4);
        int at = 60;
        var direct = MixedMode.FromSingleEnded(data.Scattering[at], DifferentialPairing.For(PortOrder.NearFirstHalf));
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True((back.Scattering[at][i, j] - direct[i, j]).Magnitude < 1e-9);
        Assert.Throws<ArgumentException>(() => MixedMode.FromSingleEnded(new Complex[2, 2], default));
        Assert.Throws<ArgumentException>(() => MixedMode.FromSingleEnded(new Complex[4, 4], new DifferentialPairing(0, 0, 1, 2)));
    }

    // ---------------- Import and interpolation ----------------

    [Fact]
    public void ImportedChannel_InterpolatesBetweenPoints_AndReachesDc()
    {
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(3.6e-7, 1e-10), 0.15) });   // 60 Ω, 0.9 ns
        var channel = ThroughFile(Sample(network, 20e6, 8e9, 400));   // no DC point in the file
        double worst = 0;
        for (double f = 31e6; f < 8e9; f += 173.3e6)
        {
            var exact = network.Scattering(f, 50);
            var read = channel.At(f);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++) worst = Math.Max(worst, (exact[i, j] - read[i, j]).Magnitude);
        }
        var dc = channel.At(0);
        var exactDc = network.Scattering(0, 50);
        double dcError = Math.Max((dc[0, 0] - exactDc[0, 0]).Magnitude, (dc[1, 0] - exactDc[1, 0]).Magnitude);
        _out.WriteLine($"between 20 MHz points: worst |ΔS| {worst:e2}; at DC (file starts at 20 MHz): {dcError:e2}");
        Assert.True(worst < 1e-3, $"interpolation error {worst:e2}");
        Assert.True(dcError < 1e-3, $"DC extrapolation error {dcError:e2}");
        Assert.Equal(0, dc[1, 0].Imaginary, 12);
        Assert.Contains(channel.Notes, n => n.Contains("DC"));
        Assert.All(channel.Checks, c => Assert.True(c.IsPassive() && c.IsReciprocal()));
        Assert.Contains(channel.Describe(), l => l.StartsWith("Passive"));
        Assert.True(channel.Covers(8e9));
        Assert.False(channel.Covers(8.1e9));
    }

    [Fact]
    public void ImportedChannel_SaysWhenTheFrequencyStepIsTooCoarse()
    {
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(3.6e-7, 1e-10), 1.5) });   // 9 ns of delay
        var channel = new ImportedChannel(Sample(network, 0, 8e9, 41));                             // 200 MHz steps
        Assert.Contains(channel.Notes, n => n.Contains("too coarse"));
    }

    // ---------------- TDR ----------------

    private static Complex CascadeS11(double f, double z0, params (double Zc, double Tau)[] sections)
    {
        // ABCD of the sections in order, then S11 into a z0 load.
        Complex a = 1, b = 0, c = 0, d = 1;
        foreach (var (zc, tau) in sections)
        {
            double theta = 2 * Math.PI * f * tau;
            Complex a2 = Math.Cos(theta), b2 = new(0, zc * Math.Sin(theta)), c2 = new(0, Math.Sin(theta) / zc), d2 = Math.Cos(theta);
            (a, b, c, d) = (a * a2 + b * c2, a * b2 + b * d2, c * a2 + d * c2, c * b2 + d * d2);
        }
        return (a + b / z0 - c * z0 - d) / (a + b / z0 + c * z0 + d);
    }

    [Fact]
    public void Tdr_ReadsEachSection_AndPeelingRemovesTheEarlierReflections()
    {
        var sections = new[] { (65.0, 0.5e-9), (40.0, 0.6e-9), (50.0, 0.4e-9), (80.0, 0.5e-9) };
        var profile = Tdr.Profile(f => CascadeS11(f, 50, sections), 20e9, 10e6, 50, maxTimeSeconds: 5e-9);

        // Round-trip times of the section middles.
        double first = profile.At(0.5e-9), second = profile.At(1.6e-9);
        double secondPeeled = profile.At(1.6e-9, peeled: true), firstPeeled = profile.At(0.5e-9, peeled: true);
        double thirdPeeled = profile.At(2.6e-9, peeled: true), fourthPeeled = profile.At(3.5e-9, peeled: true);
        double fourth = profile.At(3.5e-9);
        double before = profile.At(-0.3e-9);

        // What an instrument shows in the second section: the first interface's reflection
        // plus the second's, through the first both ways.
        double r1 = (65.0 - 50) / (65 + 50), r2 = (40.0 - 65) / (40 + 65);
        double apparent = 50 * (1 + r1 + (1 - r1 * r1) * r2) / (1 - r1 - (1 - r1 * r1) * r2);
        _out.WriteLine($"rise {profile.RiseTimeSeconds * 1e12:f1} ps; section 1: {first:f3} Ω (65); section 2: shown {second:f3} Ω " +
                       $"(apparent {apparent:f3}), peeled {secondPeeled:f3} (40); section 3 peeled {thirdPeeled:f3} (50); " +
                       $"section 4: shown {fourth:f3}, peeled {fourthPeeled:f3} (80)");
        Assert.Equal(50, before, 2);
        Assert.InRange(first, 64.9, 65.1);
        Assert.InRange(firstPeeled, 64.9, 65.1);
        Assert.InRange(second / apparent, 0.998, 1.002);
        Assert.InRange(secondPeeled, 39.9, 40.1);
        Assert.InRange(thirdPeeled, 49.8, 50.2);
        Assert.InRange(fourthPeeled, 79.5, 80.5);
        Assert.True(Math.Abs(fourth - 80) > 3 * Math.Abs(fourthPeeled - 80), "peeling should improve the last section");
        Assert.Contains(profile.Notes, n => n.Contains("lossless"));

        // A faster edge than the data supports is said to be so.
        var fast = Tdr.Profile(f => CascadeS11(f, 50, sections), 20e9, 10e6, 50, riseTimeSeconds: 10e-12, peel: false);
        Assert.Contains(fast.Notes, n => n.Contains("beyond the data"));
        Assert.Null(fast.PeeledOhms);
    }

    [Fact]
    public void Tdr_DifferentialProfile_ReadsTwiceTheOddImpedance()
    {
        var network = new MtlNetwork(new[] { new MtlSection(Pair(), 0.12) });
        var channel = new ImportedChannel(Sample(network, 0, 20e9, 1001));
        var profile = Tdr.DifferentialProfile(channel, DifferentialPairing.For(PortOrder.NearFirstHalf), maxTimeSeconds: 3e-9);
        double zOdd = Math.Sqrt((L - Lm) / (C + Cm));
        double tau = 0.12 * Math.Sqrt((L - Lm) * (C + Cm));
        double inside = profile.At(tau);   // half-way along, round trip
        _out.WriteLine($"differential TDR: {inside:f3} Ω inside the pair (2·Z_odd = {2 * zOdd:f3}); after it {profile.At(2 * tau + 0.5e-9, true):f2} Ω");
        Assert.InRange(inside / (2 * zOdd), 0.998, 1.002);
        Assert.InRange(profile.At(2 * tau + 0.5e-9, peeled: true), 99, 101);   // the 100 Ω differential load
    }

    // ---------------- Eyes ----------------

    [Fact]
    public void Eye_ThroughAnImportedFile_IsTheLineSolversOwn()
    {
        const int spu = 16;
        const double bitRate = 1e9, dt = 1 / (bitRate * spu);
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(3.6e-7, 1e-10), 0.15) });
        var channel = ThroughFile(Sample(network, 0, 8e9, 401));
        var setup = new ChannelEyeSetup
        {
            BitRate = bitRate, SamplesPerUi = spu, RiseFractionOfUi = 0.25, SourceOhms = 40, LoadOhms = 75, Swing = 1
        };
        var eye = ChannelEye.SingleEnded(channel, 0, 1, setup);

        var bits = PrbsGenerator.Generate(7, 127);
        var source = SourceWaveform.Trapezoid(bits, spu, 0.25);
        var exact = TransientLink.SolvePeriodic(network, new[] { new LineTermination(40, 75) }, 0, source, dt);
        double worst = 0;
        for (int n = 0; n < source.Length; n++)
            worst = Math.Max(worst, Math.Abs(exact.FarVoltages[0][n] - eye.Waveform[n]));
        var reference = EyeDiagram.Fold(exact.FarVoltages[0], spu, dt, bits);
        _out.WriteLine($"far-end waveform through the file against the line solver: worst {worst * 1e3:f3} mV; " +
                       $"eye height {eye.Eye.EyeHeight:f4} V against {reference.EyeHeight:f4} V");
        _out.WriteLine(string.Join("\n", eye.Describe()));
        Assert.True(worst < 3e-4, $"waveforms differ by {worst:g3} V");
        Assert.Equal(reference.EyeHeight, eye.Eye.EyeHeight, 2);
        Assert.False(eye.Eye.IsClosed);
        Assert.Null(eye.CommonMode);
    }

    [Fact]
    public void DifferentialEye_ThroughAnImportedFile_IsTheLineSolversOwn()
    {
        const int spu = 16;
        const double bitRate = 1e9, dt = 1 / (bitRate * spu);
        var bits = PrbsGenerator.Generate(7, 127);
        var plus = SourceWaveform.Trapezoid(bits, spu, 0.25, 0.4, -0.4);   // ±0.8 V between the legs
        var minus = plus.Select(v => -v).ToArray();
        var setup = new ChannelEyeSetup { BitRate = bitRate, SamplesPerUi = spu, RiseFractionOfUi = 0.25, Swing = 0.8 };

        (ChannelEyeResult Eye, double Worst) Run(RlgcResult rlgc)
        {
            var network = new MtlNetwork(new[] { new MtlSection(rlgc, 0.2) });
            var channel = ThroughFile(Sample(network, 0, 8e9, 401));
            var eye = ChannelEye.Differential(channel, DifferentialPairing.For(channel.DetectOrder()), setup);
            var exact = TransientLink.SolvePeriodic(network,
                new[] { new LineTermination(50, 50), new LineTermination(50, 50) }, new double[]?[] { plus, minus }, dt);
            double worst = 0;
            for (int n = 0; n < plus.Length; n++)
                worst = Math.Max(worst, Math.Abs(exact.FarVoltages[0][n] - exact.FarVoltages[1][n] - eye.Waveform[n]));
            return (eye, worst);
        }

        var balanced = Run(Pair());
        var skewed = Run(Pair(c2: 1.15 * C));
        double balancedCommon = balanced.Eye.CommonMode!.Max() - balanced.Eye.CommonMode.Min();
        double skewedCommon = skewed.Eye.CommonMode!.Max() - skewed.Eye.CommonMode.Min();
        _out.WriteLine($"differential waveform against the line solver: worst {balanced.Worst * 1e3:f3} mV (balanced), " +
                       $"{skewed.Worst * 1e3:f3} mV (one leg 15 % heavier)");
        _out.WriteLine($"common mode at the receiver: {balancedCommon * 1e3:e2} mV balanced, {skewedCommon * 1e3:f2} mV unbalanced; " +
                       $"eye {balanced.Eye.Eye.EyeHeight:f4} V and {skewed.Eye.Eye.EyeHeight:f4} V");
        Assert.True(balanced.Worst < 3e-4 && skewed.Worst < 3e-4);
        Assert.True(balancedCommon < 1e-9, "a balanced pair driven differentially has no common mode");
        Assert.True(skewedCommon > 5e-3);
        Assert.True(balanced.Eye.Eye.EyeHeight > 0.3);
        Assert.Contains(skewed.Eye.Describe(), l => l.StartsWith("Common mode"));
    }

    [Fact]
    public void Eye_SaysWhatTheFileDoesNotCover()
    {
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(2.5e-7, 1e-10), 0.1) });
        var channel = new ImportedChannel(Sample(network, 0, 3e9, 301));
        // Left to itself the run takes the sample rate the file supports: Nyquist 2 GHz at 4 per bit.
        var auto = ChannelEye.SingleEnded(channel, 0, 1, new ChannelEyeSetup { BitRate = 1e9 });
        Assert.Equal(4, auto.SamplesPerUi);
        Assert.DoesNotContain(auto.Notes, n => n.Contains("left out"));
        // Forced finer, the part of the spectrum above the file is dropped and reported.
        var forced = ChannelEye.SingleEnded(channel, 0, 1, new ChannelEyeSetup { BitRate = 1e9, SamplesPerUi = 32, RiseFractionOfUi = 0.1 });
        Assert.Contains(forced.Notes, n => n.Contains("left out"));
        Assert.Throws<InvalidOperationException>(() => ChannelEye.Differential(channel, default, new ChannelEyeSetup()));
    }

    // ---------------- The nonlinear engine on an imported channel ----------------

    [Fact]
    public void NonlinearEngine_OnAnImportedChannel_MatchesTheLineModel()
    {
        const int spu = 16;
        const double dt = 1 / (1e9 * spu);
        var network = new MtlNetwork(new[] { new MtlSection(SingleLine(3.6e-7, 1e-10), 0.15) });
        var channel = ThroughFile(Sample(network, 0, 8e9, 801));
        var source = SourceWaveform.Trapezoid(PrbsGenerator.Generate(7, 127), spu, 0.25);
        INonlinearDriver[] Near() => new INonlinearDriver[]
        {
            new LinearTheveninDriver(t => source[(int)Math.Round(t / dt) % source.Length], 40)
        };
        INonlinearDriver[] Far() => new INonlinearDriver[] { new LinearLoadElement(75, 2e-12) };

        var direct = NonlinearLink.SolveNPort(network, Near(), Far(), source.Length, dt, warmupPeriods: 2);
        var imported = NonlinearLink.SolveNPort(1, channel.TransferImpedanceFor(dt, PortOrder.NearOddFarEven),
            Near(), Far(), source.Length, dt, warmupPeriods: 2);
        double worst = 0;
        for (int n = 0; n < source.Length; n++)
            worst = Math.Max(worst, Math.Abs(direct.FarVolts[0][n] - imported.FarVolts[0][n]));
        _out.WriteLine($"N-port engine, receiver with 2 pF: imported channel against the line model, worst {worst * 1e3:f3} mV");
        Assert.True(worst < 3e-4, $"far end differs by {worst:g3} V");

        // The transfer impedance itself, at a frequency between the file's points.
        var zDirect = network.TransferImpedance(1.2345e9, 0.02);
        var zFile = channel.TransferImpedance(1.2345e9, 0.02, PortOrder.NearOddFarEven);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.True((zDirect[i, j] - zFile[i, j]).Magnitude < 0.05, $"Z'[{i},{j}] differs");

        // A step finer than the file's last frequency allows is refused, with the step it does allow.
        var ex = Assert.Throws<InvalidOperationException>(() => channel.TransferImpedanceFor(dt / 4, PortOrder.NearOddFarEven));
        Assert.Contains("62.5 ps", ex.Message);
    }
}
