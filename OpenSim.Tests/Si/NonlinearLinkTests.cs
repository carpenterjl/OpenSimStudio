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

    // ------------------------------------------------------------------
    // SI-15: no time-aliasing on low-loss, open-ended lines; warm-up covers the memory.
    // ------------------------------------------------------------------

    // A lossless line of characteristic impedance z0 whose one-way delay is exactly
    // delaySamples·dt, so it is exact on the sampling grid and any difference from the exact
    // periodic solve belongs to the engine.
    private static MtlNetwork LosslessLine(double z0, int delaySamples, double dt)
    {
        double c = 1.0 / (z0 * C0), l = z0 / C0;
        return new MtlNetwork(new[] { new MtlSection(SingleLine(l, c), C0 * delaySamples * dt) });
    }

    [Fact]
    public void OpenEndedLosslessLine_ReflectionsArriveOnTime_WithNoWrapAround()
    {
        // Voltage-forced at the near end and open at the far end, the line's driving-point
        // admittance has |Γ| = 1 at both ends: its impulse response never decays, so a FIR
        // sampled from it on a finite DFT wraps around. The gate is the exact periodic solve and
        // the arrival times themselves — nothing at the driver before 2·T_d after the edge.
        const int spu = 16, delay = 6;
        double dt = 1.0 / (1e9 * spu);
        var net = LosslessLine(50, delay, dt);
        var bits = new bool[32];
        bits[0] = true;                                             // one isolated pulse
        var source = Trapezoid(bits, spu, rise: 0.25, amp: 1.0);
        const double rs = 25;
        var reference = TransientLink.SolvePeriodic(net,
            new[] { new LineTermination(rs, double.PositiveInfinity) }, 0, source, dt);
        var driver = new LinearTheveninDriver(
            t => source[Math.Clamp((int)Math.Round(t / dt), 0, source.Length - 1)], rs);

        var result = NonlinearLink.Solve(net, driver,
            new NonlinearReceiver(double.PositiveInfinity), bits, spu, dt);

        double worstNear = 0, worstFar = 0;
        for (int n = 0; n < source.Length; n++)
        {
            worstNear = Math.Max(worstNear, Math.Abs(result.DriverVolts[n] - reference.NearVoltages[0][n]));
            worstFar = Math.Max(worstFar, Math.Abs(result.ReceiverVolts[n] - reference.FarVoltages[0][n]));
        }
        Assert.True(worstNear < 1e-3 && worstFar < 1e-3,
            $"against the exact periodic solve: driver {worstNear:e3} V, receiver {worstFar:e3} V");

        // Before the edge the line is quiet (the previous pulse's echoes have died away at
        // Γ_s·Γ_L = −1/3 per round trip), and after it the driver sees the incident divider
        // alone until the first echo returns at 2·T_d.
        int firstMove = Array.FindIndex(source, v => v != 0);
        for (int n = 0; n < firstMove; n++)
            Assert.True(Math.Abs(result.DriverVolts[n]) < 1e-6, $"energy at sample {n} before the edge");
        for (int n = firstMove; n < firstMove + 2 * delay; n++)
            Assert.True(Math.Abs(result.DriverVolts[n] - source[n] * 50 / (50 + rs)) < 1e-6,
                $"sample {n}: the driver moved before the first echo could return");
    }

    private static double WorstAgainstExact(NonlinearNPortResult result, TransientResult reference)
    {
        double worst = 0;
        for (int n = 0; n < reference.NearVoltages[0].Length; n++)
            worst = Math.Max(worst, Math.Max(
                Math.Abs(result.NearVolts[0][n] - reference.NearVoltages[0][n]),
                Math.Abs(result.FarVolts[0][n] - reference.FarVoltages[0][n])));
        return worst;
    }

    [Fact]
    public void ALineLongerThanTheDefaultWindow_KeepsItsEchoesInTheFir()
    {
        // A 75 Ω line under the default 50 Ω reference, open at the far end and 1500 samples
        // one way. Reference-terminated, each echo keeps |Γ_ref| = 0.2 of itself per round
        // trip; the default 8192-point window holds under three round trips, so the third echo
        // (0.2³ = 0.8 %) wrapped onto the start of the FIR. The source is matched, so the
        // circuit itself settles in one round trip and what is left is the window.
        const int spu = 16, delay = 1500;
        double dt = 1.0 / (1e9 * spu);
        var net = LosslessLine(75, delay, dt);
        var bits = new[] { true, true, false, true, false, false, true, false };
        var source = Trapezoid(bits, spu, rise: 0.25, amp: 1.0);
        const double rs = 75;
        var reference = TransientLink.SolvePeriodic(net,
            new[] { new LineTermination(rs, double.PositiveInfinity) }, 0, source, dt);

        var result = NonlinearLink.SolveNPort(net,
            new INonlinearDriver[] { new LinearTheveninDriver(
                t => source[Math.Clamp((int)Math.Round(t / dt), 0, source.Length - 1)], rs) },
            new INonlinearDriver[] { new LinearLoadElement(double.PositiveInfinity) },
            source.Length, dt);

        double worst = WorstAgainstExact(result, reference);
        Assert.True(worst < 1e-3, $"against the exact periodic solve: {worst:e3} V "
            + $"(memory {result.ChannelMemorySamples}, warm-up {result.WarmupPeriods} periods)");
    }

    [Fact]
    public void AShortPatternOnALongLine_RunsUntilThePeriodRepeats()
    {
        // The same line driven from 30 Ω into 200 Ω: the circuit keeps Γ_s·Γ_L = 0.43 × 0.45
        // of its echo per 3000-sample round trip, so it needs several round trips to settle,
        // and the pattern period is 128 samples — four warm-up periods do not even reach the
        // far end. Neither the requested warm-up nor the FIR memory is the settling time.
        const int spu = 16, delay = 1500;
        double dt = 1.0 / (1e9 * spu);
        var net = LosslessLine(75, delay, dt);
        var bits = new[] { true, true, false, true, false, false, true, false };
        var source = Trapezoid(bits, spu, rise: 0.25, amp: 1.0);
        const double rs = 30;
        var reference = TransientLink.SolvePeriodic(net,
            new[] { new LineTermination(rs, 200) }, 0, source, dt);

        var result = NonlinearLink.SolveNPort(net,
            new INonlinearDriver[] { new LinearTheveninDriver(
                t => source[Math.Clamp((int)Math.Round(t / dt), 0, source.Length - 1)], rs) },
            new INonlinearDriver[] { new LinearLoadElement(200) }, source.Length, dt);

        Assert.True(result.WarmupPeriods * source.Length >= result.ChannelMemorySamples,
            $"warm-up {result.WarmupPeriods} × {source.Length} samples is shorter than the "
            + $"channel memory {result.ChannelMemorySamples}");
        Assert.True(result.SettlingResidualVolts < 1e-5,
            $"the last two periods still differ by {result.SettlingResidualVolts:e2} V");
        double worst = WorstAgainstExact(result, reference);
        Assert.True(worst < 1e-3, $"against the exact periodic solve: {worst:e3} V "
            + $"(warm-up {result.WarmupPeriods} periods)");
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
        // [Pullup] on the IBIS axis V_table = Vcc − V: I = G_u·(V−Vcc) = −G_u·V_table (into
        // pad, "Vcc relative"; negative when sourcing).
        var pu = new[]
        {
            new IbisIvRow(0.0, new IbisCorner(0, 0, 0)),
            new IbisIvRow(vcc, new IbisCorner(-gPullup * vcc, -gPullup * vcc, -gPullup * vcc)),
        };
        return new IbisModel
        {
            Name = "LIN", ModelType = "Output", CComp = new IbisCorner(0, 0, 0),
            Pullup = pu, Pulldown = pd,
            VoltageRange = new IbisCorner(vcc, vcc, vcc),
            PullupReference = new IbisCorner(vcc, vcc, vcc),
            Ramp = new IbisRamp(
                new IbisRampEdge(new IbisCorner(vcc, vcc, vcc), new IbisCorner(1e-10, 1e-10, 1e-10)),
                new IbisRampEdge(new IbisCorner(vcc, vcc, vcc), new IbisCorner(1e-10, 1e-10, 1e-10))),
        };
    }
}
