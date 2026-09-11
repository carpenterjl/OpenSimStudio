using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Si.Ibis;

namespace OpenSim.Rf.Si;

/// <summary>A nonlinear buffer at the driver node: the current it pushes INTO the line at a
/// node voltage and time, plus that current's slope dI/dV (for the Newton step) and its die
/// capacitance C_comp (integrated separately, backward-Euler).</summary>
public interface INonlinearDriver
{
    (double Current, double Conductance) Evaluate(double nodeVolts, double timeSeconds);
    double CompCapacitanceFarads { get; }
}

/// <summary>A linear Thevenin driver V_s(t) behind R_s — the DEGENERATE case that reduces the
/// nonlinear engine to the exact linear one (the hard identity gate: this driver through
/// <see cref="NonlinearLink"/> ≡ <see cref="TransientLink"/> with the same source and R_s).</summary>
public sealed class LinearTheveninDriver : INonlinearDriver
{
    private readonly Func<double, double> _sourceVolts;
    private readonly double _rs;
    public LinearTheveninDriver(Func<double, double> sourceVolts, double sourceOhms)
    {
        if (sourceOhms <= 0) throw new ArgumentOutOfRangeException(nameof(sourceOhms));
        _sourceVolts = sourceVolts; _rs = sourceOhms;
    }
    public double CompCapacitanceFarads => 0;
    // I into the line = (Vs − V)/Rs; dI/dV = −1/Rs.
    public (double Current, double Conductance) Evaluate(double v, double t) =>
        ((_sourceVolts(t) - v) / _rs, -1 / _rs);
}

/// <summary>
/// The IBIS behavioral driver (Stage S11): the current pushed into the line is
/// −[Ku(t)·I_pu(Vcc−V) + Kd(t)·I_pd(V−V_pd) + I_gndclamp(V−V_gc) + I_powerclamp(V_pc−V)]
/// (IBIS positive current is INTO the pad, so the into-line current is its negative). The
/// switching coefficients Ku(t)/Kd(t) follow the bit stream through a trapezoid whose edge time
/// comes from [Ramp] (Δv/Δt). Pull-up / POWER-clamp tables are "Vcc relative" (voltage axis
/// = Vcc − V); pull-down / GND-clamp are rail-referenced (V − V_ref) — except an ECL model's
/// pull-down, which is Vcc relative too (<see cref="IbisTableAxis"/>). Currents interpolate
/// the monotone PWL tables with linear extrapolation past the ends.
/// </summary>
public sealed class IbisDriver : INonlinearDriver
{
    private readonly IbisModel _model;
    private readonly IbisCornerSelection _corner;
    private readonly double _vcc, _dt;
    private readonly double[] _ku, _kd;   // per-sample switching coefficients
    private readonly PwlTable _pu, _pd, _gc, _pc;

    private IbisDriver(IbisModel model, IbisCornerSelection corner, double vcc, double dt,
        double[] ku, double[] kd)
    {
        _model = model; _corner = corner; _vcc = vcc; _dt = dt; _ku = ku; _kd = kd;
        _pu = PwlTable.FromTable(model.Pullup, corner);
        _pd = PwlTable.FromTable(model.Pulldown, corner);
        _gc = PwlTable.FromTable(model.GndClamp, corner);
        _pc = PwlTable.FromTable(model.PowerClamp, corner);
        // Each table is referenced to ITS OWN rail. The defaults (pull-down and GND clamp to
        // ground, POWER clamp to the pull-up rail) reproduce the previous hardcoded behavior
        // exactly, so a file without the reference keywords is unchanged; a split-rail part,
        // where these genuinely differ, is no longer evaluated against the wrong supply.
        _pdRail = model.PulldownRailAt(corner);
        _gcRail = model.GndClampRailAt(corner);
        _pcRail = model.PowerClampRailAt(corner);
        _isEcl = model.IsEcl;
    }

    private readonly double _pdRail, _gcRail, _pcRail;
    private readonly bool _isEcl;

    public double CompCapacitanceFarads => _model.CComp.At(_corner) ?? 0;

    /// <summary>Builds the driver for a bit stream sampled at <paramref name="samplesPerUi"/>.
    /// Bit 1 = driving high (Ku → 1), bit 0 = low (Kd → 1); the edge ramps over the [Ramp]
    /// time (falls back to one UI when no ramp). The Ku/Kd schedule spans the whole pattern
    /// (periodic — the last bit wraps to the first, matching the exact-periodic convention).</summary>
    public static IbisDriver FromBits(IbisModel model, IbisCornerSelection corner,
        IReadOnlyList<bool> bits, int samplesPerUi, double dt) =>
        FromBits(model, corner, bits, samplesPerUi, dt, out _, out _);

    /// <summary>As <see cref="FromBits(IbisModel, IbisCornerSelection, IReadOnlyList{bool}, int,
    /// double)"/>, additionally reporting HOW the switching profile was obtained (measured
    /// waveform extraction or the [Ramp] fallback) and anything approximated on the way.</summary>
    public static IbisDriver FromBits(IbisModel model, IbisCornerSelection corner,
        IReadOnlyList<bool> bits, int samplesPerUi, double dt,
        out string switchingSource, out IReadOnlyList<string> switchingWarnings)
    {
        var warnings = new List<string>();
        switchingWarnings = warnings;
        if (!model.IsOutput)
            throw new ArgumentException($"IBIS model '{model.Name}' is not an output buffer (needs [Pullup] and [Pulldown]).");
        if (!model.TypeIsDriverCapable)
            throw new ArgumentException(
                $"IBIS model '{model.Name}' declares [Model_type] {model.ModelType}, which cannot "
                + "drive a line, but carries [Pullup] and [Pulldown] tables. Refusing rather than "
                + "believing the tables over the file's own declaration — pick a driver model.");
        double vcc = model.PullupRailAt(corner);
        int n = bits.Count * samplesPerUi;
        // Edge sample count from the ramp slew: t_edge = swing / (Δv/Δt); clamp to [2, 1 UI].
        double swing = vcc;
        int edgeRise = samplesPerUi, edgeFall = samplesPerUi;
        var ramp = model.Ramp;
        if (ramp is not null)
        {
            // Rising and falling edges have their OWN slews. [Ramp].Falling was parsed and then
            // never read, so every falling edge ran at the rising slew — invisible on a
            // symmetric buffer, a real timing error on an asymmetric one.
            int EdgeOf(IbisRampEdge e)
            {
                double dv = e.DeltaVolts.At(corner) ?? swing;
                double dtr = e.DeltaSeconds.At(corner) ?? dt;
                double slew = dv / dtr;                 // V/s
                return slew > 0
                    ? Math.Clamp((int)Math.Round(swing / slew / dt), 2, samplesPerUi)
                    : samplesPerUi;
            }
            edgeRise = EdgeOf(ramp.Rising);
            edgeFall = EdgeOf(ramp.Falling);
        }
        // Per-edge switching profiles. IBIS specifies the switching through the measured
        // [Rising/Falling Waveform] tables; the [Ramp] trapezoid is what the format itself
        // calls the fallback for files that carry no waveforms. Each edge is resolved
        // independently, so a file with only one waveform set still gets the measured profile
        // where it has one and the ramp where it does not.
        var (riseKu, riseKd, riseSource) = EdgeProfile(
            model, corner, model.RisingWaveforms, rising: true, dt, samplesPerUi,
            edgeRise, from: 0, to: 1, warnings);
        var (fallKu, fallKd, fallSource) = EdgeProfile(
            model, corner, model.FallingWaveforms, rising: false, dt, samplesPerUi,
            edgeFall, from: 1, to: 0, warnings);

        var ku = new double[n];
        var kd = new double[n];
        for (int b = 0; b < bits.Count; b++)
        {
            int target = bits[b] ? 1 : 0;
            int prev = bits[(b - 1 + bits.Count) % bits.Count] ? 1 : 0;
            var (eKu, eKd) = target > prev ? (riseKu, riseKd) : (fallKu, fallKd);
            for (int s = 0; s < samplesPerUi; s++)
            {
                // A transition plays its edge profile; a held bit sits at that profile's
                // SETTLED value, so the steady level always agrees with the edge that
                // reached it (the two models must not disagree about "fully driven").
                int idx = b * samplesPerUi + s;
                if (prev != target) { ku[idx] = eKu[s]; kd[idx] = eKd[s]; }
                else if (target == 1) { ku[idx] = riseKu[^1]; kd[idx] = riseKd[^1]; }
                else { ku[idx] = fallKu[^1]; kd[idx] = fallKd[^1]; }
            }
        }
        switchingSource = riseSource == fallSource
            ? riseSource
            : $"rising: {riseSource}; falling: {fallSource}";
        return new IbisDriver(model, corner, vcc, dt, ku, kd);
    }

    /// <summary>One edge's Ku/Kd over a UI: the measured two-/one-waveform extraction when the
    /// model carries waveforms for that edge, else the [Ramp] trapezoid — which is the profile
    /// that has always run, reproduced here verbatim so a ramp-only file is unchanged.</summary>
    private static (double[] Ku, double[] Kd, string Source) EdgeProfile(
        IbisModel model, IbisCornerSelection corner, IReadOnlyList<IbisWaveform> waveforms,
        bool rising, double dt, int samplesPerUi, int edge, int from, int to,
        List<string> warnings)
    {
        if (waveforms.Count is 1 or 2)
        {
            try
            {
                var sched = KuKdExtractor.Extract(model, corner, waveforms, rising, dt, samplesPerUi);
                warnings.AddRange(sched.Warnings);
                return (sched.Ku, sched.Kd, sched.Source);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // The waveforms are present but unusable (identical fixtures, a one-row table).
                // Say so and fall back to the ramp rather than failing the whole solve — the
                // ramp is a legitimate model, it is just the coarser one.
                warnings.Add($"IBIS {(rising ? "rising" : "falling")} waveform extraction failed "
                    + $"({ex.Message}) — falling back to the [Ramp] slew for this edge.");
            }
        }

        var ku = new double[samplesPerUi];
        var kd = new double[samplesPerUi];
        for (int s = 0; s < samplesPerUi; s++)
        {
            double frac = s < edge ? from + (to - from) * (s + 1.0) / edge : to;
            ku[s] = frac;
            kd[s] = 1 - frac;
        }
        return (ku, kd, "[Ramp] slew (no usable waveform tables)");
    }

    public (double Current, double Conductance) Evaluate(double v, double t)
    {
        int n = (int)Math.Round(t / _dt);
        n = Math.Clamp(n, 0, _ku.Length - 1);
        double ku = _ku[n], kd = _kd[n];
        // IBIS into-pad current, then negate for into-line. Pull-up / POWER-clamp tables are
        // "Vcc relative" (V_table = rail − V, slope sign −1), pull-down / GND-clamp rail-
        // referenced (V − rail, +1) — except an ECL pull-down, which is Vcc relative. Each
        // table is read against its own declared reference; IbisTableAxis owns the convention
        // and folds the chain-rule sign into the returned slope, so every G here is dI/dV.
        var (ipu, gpu) = IbisTableAxis.EvalSupplyReferenced(_pu, _vcc, v);
        var (ipd, gpd) = _isEcl
            ? IbisTableAxis.EvalSupplyReferenced(_pd, _pdRail, v)
            : IbisTableAxis.EvalGroundReferenced(_pd, _pdRail, v);
        var (igc, ggc) = IbisTableAxis.EvalGroundReferenced(_gc, _gcRail, v);
        var (ipc, gpc) = IbisTableAxis.EvalSupplyReferenced(_pc, _pcRail, v);
        double iPad = ku * ipu + kd * ipd + igc + ipc;
        double gPad = ku * gpu + kd * gpd + ggc + gpc;
        return (-iPad, -gPad);
    }
}

/// <summary>The receiver termination for the SINGLE-LINE reduction (linear R∥C; open = R = ∞),
/// which folds this admittance into the channel FIRs. A nonlinear (clamped) receiver cannot be
/// expressed that way and goes through <see cref="NonlinearLink.SolveNPort"/> instead, where
/// every port is an unknown.</summary>
public sealed record NonlinearReceiver(double LoadOhms, double LoadCapacitanceFarads = 0)
{
    public Complex Admittance(double frequencyHz)
    {
        // The channel reduction folds this admittance into the driver-node FIRs, so an
        // infinite admittance has nowhere to go: a shorted far end must be refused here rather
        // than silently producing Infinity (and, from there, NaN samples the Newton loop would
        // blame on a non-monotone table). MtlNetwork.SolveTerminated carries the impedance-form
        // row that CAN express a short; the linear transient path reaches it directly.
        if (LoadOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(LoadOhms),
                $"Receiver resistance must be positive (got {LoadOhms} Ω). The nonlinear "
                + "driver engine reduces the channel against a finite load; for a shorted far "
                + "end use the linear TransientLink path, which solves the short exactly.");
        double omega = 2 * Math.PI * frequencyHz;
        Complex y = double.IsPositiveInfinity(LoadOhms) ? Complex.Zero : 1.0 / LoadOhms;
        return y + new Complex(0, omega * LoadCapacitanceFarads);
    }
}

/// <summary>The result of a nonlinear link solve: one steady-state period at the driver node
/// and the receiver, plus the channel memory (truncated FIR length) and its tail-energy bound.</summary>
public sealed record NonlinearResult(
    double SampleIntervalSeconds, double[] DriverVolts, double[] ReceiverVolts,
    int ChannelMemorySamples, double TailEnergyFraction);

/// <summary>
/// The Stage S11 nonlinear transient engine: a NONLINEAR driver into a LINEAR channel. The
/// channel (a single-line MTL with a linear receiver load) is reduced to two FIR filters from
/// its frequency response — the driver-node driving-point admittance Y_in(ω) and the near→far
/// transfer H(ω) — sampled and inverse-FFT'd, then truncated at a measured tail-energy bound.
/// The driver node is time-stepped: at each sample the channel presents a Norton equivalent
/// (its instantaneous admittance y_in[0] + a history current from past node voltages), and the
/// nonlinear node equation I_drv(V) = y_in[0]·V + hist + C_comp·(V−V₋)/Δt is solved by Newton
/// on the monotone buffer curves (backward-Euler C_comp — the house transient precedent). The
/// receiver waveform is the FIR H convolved with the settled node voltage. Warm-up over several
/// periods primes the FIR; the last period is the steady state.
///
/// <para>The engine does NOT claim the exact-periodic identity the linear <see cref="TransientLink"/>
/// holds — a nonlinear system has no closed-form periodic answer — but a LINEAR driver reduces
/// it to that engine (gated). Single driven line only; multi-line nonlinear crosstalk is a named
/// follow-up.</para>
/// </summary>
public static partial class NonlinearLink
{
    /// <summary>The channel FIR is built on this DFT length (a power of two); its Δf = 1/(N·Δt)
    /// resolves the channel memory (round trips ≪ N·Δt for any real board line).</summary>
    private const int ChannelFft = 8192;

    public static NonlinearResult Solve(MtlNetwork network, INonlinearDriver driver,
        NonlinearReceiver receiver, IReadOnlyList<bool> bits, int samplesPerUi,
        double sampleIntervalSeconds, int warmupPeriods = 4, double tailEnergyBound = 1e-4,
        int? maxDegreeOfParallelism = null)
    {
        if (network.ConductorCount != 1)
            throw new ArgumentException(
                "This entry reduces the channel against ONE linear receiver, so it handles a "
                + "single line. For coupled lines — or for a nonlinear (clamped) receiver — use "
                + nameof(SolveNPort) + ", which solves every port as an unknown.");
        if (samplesPerUi < 2) throw new ArgumentOutOfRangeException(nameof(samplesPerUi));
        double dt = sampleIntervalSeconds;

        // ---- Channel FIRs from the frequency response (half spectrum, parallel slots). ----
        var yInSpec = new Complex[ChannelFft];
        var hSpec = new Complex[ChannelFft];
        int half = ChannelFft / 2;
        Parallel.For(0, half + 1,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 }, m =>
        {
            double f = m / (ChannelFft * dt);
            var t = network.ChainMatrix(f);                 // 2×2 for one line
            Complex yl = receiver.Admittance(f);
            Complex denom = t[0, 0] + t[0, 1] * yl;         // V_near = denom · V_far
            Complex yIn = (t[1, 0] + t[1, 1] * yl) / denom; // I_near / V_near
            Complex h = 1.0 / denom;                        // V_far / V_near
            yInSpec[m] = yIn; hSpec[m] = h;
            if (m > 0 && m < half)                          // conjugate-symmetric mirror
            {
                yInSpec[ChannelFft - m] = Complex.Conjugate(yIn);
                hSpec[ChannelFft - m] = Complex.Conjugate(h);
            }
        });
        var yInFir = RealPart(Fft.Inverse(yInSpec));
        var hFir = RealPart(Fft.Inverse(hSpec));

        int memory = TruncationLength(yInFir, hFir, tailEnergyBound, out double tailFraction);
        double g0 = yInFir[0];                               // instantaneous channel admittance

        // ---- Time-step the driver node over warm-up + one final period. ----
        int period = bits.Count * samplesPerUi;
        int total = (warmupPeriods + 1) * period;
        double ccomp = driver.CompCapacitanceFarads;
        var vNode = new double[total];
        for (int n = 0; n < total; n++)
        {
            double tSchedule = (n % period) * dt;           // the periodic driver schedule
            double vPrev = n > 0 ? vNode[n - 1] : 0;
            double hist = 0;                                 // Σ_{k≥1} y_in[k]·V[n−k]
            int kMax = Math.Min(memory, n);
            for (int k = 1; k <= kMax; k++) hist += yInFir[k] * vNode[n - k];

            // Newton: g(V) = I_drv(V) − g0·V − hist − C_comp·(V − vPrev)/Δt = 0.
            double v = vPrev;
            bool converged = false;
            for (int iter = 0; iter < 60; iter++)
            {
                var (idrv, gdrv) = driver.Evaluate(v, tSchedule);
                double gv = idrv - g0 * v - hist - ccomp * (v - vPrev) / dt;
                double slope = gdrv - g0 - ccomp / dt;
                if (slope == 0) break;
                double step = gv / slope;
                v -= step;
                if (Math.Abs(step) <= 1e-9 * (1 + Math.Abs(v))) { converged = true; break; }
            }
            if (!converged)
                throw new InvalidOperationException(
                    $"The nonlinear driver Newton solve did not converge at sample {n} "
                    + "(a non-monotone V-I table or a degenerate channel admittance).");
            vNode[n] = v;
        }

        // ---- Receiver = H FIR ∗ node voltage; return the last (steady) period. ----
        var vRxFull = new double[total];
        for (int n = 0; n < total; n++)
        {
            double acc = 0;
            int kMax = Math.Min(memory, n);
            for (int k = 0; k <= kMax; k++) acc += hFir[k] * vNode[n - k];
            vRxFull[n] = acc;
        }
        var driverPeriod = vNode[^period..];
        var receiverPeriod = vRxFull[^period..];
        return new NonlinearResult(dt, driverPeriod, receiverPeriod, memory, tailFraction);
    }

    private static double[] RealPart(Complex[] c)
    {
        var r = new double[c.Length];
        for (int i = 0; i < c.Length; i++) r[i] = c[i].Real;
        return r;
    }

    /// <summary>The FIR memory length: the smallest L past which BOTH filters' tail energy is
    /// below <paramref name="bound"/> of their total (measured, reported — the truncation-
    /// convergence gate doubles the window and checks the waveform barely moves).</summary>
    private static int TruncationLength(double[] a, double[] b, double bound, out double tailFraction)
    {
        double Total(double[] x) => x.Sum(v => v * v);
        double ta = Total(a), tb = Total(b);
        int Cut(double[] x, double t)
        {
            double acc = 0;
            for (int i = x.Length - 1; i >= 0; i--)
            {
                acc += x[i] * x[i];
                if (acc > bound * t) return Math.Min(i + 1, x.Length - 1);
            }
            return 0;
        }
        int la = Cut(a, ta), lb = Cut(b, tb);
        int l = Math.Max(la, lb);
        double tail(double[] x, double t) => t == 0 ? 0 : x.Skip(l + 1).Sum(v => v * v) / t;
        tailFraction = Math.Max(tail(a, ta), tail(b, tb));
        return l;
    }
}
