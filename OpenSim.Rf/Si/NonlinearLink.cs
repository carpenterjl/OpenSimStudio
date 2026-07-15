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
/// −[Ku(t)·I_pu(V−Vcc) + Kd(t)·I_pd(V) + I_gndclamp(V) + I_powerclamp(V−Vcc)] (IBIS positive
/// current is INTO the pad, so the into-line current is its negative). The switching
/// coefficients Ku(t)/Kd(t) follow the bit stream through a trapezoid whose edge time comes
/// from [Ramp] (Δv/Δt). Pull-up / POWER-clamp tables are referenced to the supply
/// (voltage axis = V − Vcc); pull-down / GND-clamp to ground. Currents interpolate the
/// monotone PWL tables with linear extrapolation past the ends.
/// </summary>
public sealed class IbisDriver : INonlinearDriver
{
    private readonly IbisModel _model;
    private readonly IbisCornerSelection _corner;
    private readonly double _vcc, _dt;
    private readonly double[] _ku, _kd;   // per-sample switching coefficients
    private readonly Pwl _pu, _pd, _gc, _pc;

    private IbisDriver(IbisModel model, IbisCornerSelection corner, double vcc, double dt,
        double[] ku, double[] kd)
    {
        _model = model; _corner = corner; _vcc = vcc; _dt = dt; _ku = ku; _kd = kd;
        _pu = Pwl.FromTable(model.Pullup, corner);
        _pd = Pwl.FromTable(model.Pulldown, corner);
        _gc = Pwl.FromTable(model.GndClamp, corner);
        _pc = Pwl.FromTable(model.PowerClamp, corner);
    }

    public double CompCapacitanceFarads => _model.CComp.At(_corner) ?? 0;

    /// <summary>Builds the driver for a bit stream sampled at <paramref name="samplesPerUi"/>.
    /// Bit 1 = driving high (Ku → 1), bit 0 = low (Kd → 1); the edge ramps over the [Ramp]
    /// time (falls back to one UI when no ramp). The Ku/Kd schedule spans the whole pattern
    /// (periodic — the last bit wraps to the first, matching the exact-periodic convention).</summary>
    public static IbisDriver FromBits(IbisModel model, IbisCornerSelection corner,
        IReadOnlyList<bool> bits, int samplesPerUi, double dt)
    {
        if (!model.IsOutput)
            throw new ArgumentException($"IBIS model '{model.Name}' is not an output buffer (needs [Pullup] and [Pulldown]).");
        double vcc = model.PullupRail;
        int n = bits.Count * samplesPerUi;
        // Edge sample count from the ramp slew: t_edge = swing / (Δv/Δt); clamp to [2, 1 UI].
        double swing = vcc;
        int edge = samplesPerUi;
        var ramp = model.Ramp;
        if (ramp is not null)
        {
            double dv = ramp.Rising.DeltaVolts.At(corner) ?? swing;
            double dtr = ramp.Rising.DeltaSeconds.At(corner) ?? dt;
            double slew = dv / dtr;                     // V/s
            if (slew > 0) edge = Math.Clamp((int)Math.Round(swing / slew / dt), 2, samplesPerUi);
        }
        var ku = new double[n];
        var kd = new double[n];
        for (int b = 0; b < bits.Count; b++)
        {
            int target = bits[b] ? 1 : 0;
            int prev = bits[(b - 1 + bits.Count) % bits.Count] ? 1 : 0;
            for (int s = 0; s < samplesPerUi; s++)
            {
                double frac = target;                    // steady level by default
                if (prev != target && s < edge)          // ramp across the edge
                    frac = prev + (target - prev) * (s + 1.0) / edge;
                ku[b * samplesPerUi + s] = frac;         // fraction pulled up
                kd[b * samplesPerUi + s] = 1 - frac;     // complementary pull-down
            }
        }
        return new IbisDriver(model, corner, vcc, dt, ku, kd);
    }

    public (double Current, double Conductance) Evaluate(double v, double t)
    {
        int n = (int)Math.Round(t / _dt);
        n = Math.Clamp(n, 0, _ku.Length - 1);
        double ku = _ku[n], kd = _kd[n];
        // IBIS into-pad current, then negate for into-line.
        var (ipu, gpu) = _pu.Eval(v - _vcc);
        var (ipd, gpd) = _pd.Eval(v);
        var (igc, ggc) = _gc.Eval(v);
        var (ipc, gpc) = _pc.Eval(v - _vcc);
        double iPad = ku * ipu + kd * ipd + igc + ipc;
        double gPad = ku * gpu + kd * gpd + ggc + gpc;   // dI/dV (V−Vcc and V share dV)
        return (-iPad, -gPad);
    }

    /// <summary>Monotone piecewise-linear V-I table with a value + slope, linear-extrapolated
    /// past both ends (so Newton never runs off a flat table).</summary>
    private sealed class Pwl
    {
        private readonly double[] _v, _i;
        private Pwl(double[] v, double[] i) { _v = v; _i = i; }

        public static Pwl FromTable(IReadOnlyList<IbisIvRow> table, IbisCornerSelection corner)
        {
            if (table.Count == 0) return new Pwl(Array.Empty<double>(), Array.Empty<double>());
            var pts = table.Select(r => (V: r.VoltageVolts, I: r.CurrentAmps.At(corner) ?? 0))
                           .OrderBy(p => p.V).ToArray();
            return new Pwl(pts.Select(p => p.V).ToArray(), pts.Select(p => p.I).ToArray());
        }

        public (double I, double G) Eval(double v)
        {
            int n = _v.Length;
            if (n == 0) return (0, 0);
            if (n == 1) return (_i[0], 0);
            int hi = 1;
            while (hi < n - 1 && _v[hi] < v) hi++;       // segment [hi-1, hi], extrapolate at ends
            double g = (_i[hi] - _i[hi - 1]) / (_v[hi] - _v[hi - 1]);
            return (_i[hi - 1] + g * (v - _v[hi - 1]), g);
        }
    }
}

/// <summary>The receiver termination the channel is loaded with (linear R∥C; open = R = ∞).
/// A nonlinear (clamped) receiver is a named follow-up — the channel is extracted with this
/// linear load.</summary>
public sealed record NonlinearReceiver(double LoadOhms, double LoadCapacitanceFarads = 0)
{
    public Complex Admittance(double frequencyHz)
    {
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
public static class NonlinearLink
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
                "The nonlinear driver engine handles a single driven line; multi-line nonlinear "
                + "crosstalk is a named follow-up (use the linear TransientLink for coupled cases).");
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
