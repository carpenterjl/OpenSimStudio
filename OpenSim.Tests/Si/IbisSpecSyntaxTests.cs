using System.Text;
using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Ibis;

namespace OpenSim.Tests.Si;

/// <summary>
/// Tier 0 / WI-3 (defect D4) — the parser against the spec's own syntax. Three things were
/// wrong: <c>R_fixture = 50</c> (the form the spec's examples use) threw; a [Submodel]'s tables
/// were appended to the preceding [Model]; and the four reference keywords kept only their
/// first token, so a Min run of a 3.3 / 3.0 / 3.6 V part drove at 3.3 V. The gates here parse
/// spec-syntax text, check that nothing leaks between models, and push the reference triples
/// through the consumers where a linear buffer makes the corner answer closed-form.
/// </summary>
public class IbisSpecSyntaxTests
{
    private static IbisFile Parse(string text) => new IbisParser().Parse(text);

    // ------------------------------------------------------------------
    // Spec syntax + a [Submodel] definition between two models.
    // ------------------------------------------------------------------

    private const string SpecSyntax = @"
[IBIS Ver]      4.2
[File Name]     spec.ibs        | a trailing comment
[Component]     ACME_SPEC
[Model]         FIRST
Model_type      Output
C_comp          2.0pF    1.5pF    2.5pF
[Voltage Range] 3.3V     3.0V     3.6V
[Pullup Reference]      3.3V     3.0V     3.6V
[Pulldown Reference]    0V       0V       0V
[GND Clamp Reference]   0V      -0.1V     0.1V
[POWER Clamp Reference] 3.3V     3.0V     3.6V
[Pulldown]
|  Voltage    I(typ)      I(min)      I(max)
   0.0V       0.0mA       0.0mA       0.0mA
   3.3V       33.0mA      30.0mA      36.0mA
[Pullup]
   0.0V       0.0mA       0.0mA       0.0mA
   3.3V      -33.0mA     -30.0mA     -36.0mA
[Ramp]
dV/dt_r = 1.65/0.5n    1.50/0.6n    1.80/0.4n
dV/dt_f   1.65/0.5n    1.50/0.6n    1.80/0.4n
R_load = 50ohms
[Rising Waveform]
R_fixture = 50
V_fixture = 0.0
   0.0        0.0         0.0         0.0
   0.5n       1.65        1.50        1.80
   1.0n       3.3         3.0         3.6
[Falling Waveform]
R_fixture=50
V_fixture = 3.3
V_fixture_min = 3.0
V_fixture_max = 3.6
   0.0        3.3         3.0         3.6
   0.5n       1.65        1.50        1.80
   1.0n       0.0         0.0         0.0
|
[Submodel]      Dyn_clamp
Submodel_type   Dynamic_clamp
[Submodel Spec]
| subparameter   typ   min   max
V_trigger_r      1.0   0.9   1.1
[GND Clamp]
  -1.0V       -1.0A       -1.0A       -1.0A
   0.0V        0.0A        0.0A        0.0A
[POWER Clamp]
  -1.0V        1.0A        1.0A        1.0A
   0.0V        0.0A        0.0A        0.0A
[Pullup]
   0.0V        0.0mA       0.0mA       0.0mA
   1.0V       -9.0mA      -9.0mA      -9.0mA
[Rising Waveform]
R_fixture = 50
V_fixture = 0.0
   0.0        0.0         0.0         0.0
   1.0n       1.0         1.0         1.0
|
[Model]         SECOND
Model_type      I/O
C_comp          1.0pF    1.0pF    1.0pF
[Voltage Range] 1.8V     1.7V     1.9V
[Pulldown]
   0.0V       0.0mA       0.0mA       0.0mA
   1.8V       18.0mA      18.0mA      18.0mA
[Pullup]
   0.0V       0.0mA       0.0mA       0.0mA
   1.8V      -18.0mA     -18.0mA     -18.0mA
[Rising Waveform]
R_fixture 50
V_fixture 0.0
   0.0        0.0         0.0         0.0
   1.0n       1.8         1.8         1.8
[End]
";

    [Fact]
    public void SpecSyntax_ParsesEverySubParameterForm()
    {
        var file = Parse(SpecSyntax);
        var first = file.Model("FIRST");

        // "R_fixture = 50", "R_fixture=50" and "R_fixture 50" are one key and one value.
        Assert.Equal(50.0, first.RisingWaveforms[0].RFixtureOhms, 12);
        Assert.Equal(50.0, first.FallingWaveforms[0].RFixtureOhms, 12);
        Assert.Equal(50.0, file.Model("SECOND").RisingWaveforms[0].RFixtureOhms, 12);

        // V_fixture_min / V_fixture_max are the fixture's own corners.
        var falling = first.FallingWaveforms[0].VFixture;
        Assert.Equal(3.3, falling.At(IbisCornerSelection.Typ)!.Value, 12);
        Assert.Equal(3.0, falling.At(IbisCornerSelection.Min)!.Value, 12);
        Assert.Equal(3.6, falling.At(IbisCornerSelection.Max)!.Value, 12);
        // Without them every corner is V_fixture.
        Assert.Equal(0.0, first.RisingWaveforms[0].VFixture.At(IbisCornerSelection.Min)!.Value, 12);

        // The reference keywords keep their typ/min/max triple.
        Assert.Equal(3.3, first.PullupRailAt(IbisCornerSelection.Typ), 12);
        Assert.Equal(3.0, first.PullupRailAt(IbisCornerSelection.Min), 12);
        Assert.Equal(3.6, first.PullupRailAt(IbisCornerSelection.Max), 12);
        Assert.Equal(-0.1, first.GndClampRailAt(IbisCornerSelection.Min), 12);
        Assert.Equal(0.1, first.GndClampRailAt(IbisCornerSelection.Max), 12);
        Assert.Equal(3.0, first.PowerClampRailAt(IbisCornerSelection.Min), 12);

        // "dV/dt_r = …" and "R_load = 50ohms" in [Ramp]; units after the scale letter ignored.
        Assert.Equal(1.65, first.Ramp!.Rising.DeltaVolts.Typ!.Value, 12);
        Assert.Equal(0.6e-9, first.Ramp.Rising.DeltaSeconds.Min!.Value, 15);
        Assert.Equal(1.65, first.Ramp.Falling.DeltaVolts.Typ!.Value, 12);
        Assert.Equal(33.0e-3, first.Pulldown[1].CurrentAmps.Typ!.Value, 15);
        Assert.Equal(2.0e-12, first.CComp.Typ!.Value, 15);
        Assert.Equal("ACME_SPEC", file.Component);
    }

    [Fact]
    public void ASubmodelDefinition_LeaksIntoNeitherNeighbour()
    {
        var file = Parse(SpecSyntax);
        var first = file.Model("FIRST");
        var second = file.Model("SECOND");

        // FIRST: exactly its own rows — the submodel's [Pullup] / clamps did not append.
        Assert.Equal(2, first.Pullup.Count);
        Assert.Equal(2, first.Pulldown.Count);
        Assert.Empty(first.GndClamp);
        Assert.Empty(first.PowerClamp);
        Assert.Single(first.RisingWaveforms);
        Assert.Single(first.FallingWaveforms);

        // SECOND: the submodel's [Rising Waveform] was not flushed into it.
        Assert.Equal(2, second.Pullup.Count);
        Assert.Equal(2, second.Pulldown.Count);
        Assert.Empty(second.GndClamp);
        Assert.Empty(second.PowerClamp);
        Assert.Single(second.RisingWaveforms);
        Assert.Equal(1.8, second.RisingWaveforms[0].Rows[1].VoltageVolts.Typ!.Value, 12);
        Assert.Empty(second.FallingWaveforms);

        // The submodel is not a model, and it was named in a warning rather than dropped.
        Assert.Equal(2, file.Models.Count);
        Assert.Contains(file.Warnings, w => w.Contains("[Submodel]") && w.Contains("Dyn_clamp"));
    }

    // ------------------------------------------------------------------
    // [Add Submodel] inside a model must not end it.
    // ------------------------------------------------------------------

    private const string WithAddSubmodel = @"
[IBIS Ver]      4.2
[File Name]     addsub.ibs
[Model]         PARENT
Model_type      I/O
C_comp          1.0pF    1.0pF    1.0pF
[Voltage Range] 3.3V     3.0V     3.6V
[Pullup]
   0.0V       0.0mA       0.0mA       0.0mA
   3.3V      -33.0mA     -30.0mA     -36.0mA
[Add Submodel]
| Submodel_name        Mode
Bus_Hold_1             Non-Driving
Dynamic_clamp_1        All
[Pulldown]
   0.0V       0.0mA       0.0mA       0.0mA
   3.3V       33.0mA      30.0mA      36.0mA
[POWER Clamp Reference] 5.0V     4.5V     5.5V
[Rising Waveform]
R_fixture = 50
V_fixture = 0.0
   0.0        0.0         0.0         0.0
   1.0n       3.3         3.0         3.6
[End]
";

    [Fact]
    public void AddSubmodel_EndsTheSectionButNotTheModel()
    {
        var file = Parse(WithAddSubmodel);
        var parent = Assert.Single(file.Models);
        Assert.Equal("PARENT", parent.Name);

        // Everything AFTER the reference list still belongs to the parent.
        Assert.Equal(2, parent.Pullup.Count);
        Assert.Equal(2, parent.Pulldown.Count);
        Assert.Equal(5.0, parent.PowerClampRailAt(IbisCornerSelection.Typ), 12);
        Assert.Equal(4.5, parent.PowerClampRailAt(IbisCornerSelection.Min), 12);
        Assert.Single(parent.RisingWaveforms);
        Assert.True(parent.IsOutput);

        // The name/mode rows are not table rows, not sub-parameters, and warned once.
        Assert.Equal(1, file.Warnings.Count(w => w.Contains("[Add Submodel]")));
        Assert.DoesNotContain(file.Warnings, w => w.Contains("Bus_Hold_1"));
    }

    // ------------------------------------------------------------------
    // Corners through the consumers: a linear pull-up makes the answer closed-form.
    // ------------------------------------------------------------------

    private const double Vtyp = 3.3, Vmin = 3.0, Vmax = 3.6;
    private const double Gpu = 12.0e-3, Gpd = 9.0e-3;

    private static double Rail(IbisCornerSelection c) => c switch
    {
        IbisCornerSelection.Min => Vmin, IbisCornerSelection.Max => Vmax, _ => Vtyp,
    };

    private static readonly IbisCornerSelection[] Corners =
        { IbisCornerSelection.Typ, IbisCornerSelection.Min, IbisCornerSelection.Max };

    /// <summary>The exact pad voltage of the linear buffer into a fixture at a corner, from
    /// the node equation Ku·g_pu·(V − Vcc) + Kd·g_pd·V + (V − V_f)/R_f = 0.</summary>
    private static double PadVolts(double ku, double kd, double rf, double vf, double vcc) =>
        (ku * Gpu * vcc + vf / rf) / (ku * Gpu + kd * Gpd + 1 / rf);

    private static (double Ku, double Kd) Schedule(int n, int count)
    {
        double x = (double)n / (count - 1);
        double ku = 0.5 - 0.5 * Math.Cos(Math.PI * x);
        return (ku, 0.25 + 0.5 * (1 - ku));            // NOT complementary: Ku + Kd ≠ 1 mid-edge
    }

    /// <summary>A spec-syntax file whose supply, pull-up reference and second fixture all carry
    /// the 3.3 / 3.0 / 3.6 V triple. The linear tables are corner-independent in V_table, so
    /// the corner enters only through the rails and V_fixture — exactly the values that used
    /// to be truncated to Typ.</summary>
    private static string CornerIbs(int count, double dt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[IBIS Ver]      4.2");
        sb.AppendLine("[File Name]     corners.ibs");
        sb.AppendLine("[Model]         CORNERS");
        sb.AppendLine("Model_type      Output");
        sb.AppendLine("C_comp          0.0pF   0.0pF   0.0pF");
        sb.AppendLine($"[Voltage Range]     {Vtyp:R}V   {Vmin:R}V   {Vmax:R}V");
        sb.AppendLine($"[Pullup Reference]  {Vtyp:R}V   {Vmin:R}V   {Vmax:R}V");
        sb.AppendLine("[Pulldown Reference] 0V   0V   0V");
        sb.AppendLine("[Pullup]                | Vtable = Vcc - Voutput; I = -g_pu * Vtable");
        for (int k = -1; k <= 3; k++)
        {
            double vt = k * Vtyp / 2;
            sb.AppendLine($"   {vt:R}   {-Gpu * vt:R}   {-Gpu * vt:R}   {-Gpu * vt:R}");
        }
        sb.AppendLine("[Pulldown]");
        for (int k = -1; k <= 3; k++)
        {
            double v = k * Vtyp / 2;
            sb.AppendLine($"   {v:R}   {Gpd * v:R}   {Gpd * v:R}   {Gpd * v:R}");
        }
        sb.AppendLine("[Ramp]");
        sb.AppendLine("dV/dt_r = 1.65/0.5n    1.65/0.5n    1.65/0.5n");
        sb.AppendLine("dV/dt_f = 1.65/0.5n    1.65/0.5n    1.65/0.5n");
        // Fixture 1: 50 Ω to ground. Fixture 2: 50 Ω to the SUPPLY, which moves with the corner.
        sb.AppendLine("[Rising Waveform]");
        sb.AppendLine("R_fixture = 50");
        sb.AppendLine("V_fixture = 0.0");
        AppendRows(sb, count, dt, rf: 50, vf: (_, _, _) => (0.0, 0.0, 0.0));
        sb.AppendLine("[Rising Waveform]");
        sb.AppendLine("R_fixture = 50");
        sb.AppendLine($"V_fixture = {Vtyp:R}");
        sb.AppendLine($"V_fixture_min = {Vmin:R}");
        sb.AppendLine($"V_fixture_max = {Vmax:R}");
        AppendRows(sb, count, dt, rf: 50, vf: (t, mn, mx) => (t, mn, mx));
        sb.AppendLine("[End]");
        return sb.ToString();
    }

    private static void AppendRows(StringBuilder sb, int count, double dt, double rf,
        Func<double, double, double, (double Typ, double Min, double Max)> vf)
    {
        var (vfT, vfMin, vfMax) = vf(Vtyp, Vmin, Vmax);
        for (int n = 0; n < count; n++)
        {
            var (ku, kd) = Schedule(n, count);
            sb.AppendLine($"   {n * dt:R}   {PadVolts(ku, kd, rf, vfT, Vtyp):R}   "
                + $"{PadVolts(ku, kd, rf, vfMin, Vmin):R}   {PadVolts(ku, kd, rf, vfMax, Vmax):R}");
        }
    }

    [Fact]
    public void DriverCurrent_FollowsTheReferenceTriple_AtEveryCorner()
    {
        // Without waveforms the driver settles on the [Ramp] profile, whose held-high state is
        // exactly Ku = 1 — the load line is then the pull-up alone.
        var model = Parse(CornerIbs(9, 50e-12)).Model("CORNERS") with
        {
            RisingWaveforms = Array.Empty<IbisWaveform>(),
        };
        foreach (var corner in Corners)
        {
            // Held high (Ku = 1), pad grounded: the pull-up sources g_pu·Vcc(corner).
            var driver = IbisDriver.FromBits(model, corner, new[] { true, true }, 8, 1e-11);
            Assert.Equal(Gpu * Rail(corner), driver.Evaluate(0.0, 0).Current, 12);
            // And is off exactly at that corner's rail — not at the Typ rail.
            Assert.Equal(0.0, driver.Evaluate(Rail(corner), 0).Current, 12);
        }
        Assert.NotEqual(
            IbisDriver.FromBits(model, IbisCornerSelection.Min, new[] { true, true }, 8, 1e-11).Evaluate(0.0, 0).Current,
            IbisDriver.FromBits(model, IbisCornerSelection.Max, new[] { true, true }, 8, 1e-11).Evaluate(0.0, 0).Current);
    }

    [Fact]
    public void TwoFixtureExtraction_RecoversTheScheduleAtEveryCorner_InSpecSyntax()
    {
        // The end-to-end gate: a non-complementary schedule (so the one-waveform closure cannot
        // rescue a wrong fixture reading), both fixture waveforms synthesised from the node
        // equation with the CORNER's rail and V_fixture, written in spec syntax, parsed, and
        // extracted back. Reading the reference or V_fixture at Typ for a Min run would put the
        // wrong load line into every sample.
        const int count = 25;
        const double dt = 50e-12;
        var model = Parse(CornerIbs(count, dt)).Model("CORNERS");
        Assert.Equal(2, model.RisingWaveforms.Count);

        foreach (var corner in Corners)
        {
            var schedule = KuKdExtractor.Extract(model, corner, model.RisingWaveforms,
                rising: true, dt, count);
            for (int n = 0; n < count; n++)
            {
                var (ku, kd) = Schedule(n, count);
                Assert.True(Math.Abs(schedule.Ku[n] - ku) < 1e-9,
                    $"{corner} Ku[{n}] {schedule.Ku[n]:R} vs generating {ku:R}");
                Assert.True(Math.Abs(schedule.Kd[n] - kd) < 1e-9,
                    $"{corner} Kd[{n}] {schedule.Kd[n]:R} vs generating {kd:R}");
            }
            Assert.Contains("two-waveform", schedule.Source);
        }
    }

    [Fact]
    public void ASingleValuedReference_ReducesToTheScalar_AtEveryCorner()
    {
        // The back-compat pin: every existing fixture gives its references as one value, and
        // for those the triple must be that value at every corner — nothing moves.
        const string single = @"
[IBIS Ver]      2.1
[File Name]     single.ibs
[Model]         S
Model_type      Output
C_comp          0.0
[Voltage Range] 3.3      3.0      3.6
[Pullup Reference]   3.3
[Pulldown Reference] 0.0
[Pullup]
   0.0        0.0         0.0         0.0
   1.0       -1.0m       -1.0m       -1.0m
[Pulldown]
   0.0        0.0         0.0         0.0
   1.0        1.0m        1.0m        1.0m
[End]
";
        var m = Parse(single).Model("S");
        foreach (var c in Corners)
        {
            Assert.Equal(3.3, m.PullupRailAt(c), 12);
            Assert.Equal(0.0, m.PulldownRailAt(c), 12);
            Assert.Equal(3.3, m.PowerClampRailAt(c), 12);
            Assert.Equal(0.0, m.GndClampRailAt(c), 12);
        }
    }

    // ------------------------------------------------------------------
    // The spec's own example text (IBIS 4.2 keyword examples: [Model], the I-V tables,
    // [Ramp], [Rising Waveform] / [Falling Waveform]) — public standard text.
    // ------------------------------------------------------------------

    private const string SpecExample = @"
[IBIS Ver]      4.2
[File Name]     specexample.ibs
[Component]     Example
|=============================================================================
| Signals       CLK1, CLK2,...         | Optional signal list, if desired
[Model]         Clockbuffer
Model_type      I/O
Polarity        Non-Inverting
Enable          Active-High
Vinl = 0.8V                            | Input logic ""low"" DC voltage, if any
Vinh = 2.0V                            | Input logic ""high"" DC voltage, if any
Vmeas = 1.5V              | Reference voltage for timing measurements
Cref = 50pF               | Timing specification test load capacitance value
Rref = 500                | Timing specification test load resistance value
Vref = 0                  | Timing specification test load voltage
| variable      C(typ)          C(min)          C(max)
C_comp          7.0pF           5.0pF           9.0pF
| variable              typ             min             max
[Temperature Range]     27.0            -50             130.0
[Voltage Range]         5.0V            4.5V            5.5V
[Pullup Reference]      5.0V            4.5V            5.5V
[Pulldown Reference]    0V              0V              0V
[POWER Clamp Reference] 5.0V            4.5V            5.5V
[GND Clamp Reference]   0V              0V              0V
[Pulldown]
|  Voltage   I(typ)    I(min)    I(max)
|
   -5.0V    -40.0m    -34.0m    -45.0m
   -4.0V    -39.0m    -33.0m    -43.0m
|    .
|    .
    0.0V      0.0m      0.0m      0.0m
|    .
|    .
    5.0V     40.0m     34.0m     45.0m
   10.0V     45.0m     40.0m     49.0m
|
[Pullup]                               | Note: Vtable = Vcc - Voutput
|
|  Voltage   I(typ)    I(min)    I(max)
|
   -5.0V     32.0m     30.0m     35.0m
   -4.0V     31.0m     29.0m     33.0m
|    .
|    .
    0.0V      0.0m      0.0m      0.0m
|    .
|    .
    5.0V    -32.0m    -30.0m    -35.0m
   10.0V    -38.0m    -35.0m    -40.0m
|
[GND Clamp]
|
|  Voltage   I(typ)    I(min)    I(max)
|
   -5.0V  -3900.0m  -3800.0m  -4000.0m
   -0.7V    -80.0m    -75.0m    -85.0m
   -0.6V    -22.0m    -20.0m    -25.0m
   -0.5V     -2.4m     -2.0m     -2.9m
   -0.4V      0.0m      0.0m      0.0m
    5.0V      0.0m      0.0m      0.0m
|
[POWER Clamp]                          | Note: Vtable = Vcc - Voutput
|
|  Voltage   I(typ)    I(min)    I(max)
|
   -5.0V   4450.0m       NA        NA
   -0.7V     95.0m       NA        NA
   -0.6V     23.0m       NA        NA
   -0.5V      2.4m       NA        NA
   -0.4V      0.0m       NA        NA
    0.0V      0.0m       NA        NA
|
[Ramp]
| variable      typ             min             max
dV/dt_r         2.20/1.06n      1.92/1.28n      2.49/650p
dV/dt_f         2.46/1.21n      2.21/1.54n      2.70/770p
R_load = 300ohms
|
[Rising Waveform]
R_fixture = 50
V_fixture = 0.0
| C_fixture = 50p        | These are shown, but are generally not recommended
| L_fixture = 2n
| C_dut = 7p
| R_dut = 1m
| L_dut = 1n
| Time            V(typ)              V(min)              V(max)
   0.0000s       25.2100mV           15.2200mV           43.5700mV
   0.2000ns       2.3325mV           -8.5090mV           23.4150mV
   0.4000ns       0.1484V            15.9375mV            0.3944V
   0.6000ns       0.7799V             0.2673V             1.3400V
   0.8000ns       1.2960V             0.6042V             1.9490V
   1.0000ns       1.6603V             0.9256V             2.4233V
   1.2000ns       1.9460V             1.2050V             2.8130V
   1.4000ns       2.1285V             1.3725V             3.0095V
   1.6000ns       2.3415V             1.5560V             3.1265V
   1.8000ns       2.5135V             1.7015V             3.1600V
   2.0000ns       2.6460V             1.8085V             3.1695V
| ...
  10.0000ns       2.7780V             2.3600V             3.1670V
|
[Falling Waveform]
R_fixture = 50
V_fixture = 5.5
V_fixture_min = 4.5
V_fixture_max = 5.5
| Time            V(typ)              V(min)              V(max)
   0.0000s        5.0000V             4.5000V             5.5000V
   0.2000ns       4.7470V             4.4695V             4.8815V
   0.4000ns       3.9030V             4.0955V             3.5355V
   0.6000ns       2.7313V             3.4533V             1.7770V
   0.8000ns       1.8150V             2.8570V             0.8629V
   1.0000ns       1.1697V             2.3270V             0.5364V
   1.2000ns       0.7539V             1.8470V             0.4524V
   1.4000ns       0.5905V             1.5430V             0.4368V
   1.6000ns       0.4923V             1.2290V             0.4266V
   1.8000ns       0.4639V             0.9906V             0.4207V
   2.0000ns       0.4489V             0.8349V             0.4169V
| ...
  10.0000ns       0.3950V             0.4935V             0.3841V
|
[End]
";

    [Fact]
    public void TheSpecsOwnExampleModel_ParsesToADriverTheExtractorAccepts()
    {
        var file = Parse(SpecExample);
        var m = file.Model("Clockbuffer");
        Assert.Equal("I/O", m.ModelType);
        Assert.True(m.IsOutput);
        Assert.True(m.TypeIsDriverCapable);
        Assert.False(m.IsEcl);

        // Values with units and mixed scale letters.
        Assert.Equal(7.0e-12, m.CComp.Typ!.Value, 15);
        Assert.Equal(4.5, m.PullupRailAt(IbisCornerSelection.Min), 12);
        Assert.Equal(5.5, m.PowerClampRailAt(IbisCornerSelection.Max), 12);
        Assert.Equal(5, m.Pulldown.Count);
        Assert.Equal(6, m.GndClamp.Count);
        Assert.Null(m.PowerClamp[0].CurrentAmps.Min);
        Assert.Equal(4.45, m.PowerClamp[0].CurrentAmps.Typ!.Value, 12);
        Assert.Equal(650e-12, m.Ramp!.Rising.DeltaSeconds.Max!.Value, 15);
        Assert.Equal(25.21e-3, m.RisingWaveforms[0].Rows[0].VoltageVolts.Typ!.Value, 15);
        Assert.Equal(0.2e-9, m.RisingWaveforms[0].Rows[1].TimeSeconds, 15);
        Assert.Equal(4.5, m.FallingWaveforms[0].VFixture.At(IbisCornerSelection.Min)!.Value, 12);
        Assert.Equal(5.5, m.FallingWaveforms[0].VFixture.At(IbisCornerSelection.Max)!.Value, 12);

        // The engine accepts it: the extractor runs on each edge at every corner, and the
        // driver builds from it. (The commented-out reactive fixture lines are comments; the
        // informational sub-parameters are warned about, not fatal.)
        const double dt = 0.2e-9;
        foreach (var corner in new[] { IbisCornerSelection.Typ, IbisCornerSelection.Min, IbisCornerSelection.Max })
        {
            var rise = KuKdExtractor.Extract(m, corner, m.RisingWaveforms, rising: true, dt, 12);
            var fall = KuKdExtractor.Extract(m, corner, m.FallingWaveforms, rising: false, dt, 12);
            Assert.Equal(12, rise.Ku.Length);
            Assert.Equal(12, fall.Kd.Length);
            Assert.All(rise.Ku.Concat(rise.Kd).Concat(fall.Ku).Concat(fall.Kd), k => Assert.False(double.IsNaN(k)));
        }
        var driver = IbisDriver.FromBits(m, IbisCornerSelection.Typ, new[] { true, false }, 16, dt / 4,
            out string source, out _);
        Assert.Contains("waveform extraction", source);
        Assert.Contains(file.Warnings, w => w.Contains("Vinl"));
        Assert.Contains(file.Warnings, w => w.Contains("R_load"));
        Assert.Contains(file.Warnings, w => w.Contains("[Temperature Range]"));
        Assert.DoesNotContain(file.Warnings, w => w.Contains("C_fixture"));
    }

    [Fact]
    public void KeyValue_SplitsEverySpecForm()
    {
        Assert.Equal(("R_fixture", "50"), IbisParser.KeyValue("R_fixture = 50"));
        Assert.Equal(("R_fixture", "50"), IbisParser.KeyValue("R_fixture=50"));
        Assert.Equal(("R_fixture", "50"), IbisParser.KeyValue("R_fixture   50"));
        Assert.Equal(("C_comp", "2.0pF 1.5pF 2.5pF"), IbisParser.KeyValue("C_comp   2.0pF   1.5pF   2.5pF"));
        Assert.Equal(("dV/dt_r", "2.20/1.06n 1.92/1.28n"), IbisParser.KeyValue("dV/dt_r  2.20/1.06n   1.92/1.28n"));
        Assert.Equal(("dV/dt_r", "2.20/1.06n   1.92/1.28n"), IbisParser.KeyValue("dV/dt_r = 2.20/1.06n   1.92/1.28n"));
    }
}
