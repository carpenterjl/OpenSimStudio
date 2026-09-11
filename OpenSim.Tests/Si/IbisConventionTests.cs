using System.Text;
using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Ibis;

namespace OpenSim.Tests.Si;

/// <summary>
/// Tier 0 / WI-2 (defect D3) — the IBIS voltage-axis convention. The spec tabulates [Pullup]
/// and [POWER Clamp] "Vcc relative", <c>Vtable = Vcc − Voutput</c>; [Pulldown] and [GND Clamp]
/// against <c>Voutput − Vref</c>; and an ECL model's [Pulldown] Vcc relative as well. Currents
/// are positive INTO the component. The engine read every table at <c>V − rail</c>, which
/// mirrors the supply-referenced characteristics: a pull-up that should source current into a
/// grounded pad sank it instead. These gates are closed-form load lines, so a wrong axis or a
/// wrong chain-rule sign is a wrong number, not a tolerance question.
/// </summary>
public class IbisConventionTests
{
    private static IbisModel Parse(string text, string name) =>
        new IbisParser().Parse(text).Model(name);

    /// <summary>A driver held at a constant switching state: all-ones bits sit at Ku = 1,
    /// all-zeros at Kd = 1, so Evaluate reads one output stage plus the clamps.</summary>
    private static IbisDriver Held(IbisModel model, bool high, IbisCornerSelection corner = IbisCornerSelection.Typ) =>
        IbisDriver.FromBits(model, corner, Enumerable.Repeat(high, 4).ToList(), 8, 1e-11);

    // ------------------------------------------------------------------
    // Pull-up: the spec's own example — a 100 Ω pull-up to 3.3 V sources 33 mA into a
    // grounded pad. Table row (V_table = 3.3, I = −33 mA): current INTO the pad is negative
    // when the pull-up is sourcing.
    // ------------------------------------------------------------------

    private const string Resistive = @"
[IBIS Ver]      2.1
[File Name]     resistive.ibs
[Model]         R100
Model_type      Output
C_comp          0.0
[Voltage Range] 3.3      3.3      3.3
[Pullup]                | Vtable = Vcc - Voutput; 100 ohm to Vcc: I = -Vtable/100
  -3.3        33.0m       33.0m       33.0m
   0.0        0.0         0.0         0.0
   3.3       -33.0m      -33.0m      -33.0m
   6.6       -66.0m      -66.0m      -66.0m
[Pulldown]              | Vtable = Voutput; 100 ohm to ground: I = Vtable/100
  -3.3       -33.0m      -33.0m      -33.0m
   0.0        0.0         0.0         0.0
   3.3        33.0m       33.0m       33.0m
   6.6        66.0m       66.0m       66.0m
[End]
";

    [Fact]
    public void PullupSourcesIntoAGroundedPad_OnTheSpecAxis()
    {
        var driver = Held(Parse(Resistive, "R100"), high: true);
        var (current, conductance) = driver.Evaluate(0.0, 0);
        // Into-line current +33 mA; into-line conductance d(+I)/dV = −1/100 S (a Thevenin
        // source behind 100 Ω).
        Assert.Equal(+0.033, current, 12);
        Assert.Equal(-0.01, conductance, 12);

        // And at the rail the pull-up is off; halfway it sources half.
        Assert.Equal(0.0, driver.Evaluate(3.3, 0).Current, 12);
        Assert.Equal(+0.0165, driver.Evaluate(1.65, 0).Current, 12);
    }

    [Fact]
    public void PulldownSinksFromARaisedPad_OnTheSpecAxis()
    {
        var driver = Held(Parse(Resistive, "R100"), high: false);
        var (current, conductance) = driver.Evaluate(3.3, 0);
        Assert.Equal(-0.033, current, 12);
        Assert.Equal(-0.01, conductance, 12);
        Assert.Equal(0.0, driver.Evaluate(0.0, 0).Current, 12);
    }

    // ------------------------------------------------------------------
    // POWER clamp: conducts when the pad is ABOVE the rail, i.e. at NEGATIVE V_table.
    // ------------------------------------------------------------------

    private const string Clamped = @"
[IBIS Ver]      2.1
[File Name]     clamped.ibs
[Model]         CLAMP
Model_type      I/O
C_comp          0.0
[Voltage Range] 3.3      3.3      3.3
[Pullup]
   0.0        0.0         0.0         0.0
   3.3        0.0         0.0         0.0
[Pulldown]
   0.0        0.0         0.0         0.0
   3.3        0.0         0.0         0.0
[GND Clamp]             | conducts below ground: negative current at negative V
  -1.0      -300.0m     -300.0m     -300.0m
  -0.5        0.0         0.0         0.0
   0.0        0.0         0.0         0.0
[POWER Clamp]           | Vtable = Vcc - Voutput; conducts at Vtable < -0.5 (V > Vcc + 0.5)
  -1.0       300.0m      300.0m      300.0m
  -0.5        0.0         0.0         0.0
   0.0        0.0         0.0         0.0
[End]
";

    [Fact]
    public void PowerClampConductsAbovetheRail_NotBelowIt()
    {
        var model = Parse(Clamped, "CLAMP");
        var receiver = new IbisReceiverElement(model, IbisCornerSelection.Typ);
        var driver = Held(model, high: true);

        // Pad at 4.0 V, Vcc 3.3 V: V_table = −0.7, between the −0.5 V knee and the −1.0 V row
        // → 120 mA into the pad, so −120 mA into the line; slope 0.6 S per volt of pad,
        // into-line −0.6 S.
        foreach (var element in new INonlinearDriver[] { receiver, driver })
        {
            var (current, conductance) = element.Evaluate(4.0, 0);
            Assert.Equal(-0.120, current, 12);
            Assert.Equal(-0.6, conductance, 12);
            // Inside the rails nothing conducts.
            Assert.Equal(0.0, element.Evaluate(2.0, 0).Current, 12);
            // And 0.7 V BELOW the rail (where the mirrored axis used to put the clamp) is off.
            Assert.Equal(0.0, element.Evaluate(2.6, 0).Current, 12);
        }
    }

    // ------------------------------------------------------------------
    // Conductance is the derivative of the current, on every table, for driver and receiver.
    // ------------------------------------------------------------------

    /// <summary>Piecewise-linear tables with several segments each, on the spec axes. Break-
    /// points sit on multiples of 0.5 V (in V_table), and the probe voltages avoid them.</summary>
    private const string Curved = @"
[IBIS Ver]      2.1
[File Name]     curved.ibs
[Model]         CRV
Model_type      I/O
C_comp          1.0p
[Voltage Range] 3.3      3.0      3.6
[Pullup Reference]   3.3
[Pulldown Reference] 0.0
[Pullup]
  -2.0        40.0m       35.0m       45.0m
   0.0        0.0         0.0         0.0
   1.0       -12.0m      -10.0m      -14.0m
   2.0       -20.0m      -17.0m      -23.0m
   3.5       -26.0m      -22.0m      -30.0m
   5.0       -27.0m      -23.0m      -31.0m
[Pulldown]
  -2.0       -40.0m      -35.0m      -45.0m
   0.0        0.0         0.0         0.0
   1.0        11.0m       9.0m        13.0m
   2.0        18.0m       15.0m       21.0m
   3.5        23.0m       19.0m       27.0m
   5.0        24.0m       20.0m       28.0m
[GND Clamp]
  -2.0      -900.0m     -800.0m    -1000.0m
  -1.0      -200.0m     -150.0m     -250.0m
  -0.5        0.0         0.0         0.0
   5.0        0.0         0.0         0.0
[POWER Clamp]
  -2.0       900.0m      800.0m     1000.0m
  -1.0       200.0m      150.0m      250.0m
  -0.5        0.0         0.0         0.0
   5.0        0.0         0.0         0.0
[End]
";

    private static readonly double[] ProbeVolts =
        { -0.9, -0.4, 0.1, 0.6, 1.1, 1.6, 2.1, 2.6, 3.1, 4.1 };

    private static void AssertConductanceIsTheDerivative(INonlinearDriver element, string what)
    {
        const double h = 1e-7;
        foreach (double v in ProbeVolts)
        {
            double fd = (element.Evaluate(v + h, 0).Current - element.Evaluate(v - h, 0).Current) / (2 * h);
            double g = element.Evaluate(v, 0).Conductance;
            Assert.True(Math.Abs(fd - g) < 1e-6 * (1 + Math.Abs(g)),
                $"{what} at {v} V: conductance {g:R} vs finite difference {fd:R}");
        }
    }

    [Fact]
    public void Conductance_IsTheDerivativeOfCurrent_ForDriverAndReceiver()
    {
        var model = Parse(Curved, "CRV");
        foreach (var corner in new[] { IbisCornerSelection.Typ, IbisCornerSelection.Min, IbisCornerSelection.Max })
        {
            AssertConductanceIsTheDerivative(Held(model, high: true, corner), $"driver high {corner}");
            AssertConductanceIsTheDerivative(Held(model, high: false, corner), $"driver low {corner}");
            AssertConductanceIsTheDerivative(new IbisReceiverElement(model, corner, 75), $"receiver {corner}");
        }
    }

    // ------------------------------------------------------------------
    // ECL: the [Pulldown] table is Vcc relative too, and its reference defaults to the
    // pull-up rail (owner decision Q9, 2026-09-10).
    // ------------------------------------------------------------------

    /// <summary>The same 50 Ω "to the reference" pull-down table, once in an ECL model (read
    /// at V_ref − V_out, V_ref defaulting to the 5.0 V supply) and once in a plain Output model
    /// whose [Pulldown Reference] is set to 5.0 V (read at V_out − V_ref). The two readings sit
    /// at opposite ends of the table, so the sign of the answer tells them apart.</summary>
    private static string EclPair(string pulldownReferenceLine) => $@"
[IBIS Ver]      2.1
[File Name]     ecl.ibs
[Model]         ECL
Model_type      Output_ECL
C_comp          0.0
[Voltage Range] 5.0      5.0      5.0
{pulldownReferenceLine}
[Pullup]                | Vtable = Vcc - Voutput
   0.0        0.0         0.0         0.0
   3.0       -60.0m      -60.0m      -60.0m
[Pulldown]              | ECL: Vtable = Vcc - Voutput as well; 50 ohm: I = -Vtable/50
  -3.0        60.0m       60.0m       60.0m
   0.0        0.0         0.0         0.0
   3.0       -60.0m      -60.0m      -60.0m
[Model]         CMOS
Model_type      Output
C_comp          0.0
[Voltage Range] 5.0      5.0      5.0
[Pulldown Reference] 5.0
[Pullup]
   0.0        0.0         0.0         0.0
   3.0       -60.0m      -60.0m      -60.0m
[Pulldown]              | non-ECL: Vtable = Voutput - Vref
  -3.0        60.0m       60.0m       60.0m
   0.0        0.0         0.0         0.0
   3.0       -60.0m      -60.0m      -60.0m
[End]
";

    [Fact]
    public void EclPulldown_IsReadVccRelative_AndANonEclModelIsNot()
    {
        var file = new IbisParser().Parse(EclPair(""));
        var ecl = Held(file.Model("ECL"), high: false);
        var cmos = Held(file.Model("CMOS"), high: false);

        // Pad at 3.0 V. ECL: V_table = 5 − 3 = 2 → −40 mA into the pad → +40 mA into the line,
        // conductance: table slope −0.02 S/V, chain-rule −1 → +0.02 into pad, −0.02 into line.
        var (iEcl, gEcl) = ecl.Evaluate(3.0, 0);
        Assert.Equal(+0.040, iEcl, 12);
        Assert.Equal(-0.02, gEcl, 12);

        // Non-ECL with the same table and reference: V_table = 3 − 5 = −2 → +40 mA into the
        // pad → −40 mA into the line; slope −0.02 with chain-rule +1 → +0.02 into line.
        var (iCmos, gCmos) = cmos.Evaluate(3.0, 0);
        Assert.Equal(-0.040, iCmos, 12);
        Assert.Equal(+0.02, gCmos, 12);
    }

    [Fact]
    public void EclPulldownReference_DefaultsToThePullupRail_AndAnExplicitOneOverridesIt()
    {
        var bare = new IbisParser().Parse(EclPair("")).Model("ECL");
        Assert.Equal(5.0, bare.PulldownRailAt(IbisCornerSelection.Typ), 12);

        // An explicit [Pulldown Reference] wins, at every corner: pad at 3.0 V, V_table =
        // V_ref − 3.0 → −(V_ref − 3.0)/50 into the pad → +(V_ref − 3.0)/50 into the line:
        // 30 mA at the 4.5 V Typ reference, 26 mA at 4.3 V Min, 34 mA at 4.7 V Max.
        var referenced = new IbisParser().Parse(EclPair("[Pulldown Reference] 4.5 4.3 4.7")).Model("ECL");
        Assert.Equal(4.5, referenced.PulldownRailAt(IbisCornerSelection.Typ), 12);
        Assert.Equal(4.3, referenced.PulldownRailAt(IbisCornerSelection.Min), 12);
        Assert.Equal(4.7, referenced.PulldownRailAt(IbisCornerSelection.Max), 12);
        Assert.Equal(+0.030, Held(referenced, high: false, IbisCornerSelection.Typ).Evaluate(3.0, 0).Current, 12);
        Assert.Equal(+0.026, Held(referenced, high: false, IbisCornerSelection.Min).Evaluate(3.0, 0).Current, 12);
        Assert.Equal(+0.034, Held(referenced, high: false, IbisCornerSelection.Max).Evaluate(3.0, 0).Current, 12);
    }

    [Fact]
    public void EclDriver_ConductanceIsTheDerivative()
    {
        var model = new IbisParser().Parse(EclPair("")).Model("ECL");
        AssertConductanceIsTheDerivative(Held(model, high: false), "ECL driver low");
        AssertConductanceIsTheDerivative(Held(model, high: true), "ECL driver high");
    }

    // ------------------------------------------------------------------
    // ECL through the extractor: the inverse identity on the ECL node equation.
    // ------------------------------------------------------------------

    private const double EclVcc = 5.0;
    private const double EclGpu = 20.0e-3;     // pull-up: I_pu = −g_pu·V_table
    private const double EclGpd = 8.0e-3;      // pull-down: I_pd = −g_pd·V_table + I0
    private const double EclI0 = 25.0e-3;      // the offset keeps the two columns independent

    /// <summary>Pad voltage of the linear ECL buffer into a fixture, from the node equation
    /// with BOTH stages Vcc relative: Ku·(−g_pu)(Vcc−V) + Kd·(−g_pd(Vcc−V) + I0) + (V−V_f)/R_f = 0.</summary>
    private static double EclPadVolts(double ku, double kd, double rf, double vf) =>
        (EclVcc * (ku * EclGpu + kd * EclGpd) - kd * EclI0 + vf / rf)
        / (ku * EclGpu + kd * EclGpd + 1 / rf);

    private static (double Ku, double Kd) EclSchedule(int n, int count)
    {
        double x = (double)n / (count - 1);
        double ku = 0.5 - 0.5 * Math.Cos(Math.PI * x);
        return (ku, 0.2 + 0.6 * (1 - ku));        // deliberately NOT complementary
    }

    private static string EclWaveformIbs(int count, double dt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[IBIS Ver]      2.1");
        sb.AppendLine("[File Name]     eclsynth.ibs");
        sb.AppendLine("[Model]         ECLGEN");
        sb.AppendLine("Model_type      Output_ECL");
        sb.AppendLine("C_comp          0.0");
        sb.AppendLine($"[Voltage Range] {EclVcc:R} {EclVcc:R} {EclVcc:R}");
        sb.AppendLine("[Pullup]");
        for (int k = -1; k <= 3; k++)
        {
            double vt = k * EclVcc / 2;
            sb.AppendLine($"   {vt:R}   {-EclGpu * vt:R}   {-EclGpu * vt:R}   {-EclGpu * vt:R}");
        }
        sb.AppendLine("[Pulldown]");
        for (int k = -1; k <= 3; k++)
        {
            double vt = k * EclVcc / 2, i = -EclGpd * vt + EclI0;
            sb.AppendLine($"   {vt:R}   {i:R}   {i:R}   {i:R}");
        }
        foreach (var (r, vf) in new[] { (50.0, 0.0), (50.0, EclVcc) })
        {
            sb.AppendLine("[Rising Waveform]");
            sb.AppendLine($"R_fixture   {r:R}");
            sb.AppendLine($"V_fixture   {vf:R}");
            for (int n = 0; n < count; n++)
            {
                var (ku, kd) = EclSchedule(n, count);
                double v = EclPadVolts(ku, kd, r, vf);
                sb.AppendLine($"   {n * dt:R}   {v:R}   {v:R}   {v:R}");
            }
        }
        sb.AppendLine("[End]");
        return sb.ToString();
    }

    [Fact]
    public void EclExtraction_RecoversTheGeneratingScheduleExactly()
    {
        // Waveforms synthesised from the ECL node equation (pull-down Vcc relative) must give
        // back their schedule. If the extractor read the ECL pull-down on the ground axis the
        // recovered coefficients would be wrong by the whole mirrored characteristic.
        const int count = 21;
        const double dt = 50e-12;
        var model = new IbisParser().Parse(EclWaveformIbs(count, dt)).Model("ECLGEN");
        Assert.True(model.IsEcl);

        var schedule = KuKdExtractor.Extract(model, IbisCornerSelection.Typ,
            model.RisingWaveforms, rising: true, dt, count);
        for (int n = 0; n < count; n++)
        {
            var (ku, kd) = EclSchedule(n, count);
            Assert.True(Math.Abs(schedule.Ku[n] - ku) < 1e-9, $"Ku[{n}] {schedule.Ku[n]:R} vs {ku:R}");
            Assert.True(Math.Abs(schedule.Kd[n] - kd) < 1e-9, $"Kd[{n}] {schedule.Kd[n]:R} vs {kd:R}");
        }
    }

    // ------------------------------------------------------------------
    // The flag itself.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Output_ECL", true)]
    [InlineData("I/O_ECL", true)]
    [InlineData("3-state_ECL", true)]
    [InlineData("Input_ECL", true)]
    [InlineData("output_ecl", true)]
    [InlineData("Output", false)]
    [InlineData("I/O", false)]
    [InlineData("3-state", false)]
    [InlineData("Terminator", false)]
    [InlineData("", false)]
    public void IsEcl_FollowsModelType(string type, bool ecl)
    {
        Assert.Equal(ecl, new IbisModel { Name = "X", ModelType = type }.IsEcl);
    }

    [Fact]
    public void AParsedEclModel_ReportsIsEcl_AndMayDrive()
    {
        var file = new IbisParser().Parse(EclPair(""));
        Assert.True(file.Model("ECL").IsEcl);
        Assert.True(file.Model("ECL").TypeIsDriverCapable);
        Assert.False(file.Model("CMOS").IsEcl);
    }
}
