namespace OpenSim.Rf.Si.Ibis;

/// <summary>The switching schedule of an IBIS buffer: the pull-up and pull-down scaling
/// coefficients sampled on a uniform grid, plus how they were obtained.</summary>
/// <param name="Ku">Pull-up scaling per sample.</param>
/// <param name="Kd">Pull-down scaling per sample.</param>
/// <param name="TimeStepSeconds">The grid step the coefficients are sampled on.</param>
/// <param name="Source">Human-readable provenance, surfaced with the run's assumptions.</param>
/// <param name="Warnings">Anything approximated on the way (clipped tables, out-of-range
/// coefficients) — never silent.</param>
public sealed record KuKdSchedule(
    double[] Ku, double[] Kd, double TimeStepSeconds, string Source,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Backs the switching coefficients Ku(t)/Kd(t) out of an IBIS buffer's measured
/// [Rising Waveform] / [Falling Waveform] tables — the model IBIS actually specifies, as
/// opposed to the [Ramp] slew, which is the fallback for files that carry no waveforms.
///
/// <para><b>The equation.</b> A waveform is V(t) measured at the pad while the buffer drives a
/// known fixture (R_fixture to V_fixture). At every sample the pad node balances:</para>
/// <code>
///   Ku·I_pu(V_pu − V) + Kd·I_pd(V − V_pd) + I_gc(V − V_gc) + I_pc(V_pc − V)
///     + C_comp·dV/dt + (V − V_fixture)/R_fixture = 0
/// </code>
/// <para>(each table on its IBIS axis — pull-up and POWER clamp "Vcc relative", pull-down and
/// GND clamp rail-referenced, an ECL pull-down Vcc relative; see <see cref="IbisTableAxis"/>).
/// Everything except Ku and Kd is known at that sample, so ONE waveform gives one linear
/// equation in two unknowns and TWO waveforms measured into DIFFERENT fixtures give a 2×2
/// system — which is exactly why IBIS invites two fixtures per edge.</para>
///
/// <para><b>With only one waveform</b> the system is closed with the complementary-switching
/// constraint Ku + Kd = 1. That is an assumption, not a measurement, and it is reported as
/// such rather than presented as an extraction.</para>
///
/// <para><b>With no waveforms</b> this type is not used at all: the caller keeps the [Ramp]
/// trapezoid, which is what has always run.</para>
/// </summary>
public static class KuKdExtractor
{
    /// <summary>Extract the schedule for one edge onto a uniform grid of
    /// <paramref name="sampleCount"/> steps of <paramref name="dt"/>.</summary>
    /// <param name="model">The buffer (tables + rails + C_comp).</param>
    /// <param name="corner">Which process corner to read every table at.</param>
    /// <param name="waveforms">The edge's waveforms — 1 or 2 are used; more is a typed failure
    /// because there is no rule for which pair to believe.</param>
    /// <param name="rising">True for a rising edge (used only to describe the result).</param>
    /// <param name="dt">The output grid step.</param>
    /// <param name="sampleCount">How many output samples to produce.</param>
    public static KuKdSchedule Extract(IbisModel model, IbisCornerSelection corner,
        IReadOnlyList<IbisWaveform> waveforms, bool rising, double dt, int sampleCount)
    {
        if (waveforms is null || waveforms.Count == 0)
            throw new ArgumentException(
                "Ku/Kd extraction needs at least one waveform table; a model with none keeps "
                + "the [Ramp] trapezoid.", nameof(waveforms));
        if (waveforms.Count > 2)
            throw new ArgumentException(
                $"This edge carries {waveforms.Count} waveform tables. IBIS specifies the "
                + "extraction against one or two fixtures, and there is no rule for choosing "
                + "among more — remove the extras or name the pair to use.", nameof(waveforms));
        if (dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));

        var warnings = new List<string>();
        var pu = PwlTable.FromTable(model.Pullup, corner);
        var pd = PwlTable.FromTable(model.Pulldown, corner);
        var gc = PwlTable.FromTable(model.GndClamp, corner);
        var pc = PwlTable.FromTable(model.PowerClamp, corner);
        double vPu = model.PullupRailAt(corner);
        double vPd = model.PulldownRailAt(corner);
        double vGc = model.GndClampRail;
        double vPc = model.PowerClampRailAt(corner);
        bool ecl = model.IsEcl;
        double cComp = model.CComp.At(corner) ?? 0;

        var ku = new double[sampleCount];
        var kd = new double[sampleCount];

        // Per waveform, per output sample: the pad voltage and its slope, resampled onto the
        // common grid. The waveform's own grid is generally neither uniform nor aligned with
        // the simulation step, so the derivative is taken on the WAVEFORM's grid (where the
        // measurement lives) and then resampled — differentiating after resampling would
        // smear an edge that falls between two output samples.
        var sampled = new (double V, double DvDt)[waveforms.Count][];
        for (int w = 0; w < waveforms.Count; w++)
            sampled[w] = Resample(waveforms[w], corner, dt, sampleCount, warnings);

        bool clipped = false;
        // Row buffers for the per-sample 2×2 system, allocated ONCE. stackalloc inside the loop
        // would not be freed until the method returned, so a long schedule would grow the frame
        // sample by sample — the house's recorded StackOverflow trap.
        var a = new double[2];      // ∂/∂Ku
        var b = new double[2];      // ∂/∂Kd
        var r = new double[2];      // the known remainder (negated)
        for (int n = 0; n < sampleCount; n++)
        {
            // Residual of the node equation at this sample with Ku = Kd = 0, and the two
            // coefficients' sensitivities — one row per waveform.
            for (int w = 0; w < waveforms.Count; w++)
            {
                double v = sampled[w][n].V, dv = sampled[w][n].DvDt;
                double rf = waveforms[w].RFixtureOhms, vf = waveforms[w].VFixtureVolts;
                // The same axes IbisDriver.Evaluate reads the tables on — the extractor and
                // the driver must agree exactly, or every coefficient carries the mismatch.
                a[w] = IbisTableAxis.EvalSupplyReferenced(pu, vPu, v).I;
                b[w] = ecl
                    ? IbisTableAxis.EvalSupplyReferenced(pd, vPd, v).I
                    : IbisTableAxis.EvalGroundReferenced(pd, vPd, v).I;
                double known = IbisTableAxis.EvalGroundReferenced(gc, vGc, v).I
                             + IbisTableAxis.EvalSupplyReferenced(pc, vPc, v).I
                             + cComp * dv
                             + (rf > 0 ? (v - vf) / rf : 0);
                r[w] = -known;
            }

            double kuN, kdN;
            if (waveforms.Count == 2)
            {
                double det = a[0] * b[1] - a[1] * b[0];
                double scale = Math.Max(Math.Abs(a[0] * b[1]), Math.Abs(a[1] * b[0]));
                if (Math.Abs(det) <= 1e-12 * Math.Max(scale, 1e-30))
                    throw new InvalidOperationException(
                        "The two waveform fixtures present the same load line at t = "
                        + $"{n * dt:g3} s, so Ku and Kd cannot be separated (the 2×2 system is "
                        + "singular). Two DIFFERENT fixtures are what make the extraction "
                        + "possible — check R_fixture/V_fixture on both waveforms.");
                kuN = (r[0] * b[1] - r[1] * b[0]) / det;
                kdN = (a[0] * r[1] - a[1] * r[0]) / det;
            }
            else
            {
                // One waveform: close the system with Ku + Kd = 1 (complementary switching).
                // (a − b)·Ku = r − b.
                double denom = a[0] - b[0];
                if (Math.Abs(denom) <= 1e-12 * Math.Max(Math.Abs(a[0]) + Math.Abs(b[0]), 1e-30))
                {
                    // Pull-up and pull-down are indistinguishable here (both ~zero current, as
                    // at a rail): hold the previous value rather than dividing through noise.
                    kuN = n > 0 ? ku[n - 1] : 0;
                    kdN = 1 - kuN;
                }
                else
                {
                    kuN = (r[0] - b[0]) / denom;
                    kdN = 1 - kuN;
                }
            }

            if (kuN < -1e-6 || kuN > 1 + 1e-6 || kdN < -1e-6 || kdN > 1 + 1e-6) clipped = true;
            ku[n] = kuN;
            kd[n] = kdN;
        }

        if (clipped)
            warnings.Add("Extracted switching coefficients left [0, 1] at some samples — the "
                + "waveform, its fixture and the V-I tables are not perfectly consistent "
                + "(a common artifact of tables and waveforms measured separately). The values "
                + "are used AS EXTRACTED rather than clamped, so the inconsistency stays "
                + "visible in the result.");

        string source = waveforms.Count == 2
            ? $"two-waveform extraction from the {(rising ? "rising" : "falling")} edge's "
              + $"{waveforms[0].RFixtureOhms:g3} Ω and {waveforms[1].RFixtureOhms:g3} Ω fixtures"
            : $"one-waveform extraction from the {(rising ? "rising" : "falling")} edge's "
              + $"{waveforms[0].RFixtureOhms:g3} Ω fixture, closed with Ku + Kd = 1 "
              + "(complementary switching ASSUMED — a second fixture would measure it)";
        return new KuKdSchedule(ku, kd, dt, source, warnings);
    }

    /// <summary>The waveform's V and dV/dt on the output grid. The derivative is central on the
    /// waveform's own (possibly non-uniform) grid, one-sided at its ends; both are then linearly
    /// interpolated to the output samples. Past the table's end the buffer is settled, so the
    /// last value is held and the slope goes to zero.</summary>
    private static (double V, double DvDt)[] Resample(IbisWaveform waveform,
        IbisCornerSelection corner, double dt, int sampleCount, List<string> warnings)
    {
        var rows = waveform.Rows
            .Select(r => (T: r.TimeSeconds, V: r.VoltageVolts.At(corner) ?? 0))
            .OrderBy(r => r.T).ToArray();
        if (rows.Length < 2)
            throw new InvalidOperationException(
                $"A waveform measured into the {waveform.RFixtureOhms:g3} Ω fixture has "
                + $"{rows.Length} row(s); at least two are needed to define an edge.");

        var slope = new double[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            if (i == 0) slope[i] = (rows[1].V - rows[0].V) / (rows[1].T - rows[0].T);
            else if (i == rows.Length - 1)
                slope[i] = (rows[i].V - rows[i - 1].V) / (rows[i].T - rows[i - 1].T);
            else slope[i] = (rows[i + 1].V - rows[i - 1].V) / (rows[i + 1].T - rows[i - 1].T);
        }

        double span = rows[^1].T - rows[0].T;
        if (sampleCount * dt > span * 1.000001)
            warnings.Add($"The {waveform.RFixtureOhms:g3} Ω waveform spans {span * 1e9:g3} ns but "
                + $"the schedule needs {sampleCount * dt * 1e9:g3} ns; the settled final value is "
                + "held beyond the table (the buffer has finished switching there).");

        var outp = new (double V, double DvDt)[sampleCount];
        for (int n = 0; n < sampleCount; n++)
        {
            double t = rows[0].T + n * dt;
            if (t <= rows[0].T) { outp[n] = (rows[0].V, slope[0]); continue; }
            if (t >= rows[^1].T) { outp[n] = (rows[^1].V, 0); continue; }
            int hi = 1;
            while (hi < rows.Length - 1 && rows[hi].T < t) hi++;
            double f = (t - rows[hi - 1].T) / (rows[hi].T - rows[hi - 1].T);
            outp[n] = (rows[hi - 1].V + f * (rows[hi].V - rows[hi - 1].V),
                       slope[hi - 1] + f * (slope[hi] - slope[hi - 1]));
        }
        return outp;
    }
}
