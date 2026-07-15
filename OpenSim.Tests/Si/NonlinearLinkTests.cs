using OpenSim.Core.Signals;
using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Ibis;

namespace OpenSim.Tests.Si;

/// <summary>
/// The nonlinear IBIS driver engine gates (SI Stage S11b). The anchor is the identity gate:
/// a LINEAR Thevenin driver run through the nonlinear engine must reproduce the exact linear
/// <see cref="TransientLink"/> (this exercises the whole channel-FIR + Newton + backward-Euler
/// path — a convolution/truncation/Newton bug breaks it). The DC switching level equals the
/// V-I / load-line intersection; the FIR truncation converges; non-conforming inputs are typed
/// failures.
/// </summary>
public class NonlinearLinkTests
{
    private const double C0 = 299792458.0;

    private static RlgcResult SingleLine(double l, double c)
        => new(1, new[,] { { c } }, new[,] { { 0.0 } }, new[,] { { c } },
            new[,] { { l } }, new[] { 0.0 }, new[] { 0.0 }, Array.Empty<string>());

    // A 50 Ω line with a 0.5 ns one-way delay (v = 2e8 m/s ⇒ ℓ = 0.1 m).
    private static MtlNetwork Line50(double lengthMeters = 0.1)
    {
        double c = 100e-12, l = c * 50.0 * 50.0;                 // Z0 = √(L/C) = 50
        return new MtlNetwork(new[] { new MtlSection(SingleLine(l, c), lengthMeters) });
    }

    private static double[] Trapezoid(IReadOnlyList<bool> bits, int spu, double rise, double amp)
        => SourceWaveform.Trapezoid(bits, spu, rise, amp, 0);

    // ------------------------------------------------------------------
    // The hard identity: a linear driver ≡ the exact linear TransientLink.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(25.0, 75.0)]    // mismatched both ends — reflections exercise the channel FIR
    [InlineData(50.0, 1e9)]     // matched source, ~open receiver
    public void LinearDriver_ReproducesTheExactLinearTransientLink(double rs, double rl)
    {
        const int spu = 32;
        double dt = 1.0 / (1e9 * spu);
        var net = Line50();
        var bits = PrbsGenerator.Generate(7, 127);
        var source = Trapezoid(bits, spu, rise: 0.25, amp: 1.0);
        int period = source.Length;

        // Exact linear reference.
        var reference = TransientLink.SolvePeriodic(net,
            new[] { new LineTermination(rs, rl) }, 0, source, dt);

        // The same Thevenin source through the nonlinear engine.
        var driver = new LinearTheveninDriver(
            t => source[Math.Clamp((int)Math.Round(t / dt), 0, period - 1)], rs);
        var result = NonlinearLink.Solve(net, driver, new NonlinearReceiver(rl), bits, spu, dt,
            warmupPeriods: 3);

        // Peak-normalized band: the only difference is the FIR truncation + finite settling.
        double swing = source.Max() - source.Min();
        double maxErr = 0;
        for (int n = 0; n < period; n++)
            maxErr = Math.Max(maxErr, Math.Abs(result.ReceiverVolts[n] - reference.FarVoltages[0][n]));
        Assert.True(maxErr < 0.01 * swing,
            $"rs={rs} rl={rl}: max |Δ| = {maxErr:e3} of swing {swing:g3} (tail {result.TailEnergyFraction:e2})");
    }

    // ------------------------------------------------------------------
    // DC switching level ≡ the load-line intersection.
    // ------------------------------------------------------------------

    [Fact]
    public void DcHighLevel_MatchesTheLoadLineIntersection()
    {
        // Hold the driver high into a resistive load down a (DC-transparent) line; the settled
        // node voltage solves I_drv(V) = V·Y_in(0) = V/R_load independently.
        const double rload = 50.0, vcc = 3.3;
        var model = LinearIbis(gPullup: 1.0 / 30, gPulldown: 1.0 / 30, vcc: vcc);
        const int spu = 16;
        double dt = 1.0 / (1e9 * spu);
        var ones = Enumerable.Repeat(true, 8).ToList();         // steady high
        var driver = IbisDriver.FromBits(model, IbisCornerSelection.Typ, ones, spu, dt);
        // Integer-sample line delay (1 sample: ℓ = v·dt = 2e8·62.5ps = 0.0125 m) so the
        // channel FIR carries the exact DC gain — the same integer-delay choice the S5
        // identity gates make (a fractional delay spreads the sinc and its wrapped tail).
        var result = NonlinearLink.Solve(Line50(0.0125), driver, new NonlinearReceiver(rload),
            ones, spu, dt, warmupPeriods: 6);

        // Independent load line: pull-up is G_u(Vcc − V); high state ⇒ I_drv = G_u(Vcc − V).
        // G_u(Vcc − V) = V/R ⇒ V = Vcc / (1 + 1/(G_u·R)).
        double gu = 1.0 / 30;
        double expected = vcc / (1 + 1.0 / (gu * rload));
        double settled = result.ReceiverVolts[^1];
        Assert.True(Math.Abs(settled - expected) < 0.02 * vcc,
            $"DC high {settled:g4} V vs load-line {expected:g4} V");
    }

    // ------------------------------------------------------------------
    // FIR truncation convergence + typed failures.
    // ------------------------------------------------------------------

    [Fact]
    public void ChannelTruncation_Converges_AsTheBoundTightens()
    {
        const int spu = 32;
        double dt = 1.0 / (1e9 * spu);
        var net = Line50();
        var bits = PrbsGenerator.Generate(7, 127);
        var source = Trapezoid(bits, spu, 0.25, 1.0);
        var driver = new LinearTheveninDriver(
            t => source[Math.Clamp((int)Math.Round(t / dt), 0, source.Length - 1)], 25);

        var loose = NonlinearLink.Solve(net, driver, new NonlinearReceiver(75), bits, spu, dt,
            warmupPeriods: 3, tailEnergyBound: 1e-3);
        var tight = NonlinearLink.Solve(net, driver, new NonlinearReceiver(75), bits, spu, dt,
            warmupPeriods: 3, tailEnergyBound: 1e-6);
        Assert.True(tight.ChannelMemorySamples >= loose.ChannelMemorySamples, "tighter bound ⇒ longer FIR");
        double maxDiff = 0;
        for (int n = 0; n < source.Length; n++)
            maxDiff = Math.Max(maxDiff, Math.Abs(tight.ReceiverVolts[n] - loose.ReceiverVolts[n]));
        Assert.True(maxDiff < 5e-3, $"truncation shift {maxDiff:e3} must be small");
    }

    [Fact]
    public void TypedFailures_NameTheProblem()
    {
        int spu = 16;
        double dt = 1.0 / (1e9 * spu);
        var bits = new[] { true, false, true, false };

        // Multi-line network is rejected (nonlinear crosstalk is a follow-up).
        var coupled = new MtlNetwork(new[] { new MtlSection(
            new RlgcResult(2, new[,] { { 1e-10, -1e-11 }, { -1e-11, 1e-10 } }, new double[2, 2],
                new[,] { { 1e-10, -1e-11 }, { -1e-11, 1e-10 } },
                new[,] { { 2.5e-7, 2e-8 }, { 2e-8, 2.5e-7 } },
                new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 }, Array.Empty<string>()), 0.1) });
        Assert.Throws<ArgumentException>(() => NonlinearLink.Solve(coupled,
            new LinearTheveninDriver(_ => 1, 50), new NonlinearReceiver(50), bits, spu, dt));

        // A driver returning NaN never converges → a typed failure, not a garbage waveform.
        Assert.Throws<InvalidOperationException>(() => NonlinearLink.Solve(Line50(0.02),
            new NanDriver(), new NonlinearReceiver(50), bits, spu, dt));

        // A non-output IBIS model cannot be a driver.
        var input = new IbisModel { Name = "IN", ModelType = "Input" };
        Assert.Throws<ArgumentException>(() =>
            IbisDriver.FromBits(input, IbisCornerSelection.Typ, bits, spu, dt));
    }

    private sealed class NanDriver : INonlinearDriver
    {
        public double CompCapacitanceFarads => 0;
        public (double Current, double Conductance) Evaluate(double v, double t) => (double.NaN, 1);
    }

    // ------------------------------------------------------------------
    // A synthetic linear IBIS model: straight-line pull-up/pull-down conductances to the
    // rails, no clamps, no C_comp — the degenerate model whose steady states are analytic.
    // ------------------------------------------------------------------

    private static IbisModel LinearIbis(double gPullup, double gPulldown, double vcc)
    {
        // [Pulldown] I(V) = G_d·V (into pad, ground-referenced).
        var pd = new[]
        {
            new IbisIvRow(0.0, new IbisCorner(0, 0, 0)),
            new IbisIvRow(vcc, new IbisCorner(gPulldown * vcc, gPulldown * vcc, gPulldown * vcc)),
        };
        // [Pullup] I(V−Vcc) = G_u·(V−Vcc) (into pad, supply-referenced; negative when sourcing).
        var pu = new[]
        {
            new IbisIvRow(-vcc, new IbisCorner(-gPullup * vcc, -gPullup * vcc, -gPullup * vcc)),
            new IbisIvRow(0.0, new IbisCorner(0, 0, 0)),
        };
        return new IbisModel
        {
            Name = "LIN", ModelType = "Output", CComp = new IbisCorner(0, 0, 0),
            Pullup = pu, Pulldown = pd,
            VoltageRange = new IbisCorner(vcc, vcc, vcc),
            PullupReferenceVolts = vcc,
            Ramp = new IbisRamp(
                new IbisRampEdge(new IbisCorner(vcc, vcc, vcc), new IbisCorner(1e-10, 1e-10, 1e-10)),
                new IbisRampEdge(new IbisCorner(vcc, vcc, vcc), new IbisCorner(1e-10, 1e-10, 1e-10))),
        };
    }
}
