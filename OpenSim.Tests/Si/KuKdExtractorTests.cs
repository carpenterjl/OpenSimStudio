using System.Text;
using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Ibis;

namespace OpenSim.Tests.Si;

/// <summary>
/// Stage B3 — backing Ku(t)/Kd(t) out of the measured [Rising/Falling Waveform] tables, the
/// switching model IBIS actually specifies. Before this the tables were parsed, stored, and
/// never read: every edge ran on the [Ramp] slew, which the file format calls the fallback.
///
/// <para>The headline gate is an INVERSE identity. A buffer with linear conductance tables has
/// a closed-form pad voltage for any (Ku, Kd), so a waveform can be GENERATED from a known
/// schedule, written out as a real .ibs, parsed back through the real parser, and the extractor
/// must return the schedule it was built from. Nothing is fitted — if the algebra is right the
/// recovery is exact to roundoff, and any sign or rail error shows immediately.</para>
/// </summary>
public class KuKdExtractorTests
{
    private const double Vcc = 3.3;
    private const double Gpu = 12.0e-3;      // pull-up conductance [S]
    private const double Gpd = 9.0e-3;       // pull-down conductance [S], deliberately unequal

    /// <summary>The exact pad voltage of the linear buffer into a fixture, from the same node
    /// equation the extractor inverts: Ku·g_pu·(V−Vcc) + Kd·g_pd·V + (V−V_f)/R_f = 0.</summary>
    private static double PadVolts(double ku, double kd, double rf, double vf) =>
        (ku * Gpu * Vcc + vf / rf) / (ku * Gpu + kd * Gpd + 1 / rf);

    /// <summary>A schedule with an interesting shape — not a straight ramp, so a linear-ramp
    /// bug cannot pass by coincidence.</summary>
    private static (double Ku, double Kd) Schedule(int n, int count)
    {
        double x = (double)n / (count - 1);
        double ku = 0.5 - 0.5 * Math.Cos(Math.PI * x);   // smooth 0 → 1
        return (ku, 1 - ku);
    }

    private static string BuildIbs(int count, double dt,
        (double R, double V)[] fixtures, bool complementary = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[IBIS Ver]      2.1");
        sb.AppendLine("[File Name]     synth.ibs");
        sb.AppendLine("[Component]     SYNTH");
        sb.AppendLine("[Model]         GEN");
        sb.AppendLine("Model_type      Output");
        sb.AppendLine("C_comp          0.0");
        sb.AppendLine($"[Voltage Range] {Vcc}      {Vcc}      {Vcc}");
        sb.AppendLine($"[Pullup Reference]   {Vcc}");
        sb.AppendLine("[Pulldown Reference] 0.0");
        // Linear tables spanning the operating range (linear interpolation of a linear
        // function is exact, so the tables ARE the conductances). The pull-up is tabulated on
        // the IBIS axis V_table = Vcc − V: I_pu = g_pu·(V − Vcc) = −g_pu·V_table.
        sb.AppendLine("[Pullup]");
        for (int k = -1; k <= 3; k++)
        {
            double vt = k * Vcc / 2;
            sb.AppendLine($"   {vt:R}   {-Gpu * vt:R}   {-Gpu * vt:R}   {-Gpu * vt:R}");
        }
        sb.AppendLine("[Pulldown]");
        for (int k = -1; k <= 3; k++)
        {
            double v = k * Vcc / 2;
            sb.AppendLine($"   {v:R}   {Gpd * v:R}   {Gpd * v:R}   {Gpd * v:R}");
        }
        sb.AppendLine("[Ramp]");
        sb.AppendLine("dV/dt_r     1.65/0.5n    1.65/0.5n    1.65/0.5n");
        sb.AppendLine("dV/dt_f     1.65/0.5n    1.65/0.5n    1.65/0.5n");
        foreach (var (r, vf) in fixtures)
        {
            sb.AppendLine("[Rising Waveform]");
            sb.AppendLine($"R_fixture   {r:R}");
            sb.AppendLine($"V_fixture   {vf:R}");
            for (int n = 0; n < count; n++)
            {
                var (ku, kd) = Schedule(n, count);
                if (!complementary) kd = 0.25 + 0.5 * kd;   // break Ku + Kd = 1 deliberately
                double v = PadVolts(ku, kd, r, vf);
                sb.AppendLine($"   {n * dt:R}   {v:R}   {v:R}   {v:R}");
            }
        }
        sb.AppendLine("[End]");
        return sb.ToString();
    }

    [Fact]
    public void TwoWaveforms_RecoverTheGeneratingScheduleExactly()
    {
        // THE gate. Two different fixtures give two independent equations per sample, so both
        // coefficients are MEASURED — no complementary assumption is needed, and the recovery
        // of a deliberately non-complementary schedule proves it.
        const int count = 33;
        const double dt = 50e-12;
        var fixtures = new[] { (50.0, 0.0), (50.0, Vcc) };
        var text = BuildIbs(count, dt, fixtures, complementary: false);
        var model = new IbisParser().Parse(text).Model("GEN");

        var schedule = KuKdExtractor.Extract(model, IbisCornerSelection.Typ,
            model.RisingWaveforms, rising: true, dt, count);

        for (int n = 0; n < count; n++)
        {
            var (ku, kd) = Schedule(n, count);
            kd = 0.25 + 0.5 * kd;
            Assert.True(Math.Abs(schedule.Ku[n] - ku) < 1e-9,
                $"Ku[{n}] {schedule.Ku[n]:R} vs generating {ku:R}");
            Assert.True(Math.Abs(schedule.Kd[n] - kd) < 1e-9,
                $"Kd[{n}] {schedule.Kd[n]:R} vs generating {kd:R}");
        }
        Assert.Contains("two-waveform", schedule.Source);
    }

    [Fact]
    public void OneWaveform_RecoversAComplementaryScheduleExactly()
    {
        // With a single fixture the system is closed by Ku + Kd = 1. When the buffer really is
        // complementary — as this generated one is — that closure is exact, so the recovery is
        // still an identity rather than a fit.
        const int count = 25;
        const double dt = 40e-12;
        var text = BuildIbs(count, dt, new[] { (50.0, 0.0) });
        var model = new IbisParser().Parse(text).Model("GEN");

        var schedule = KuKdExtractor.Extract(model, IbisCornerSelection.Typ,
            model.RisingWaveforms, rising: true, dt, count);

        for (int n = 0; n < count; n++)
        {
            var (ku, kd) = Schedule(n, count);
            Assert.True(Math.Abs(schedule.Ku[n] - ku) < 1e-9,
                $"Ku[{n}] {schedule.Ku[n]:R} vs generating {ku:R}");
            Assert.True(Math.Abs(schedule.Kd[n] - kd) < 1e-9, $"Kd[{n}]");
        }
        Assert.Contains("ASSUMED", schedule.Source);
    }

    [Fact]
    public void OneWaveform_AnnouncesTheComplementaryAssumption()
    {
        // The one-waveform closure is an assumption, not a measurement, and the result says so —
        // the difference matters to anyone reading a number off the eye.
        var text = BuildIbs(9, 50e-12, new[] { (50.0, 0.0) });
        var model = new IbisParser().Parse(text).Model("GEN");
        var schedule = KuKdExtractor.Extract(model, IbisCornerSelection.Typ,
            model.RisingWaveforms, rising: true, 50e-12, 9);
        Assert.Contains("Ku + Kd = 1", schedule.Source);
        Assert.Contains("complementary switching ASSUMED", schedule.Source);
    }

    [Fact]
    public void IdenticalFixtures_AreATypedFailure()
    {
        // Two waveforms measured into the SAME fixture present the same load line, so the 2×2
        // system is singular and the coefficients genuinely cannot be separated. That is a
        // property of the file, and it is named rather than papered over with a pseudo-inverse.
        const int count = 11;
        const double dt = 50e-12;
        var text = BuildIbs(count, dt, new[] { (50.0, 0.0), (50.0, 0.0) });
        var model = new IbisParser().Parse(text).Model("GEN");

        var e = Assert.Throws<InvalidOperationException>(() => KuKdExtractor.Extract(
            model, IbisCornerSelection.Typ, model.RisingWaveforms, rising: true, dt, count));
        Assert.Contains("same load line", e.Message);
        Assert.Contains("singular", e.Message);
    }

    [Fact]
    public void NoWaveforms_IsATypedFailure_SoTheCallerKeepsTheRamp()
    {
        var text = BuildIbs(5, 50e-12, Array.Empty<(double, double)>());
        var model = new IbisParser().Parse(text).Model("GEN");
        Assert.Empty(model.RisingWaveforms);
        var e = Assert.Throws<ArgumentException>(() => KuKdExtractor.Extract(
            model, IbisCornerSelection.Typ, model.RisingWaveforms, rising: true, 50e-12, 5));
        Assert.Contains("[Ramp]", e.Message);
    }

    [Fact]
    public void AScheduleLongerThanTheWaveform_HoldsTheSettledValueAndSaysSo()
    {
        // Real waveform tables stop once the buffer has settled, while a schedule spans a whole
        // UI. Holding the final value is right — the buffer has finished switching — but the
        // extrapolation is stated rather than assumed.
        const int count = 12;
        const double dt = 50e-12;
        var text = BuildIbs(count, dt, new[] { (50.0, 0.0), (50.0, Vcc) });
        var model = new IbisParser().Parse(text).Model("GEN");

        var schedule = KuKdExtractor.Extract(model, IbisCornerSelection.Typ,
            model.RisingWaveforms, rising: true, dt, count * 3);

        Assert.Contains(schedule.Warnings, w => w.Contains("held beyond the table"));
        // Past the table the coefficients sit at their settled values.
        for (int n = count; n < count * 3; n++)
        {
            Assert.True(Math.Abs(schedule.Ku[n] - schedule.Ku[count - 1]) < 1e-9);
            Assert.True(Math.Abs(schedule.Kd[n] - schedule.Kd[count - 1]) < 1e-9);
        }
    }

    // ------------------------------------------------------------------
    // Driver integration: which switching model actually runs.
    // ------------------------------------------------------------------

    [Fact]
    public void ARampOnlyFile_KeepsTheRampProfile_Bitwise()
    {
        // The back-compat pin. A file with no waveform tables must run the trapezoid it has
        // always run — same samples, not merely a similar shape — so every result produced
        // before the extraction existed is reproduced exactly.
        var text = BuildIbs(5, 50e-12, Array.Empty<(double, double)>());
        var model = new IbisParser().Parse(text).Model("GEN");
        Assert.Empty(model.RisingWaveforms);

        const int spui = 16;
        const double dt = 62.5e-12;      // 1 ns UI; the ramp declares 1.65 V / 0.5 ns
        var driver = IbisDriver.FromBits(model, IbisCornerSelection.Typ,
            new[] { true, false }, spui, dt, out string source, out _);

        Assert.Contains("[Ramp]", source);

        // Reproduce the trapezoid independently and compare through the public evaluator.
        double swing = Vcc, slew = 1.65 / 0.5e-9;
        int edge = Math.Clamp((int)Math.Round(swing / slew / dt), 2, spui);
        for (int b = 0; b < 2; b++)
        {
            int target = b == 0 ? 1 : 0, prev = b == 0 ? 0 : 1;
            for (int sIdx = 0; sIdx < spui; sIdx++)
            {
                double frac = sIdx < edge ? prev + (target - prev) * (sIdx + 1.0) / edge : target;
                double expected = ExpectedIntoLine(frac, 0.5 * Vcc);
                double actual = driver.Evaluate(0.5 * Vcc, (b * spui + sIdx) * dt).Current;
                Assert.Equal(expected, actual, 15);
            }
        }
    }

    /// <summary>The into-line current of the generated LINEAR buffer at a given Ku (with
    /// Kd = 1 − Ku): −[Ku·g_pu(V−Vcc) + Kd·g_pd·V]. Independent of the driver's own code.</summary>
    private static double ExpectedIntoLine(double ku, double v) =>
        -(ku * Gpu * (v - Vcc) + (1 - ku) * Gpd * v);

    [Fact]
    public void AWaveformFile_RunsTheMeasuredExtraction_NotTheRamp()
    {
        // The complement: when the file DOES carry waveforms, the measured profile is what
        // runs. The generated file's ramp and waveforms describe different shapes, so the
        // resulting schedule must differ from the trapezoid — otherwise the extraction is
        // built and quietly ignored, which is the state this stage exists to end.
        const int count = 16;
        const double dt = 62.5e-12;
        var text = BuildIbs(count, dt, new[] { (50.0, 0.0), (50.0, Vcc) });
        var model = new IbisParser().Parse(text).Model("GEN");

        var driver = IbisDriver.FromBits(model, IbisCornerSelection.Typ,
            new[] { true, false }, count, dt, out string source, out _);
        Assert.Contains("waveform extraction", source);

        // The generating schedule is a raised cosine; the ramp would be a straight line. At the
        // UI midpoint they differ measurably.
        var (kuMid, _) = Schedule(count / 2, count);
        double expected = ExpectedIntoLine(kuMid, 0.5 * Vcc);
        double actual = driver.Evaluate(0.5 * Vcc, (count / 2) * dt).Current;
        Assert.True(Math.Abs(actual - expected) < 1e-9 * Math.Max(Math.Abs(expected), 1e-12),
            $"mid-edge into-line {actual:R} vs the generating schedule's {expected:R}");
    }

    [Fact]
    public void UnusableWaveforms_FallBackToTheRamp_AndSayWhy()
    {
        // Two identical fixtures cannot separate Ku from Kd. That is worth naming, but it is
        // not worth failing an entire eye over when a legitimate coarser model — the ramp —
        // is right there. The fallback is reported, never silent.
        const int count = 12;
        const double dt = 62.5e-12;
        var text = BuildIbs(count, dt, new[] { (50.0, 0.0), (50.0, 0.0) });
        var model = new IbisParser().Parse(text).Model("GEN");

        var driver = IbisDriver.FromBits(model, IbisCornerSelection.Typ,
            new[] { true, false }, count, dt, out string source, out var warnings);
        Assert.Contains("[Ramp]", source);
        Assert.Contains(warnings, w => w.Contains("same load line"));
        Assert.Contains(warnings, w => w.Contains("falling back"));
    }

    [Fact]
    public void MoreThanTwoWaveformsPerEdge_IsATypedFailure()
    {
        const int count = 9;
        const double dt = 50e-12;
        var text = BuildIbs(count, dt, new[] { (50.0, 0.0), (50.0, Vcc), (75.0, 0.0) });
        var model = new IbisParser().Parse(text).Model("GEN");
        Assert.Equal(3, model.RisingWaveforms.Count);

        var e = Assert.Throws<ArgumentException>(() => KuKdExtractor.Extract(
            model, IbisCornerSelection.Typ, model.RisingWaveforms, rising: true, dt, count));
        Assert.Contains("no rule for choosing", e.Message);
    }
}
