using System.Text;
using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Ibis;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Si;

/// <summary>
/// Fix 14 — when an IBIS buffer's edges happen.
///
/// <para>Three things were wrong with the timing. The [Ramp] edge time was Vcc divided by the
/// slew, although the ramp's dV is by definition the 20 %–80 % part of the swing into the test
/// load: the whole swing along that line takes dt/0.6, whatever the load. The switching
/// schedule was cut to one unit interval, so a buffer slower than the bit either froze part-way
/// or (ramp) was silently sped up to fit. And the die capacitance was stepped by backward
/// Euler, which lengthens its time constant.</para>
/// </summary>
public class IbisEdgeTimingTests
{
    private readonly ITestOutputHelper _output;
    public IbisEdgeTimingTests(ITestOutputHelper output) => _output = output;

    private const double C0 = 299_792_458.0;
    private const double Vcc = 3.3;
    private const double OutputOhms = 25.0;

    /// <summary>A buffer with equal linear pull-up and pull-down (25 Ω), a ramp, optionally
    /// waveforms, optionally only one of its two stages.</summary>
    private static string Ibs(double rampSeconds, double cComp = 0, string type = "Output",
        bool pullup = true, bool pulldown = true, string? extra = null)
    {
        double g = 1 / OutputOhms;
        double swing = Vcc * 50 / (50 + OutputOhms);          // into the ramp's 50 Ω load
        string dv = (0.6 * swing).ToString("R");
        var sb = new StringBuilder();
        sb.AppendLine("[IBIS Ver]      2.1");
        sb.AppendLine("[File Name]     edge.ibs");
        sb.AppendLine("[Component]     EDGE");
        sb.AppendLine("[Model]         BUF");
        sb.AppendLine($"Model_type      {type}");
        sb.AppendLine($"C_comp          {cComp:R}");
        sb.AppendLine($"[Voltage Range] {Vcc}      {Vcc}      {Vcc}");
        if (pullup)
        {
            sb.AppendLine("[Pullup]");
            for (int k = -1; k <= 3; k++)
            {
                double vt = k * Vcc / 2;
                sb.AppendLine($"   {vt:R}   {-g * vt:R}   {-g * vt:R}   {-g * vt:R}");
            }
        }
        if (pulldown)
        {
            sb.AppendLine("[Pulldown]");
            for (int k = -1; k <= 3; k++)
            {
                double v = k * Vcc / 2;
                sb.AppendLine($"   {v:R}   {g * v:R}   {g * v:R}   {g * v:R}");
            }
        }
        sb.AppendLine("[Ramp]");
        sb.AppendLine($"dV/dt_r     {dv}/{rampSeconds:R}    {dv}/{rampSeconds:R}    {dv}/{rampSeconds:R}");
        sb.AppendLine($"dV/dt_f     {dv}/{rampSeconds:R}    {dv}/{rampSeconds:R}    {dv}/{rampSeconds:R}");
        sb.AppendLine("R_load      50");
        if (extra is not null) sb.Append(extra);
        sb.AppendLine("[End]");
        return sb.ToString();
    }

    private static IbisModel Model(string text) => new IbisParser().Parse(text).Model("BUF");

    /// <summary>A line so short it is a node: the fixture hangs directly on the pad.</summary>
    private static MtlNetwork Stub()
    {
        double c = 1.0 / (50.0 * C0), l = 50.0 / C0;
        var rlgc = new RlgcResult(1, new[,] { { c } }, new[,] { { 0.0 } }, new[,] { { c } },
            new[,] { { l } }, new[] { 0.0 }, new[] { 0.0 }, Array.Empty<string>());
        return new MtlNetwork(new[] { new MtlSection(rlgc, 1e-4) });
    }

    private static double CrossingTime(double[] wave, double dt, double level, int from)
    {
        for (int n = from; n + 1 < wave.Length; n++)
            if (wave[n] < level && wave[n + 1] >= level)
                return (n + (level - wave[n]) / (wave[n + 1] - wave[n])) * dt;
        throw new InvalidOperationException($"the waveform never rises through {level}");
    }

    [Fact]
    public void ARampOnlyModel_IntoItsOwnFixture_ReproducesTheStatedSlew()
    {
        // The plan's gate. The file says dV/dt_r = 1.32 V / 0.5 ns, where 1.32 V is 60 % of the
        // 2.2 V the 25 Ω buffer swings into the ramp's 50 Ω load. Driven into that load, the
        // pad must cross from 20 % to 80 % of its swing at that slew. With the edge taken as
        // Vcc / slew the edge was 1.25 ns instead of 0.833 ns: 1.5× slow.
        const double ramp = 0.5e-9, ui = 4e-9;
        const int spui = 96;
        double dt = ui / spui;
        var model = Model(Ibs(ramp));
        var bits = new[] { false, false, true, true };
        var driver = IbisDriver.FromBits(model, IbisCornerSelection.Typ, bits, spui, dt,
            out string source, out var warnings);
        Assert.Contains("[Ramp]", source);
        Assert.Empty(warnings);

        var solved = NonlinearLink.SolveNPort(Stub(), new INonlinearDriver[] { driver },
            new INonlinearDriver[] { new LinearLoadElement(50) }, bits.Length * spui, dt);
        var pad = solved.NearVolts[0];
        double swing = Vcc * 50 / (50 + OutputOhms);
        Assert.Equal(swing, pad.Max(), 2);
        Assert.Equal(0.0, pad.Min(), 2);

        int edgeStart = 2 * spui - 2;
        double t20 = CrossingTime(pad, dt, 0.2 * swing, edgeStart);
        double t80 = CrossingTime(pad, dt, 0.8 * swing, edgeStart);
        double slew = 0.6 * swing / (t80 - t20);
        double stated = 0.6 * swing / ramp;
        _output.WriteLine($"20–80 % in {(t80 - t20) * 1e9:g4} ns: {slew / 1e9:g4} V/ns, stated {stated / 1e9:g4} V/ns");
        Assert.InRange(slew / stated, 0.97, 1.03);
    }

    [Fact]
    public void AnEdgeLongerThanTheBit_IsNotSpedUp_AndIsReported()
    {
        // A ramp whose whole-swing time is three unit intervals. It used to be clamped to one
        // UI — the driver three times faster than its model, with nothing said.
        const int spui = 20;
        const double ui = 1e-9, dt = ui / spui;
        var model = Model(Ibs(rampSeconds: 0.6 * 3 * ui));
        var bits = new[] { true, false, false, false, false, false, false, false, false, false };
        var driver = IbisDriver.FromBits(model, IbisCornerSelection.Typ, bits, spui, dt,
            out _, out var warnings);
        var ku = driver.PullupSchedule;

        Assert.Contains(warnings, w => w.Contains("longer than the 1 ns unit interval"));
        // After the one-bit high: a third of the way up, not at the rail.
        Assert.Equal(1.0 / 3, ku[spui - 1], 9);
        // The falling edge takes over from THERE, at its own slew, not from the rail.
        Assert.True(ku[spui] < ku[spui - 1]);
        Assert.Equal(ku[spui - 1] - ku[spui], 1.0 / (3 * spui), 9);
        Assert.Equal(0.0, ku[2 * spui + 2], 9);
        // And the pull-down is the complement throughout.
        var kd = driver.PulldownSchedule;
        for (int n = 0; n < ku.Count; n++) Assert.Equal(1.0, ku[n] + kd[n], 12);
    }

    [Fact]
    public void AnEdgeThatFitsItsBit_IsTheSameScheduleAsBefore()
    {
        // 0.5 ns ramp → 0.833 ns whole swing at 16 samples per 1 ns: 13 samples of ramp, then
        // settled, every transition starting from the rail.
        const int spui = 16;
        const double dt = 62.5e-12;
        var driver = IbisDriver.FromBits(Model(Ibs(0.5e-9)), IbisCornerSelection.Typ,
            new[] { true, false, false, true }, spui, dt, out _, out var warnings);
        Assert.Empty(warnings);
        var ku = driver.PullupSchedule;
        const int edge = 13;
        // Bit 0 follows bit 3 (both high): held. Bit 1 falls, bit 2 holds low, bit 3 rises.
        for (int s = 0; s < spui; s++)
        {
            Assert.Equal(1.0, ku[s], 12);
            Assert.Equal(s < edge ? 1 - (s + 1.0) / edge : 0.0, ku[spui + s], 12);
            Assert.Equal(0.0, ku[2 * spui + s], 12);
            Assert.Equal(s < edge ? (s + 1.0) / edge : 1.0, ku[3 * spui + s], 12);
        }
    }

    [Fact]
    public void AMeasuredEdgeLongerThanTheBit_RunsOnPastTheBoundary()
    {
        // The audit's case: a V-T table with 0.4 ns of lead-in and a 1 ns edge, run at 1 Gb/s.
        // At the end of the first unit interval the buffer is 60 % switched; it used to be
        // frozen there for as long as the bit was held.
        const int spui = 20, rows = 41;
        const double ui = 1e-9, dt = ui / spui, tableStep = 50e-12;
        double g = 1 / OutputOhms;
        var extra = new StringBuilder();
        foreach (bool rising in new[] { true, false })
        {
            extra.AppendLine(rising ? "[Rising Waveform]" : "[Falling Waveform]");
            extra.AppendLine("R_fixture   50");
            extra.AppendLine("V_fixture   0");
            for (int k = 0; k < rows; k++)
            {
                double t = k * tableStep;
                double progress = Math.Clamp((t - 0.4e-9) / 1e-9, 0, 1);
                double kuRow = rising ? progress : 1 - progress;
                // Ku·g·(V − Vcc) + (1 − Ku)·g·V + V/50 = 0.
                double v = kuRow * g * Vcc / (g + 1 / 50.0);
                extra.AppendLine($"   {t:R}   {v:R}   {v:R}   {v:R}");
            }
        }
        var model = Model(Ibs(0.5e-9, extra: extra.ToString()));
        var bits = new[] { true, true, true, false, false, false };
        var driver = IbisDriver.FromBits(model, IbisCornerSelection.Typ, bits, spui, dt,
            out string source, out var warnings);
        Assert.Contains("waveform extraction", source);
        Assert.Contains(warnings, w => w.Contains("rising edge takes") && w.Contains("longer than"));

        // The rising edge starts at bit 0 (the pattern wraps from low).
        var ku = driver.PullupSchedule;
        Assert.Equal(0.0, ku[spui * 4 / 10 - 1], 6);          // still in the lead-in at 0.35 ns
        Assert.Equal(0.6, ku[spui], 6);                        // 1.0 ns: 60 % of the way
        Assert.Equal(1.0, ku[spui + 9], 6);                    // 1.45 ns: settled
        Assert.Equal(1.0, ku[3 * spui - 1], 6);                // and held until the fall
        Assert.Equal(0.4, ku[4 * spui], 6);                    // the fall, 1.0 ns after it began
    }

    [Fact]
    public void DuplicateVoltageRows_AreNamedAndDoNotBreakTheTable()
    {
        string text = Ibs(0.5e-9).Replace("[Pulldown]\n", "[Pulldown]\n   0   0   0   0\n")
                                 .Replace("[Pulldown]\r\n", "[Pulldown]\r\n   0   0   0   0\r\n");
        var file = new IbisParser().Parse(text);
        Assert.Contains(file.Warnings, w => w.Contains("second row at 0 V"));

        var table = PwlTable.FromTable(new[]
        {
            new IbisIvRow(0.0, Corner(0.0)), new IbisIvRow(1.0, Corner(0.01)),
            new IbisIvRow(1.0, Corner(0.02)), new IbisIvRow(2.0, Corner(0.03))
        }, IbisCornerSelection.Typ);
        foreach (double v in new[] { -0.5, 0.5, 1.0, 1.5, 2.5 })
        {
            var (i, slope) = table.Eval(v);
            Assert.True(double.IsFinite(i) && double.IsFinite(slope), $"at {v} V: I = {i}, G = {slope}");
        }
        Assert.Equal(0.01, table.Eval(1.0).I, 12);            // the first row at 1 V is the one kept
    }

    private static IbisCorner Corner(double value) => new(value, value, value);

    [Fact]
    public void AnOpenDrainModel_Drives_AgainstAnExternalPullUp()
    {
        // [Pulldown] only. It was refused as "not an output buffer" although its Model_type
        // says it drives. Low: the 25 Ω device against a 50 Ω pull-up to 3.3 V; high: released.
        var model = Model(Ibs(0.5e-9, type: "Open_drain", pullup: false));
        Assert.True(model.IsOutput);
        Assert.True(model.IsOpenStage);

        const int spui = 64;
        const double dt = 4e-9 / spui;
        var bits = new[] { true, true, false, false };
        var driver = IbisDriver.FromBits(model, IbisCornerSelection.Typ, bits, spui, dt);
        var solved = NonlinearLink.SolveNPort(Stub(), new INonlinearDriver[] { driver },
            new INonlinearDriver[] { new LinearTheveninDriver(_ => Vcc, 50) }, bits.Length * spui, dt);
        var pad = solved.NearVolts[0];
        Assert.InRange(pad[2 * spui - 1] / Vcc, 0.99, 1.01);
        Assert.InRange(pad[4 * spui - 1] / (Vcc * OutputOhms / (OutputOhms + 50)), 0.99, 1.01);

        // A push-pull type with one table missing is still not an output.
        Assert.False(Model(Ibs(0.5e-9, pullup: false)).IsOutput);
    }

    // ------------------------------------------------------------------
    // Port capacitance
    // ------------------------------------------------------------------

    private static double[] Trapezoid(bool[] bits, int spui, int edge)
    {
        var v = new double[bits.Length * spui];
        for (int b = 0; b < bits.Length; b++)
        {
            int target = bits[b] ? 1 : 0, prev = bits[(b - 1 + bits.Length) % bits.Length] ? 1 : 0;
            for (int s = 0; s < spui; s++)
                v[b * spui + s] = prev != target && s < edge
                    ? prev + (target - prev) * (s + 1.0) / edge : target;
        }
        return v;
    }

    private static double Sample(double[] wave, double t, double dt) =>
        wave[Math.Clamp((int)Math.Round(t / dt), 0, wave.Length - 1)];

    private sealed class CappedThevenin : INonlinearDriver
    {
        private readonly LinearTheveninDriver _inner;
        public CappedThevenin(Func<double, double> source, double ohms, double farads)
        {
            _inner = new LinearTheveninDriver(source, ohms);
            CompCapacitanceFarads = farads;
        }
        public double CompCapacitanceFarads { get; }
        public (double Current, double Conductance) Evaluate(double v, double t) => _inner.Evaluate(v, t);
    }

    [Fact]
    public void ALinearLinkWithALoadCapacitor_MatchesTheExactLinearSolver()
    {
        // The audit's gate for the capacitor: 1 Gb/s at 32 samples per UI, a 5 pF load on a
        // 50 Ω line (time constant 125 ps against a 31 ps step). The linear engine carries
        // the capacitor exactly; the N-port engine (trapezoidal capacitor inside its channel
        // reduction) must agree within 1 % of the swing. Measured 2.5 mV of 1 V.
        const int spui = 32;
        const double dt = 1e-9 / spui, rs = 50, rl = 1e6, cl = 5e-12;
        var bits = new[] { true, false, true, true, false, false, true, false };
        double c = 1.0 / (50.0 * C0), l = 50.0 / C0;
        var rlgc = new RlgcResult(1, new[,] { { c } }, new[,] { { 0.0 } }, new[,] { { c } },
            new[,] { { l } }, new[] { 0.0 }, new[] { 0.0 }, Array.Empty<string>());
        // Six samples of delay exactly: the lossless line is then exact on the sampling grid,
        // and what is left is the capacitor.
        var network = new MtlNetwork(new[] { new MtlSection(rlgc, C0 * 6 * dt) });
        var source = Trapezoid(bits, spui, 6);

        var linear = TransientLink.SolvePeriodic(network,
            new[] { new LineTermination(rs, rl, cl) }, new[] { (double[]?)source }, dt);
        var nport = NonlinearLink.SolveNPort(network,
            new INonlinearDriver[] { new LinearTheveninDriver(t => Sample(source, t, dt), rs) },
            new INonlinearDriver[] { new LinearLoadElement(rl, cl) }, source.Length, dt);

        double worst = 0;
        for (int n = 0; n < source.Length; n++)
            worst = Math.Max(worst, Math.Abs(linear.FarVoltages[0][n] - nport.FarVolts[0][n]));
        _output.WriteLine($"worst far-end difference {worst * 1e3:g3} mV of a 1 V swing");
        Assert.True(worst < 0.01, $"far end differs by {worst:g3} V");
    }

    [Fact]
    public void TheSingleLineEngine_AgreesWithTheNPortEngine_OnADriverCapacitance()
    {
        // The single-line engine steps the driver's C_comp itself (trapezoidal); the N-port
        // engine has it in the channel. Same link, 4 pF at the driver.
        const int spui = 32;
        const double dt = 1e-9 / spui, rs = 40, rl = 60, cDriver = 4e-12;
        var bits = new[] { true, false, true, true, false, false, true, false };
        double c = 1.0 / (50.0 * C0), l = 50.0 / C0;
        var rlgc = new RlgcResult(1, new[,] { { c } }, new[,] { { 0.0 } }, new[,] { { c } },
            new[,] { { l } }, new[] { 0.0 }, new[] { 0.0 }, Array.Empty<string>());
        // Six samples of delay exactly: the lossless line is then exact on the sampling grid,
        // and what is left is the capacitor.
        var network = new MtlNetwork(new[] { new MtlSection(rlgc, C0 * 6 * dt) });
        var source = Trapezoid(bits, spui, 6);
        INonlinearDriver Driver() => new CappedThevenin(t => Sample(source, t, dt), rs, cDriver);

        var single = NonlinearLink.Solve(network, Driver(), new NonlinearReceiver(rl), bits, spui, dt);
        var nport = NonlinearLink.SolveNPort(network, new[] { Driver() },
            new INonlinearDriver[] { new LinearLoadElement(rl) }, source.Length, dt);

        double worst = 0;
        for (int n = 0; n < source.Length; n++)
            worst = Math.Max(worst, Math.Abs(single.ReceiverVolts[n] - nport.FarVolts[0][n]));
        _output.WriteLine($"worst far-end difference {worst * 1e3:g3} mV");
        Assert.True(worst < 0.01, $"the two engines differ by {worst:g3} V at the receiver");
    }
}
