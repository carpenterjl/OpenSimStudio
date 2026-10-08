using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Si.Ibis;

namespace OpenSim.Rf.Si;

/// <summary>A nonlinear buffer at the driver node: the current it pushes INTO the line at a
/// node voltage and time, plus that current's slope dI/dV (for the Newton step) and its die
/// capacitance C_comp (a linear shunt the engine accounts for itself, by the trapezoidal rule
/// inside its channel reduction).</summary>
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
/// switching coefficients Ku(t)/Kd(t) follow the bit stream through the measured waveform
/// tables, or failing those a linear ramp whose 0–100 % time is the [Ramp] dt divided by 0.6
/// (the ramp's dV is the 20 %–80 % part of the swing). An edge is not confined to its unit
/// interval: it runs until it settles or until the next transition takes over from wherever
/// it had got to. Pull-up / POWER-clamp tables are "Vcc relative" (voltage axis
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

    /// <summary>The pull-up and pull-down switching coefficients over one pattern period, one
    /// value per sample — what the bit stream was turned into.</summary>
    internal IReadOnlyList<double> PullupSchedule => _ku;
    internal IReadOnlyList<double> PulldownSchedule => _kd;

    /// <summary>The [Ramp] sub-parameters give dV/dt with dV defined as the 20 % to 80 % part
    /// of the swing into the ramp's test load, so the time for the whole swing along that
    /// straight line is dt / 0.6 — whatever the load and whatever the rail.</summary>
    internal const double RampSwingFraction = 0.6;

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
            throw new ArgumentException($"IBIS model '{model.Name}' is not an output buffer (needs "
                + "[Pullup] and [Pulldown], or the one it has when [Model_type] is an Open_* type).");
        if (!model.TypeIsDriverCapable)
            throw new ArgumentException(
                $"IBIS model '{model.Name}' declares [Model_type] {model.ModelType}, which cannot "
                + "drive a line, but carries [Pullup] and [Pulldown] tables. Refusing rather than "
                + "believing the tables over the file's own declaration — pick a driver model.");
        double vcc = model.PullupRailAt(corner);
        int n = bits.Count * samplesPerUi;
        // Edge sample count from [Ramp]: the whole-swing time is dt / 0.6 (see
        // RampSwingFraction). It was taken as Vcc / (dV/dt), which is the same thing only if
        // the swing into the test load reached the rail; a 25 Ω output into the default 50 Ω
        // swings two thirds of it and came out 1.5× slow. The edge is NOT clamped to one UI:
        // a buffer too slow for the bit rate has to show that, not be sped up to fit.
        int edgeRise = samplesPerUi, edgeFall = samplesPerUi;
        var ramp = model.Ramp;
        if (ramp is not null)
        {
            // Rising and falling edges have their OWN slews. [Ramp].Falling was parsed and then
            // never read, so every falling edge ran at the rising slew — invisible on a
            // symmetric buffer, a real timing error on an asymmetric one.
            int EdgeOf(IbisRampEdge e)
            {
                double? dtr = e.DeltaSeconds.At(corner);
                return dtr is > 0
                    ? Math.Max(2, (int)Math.Round(dtr.Value / RampSwingFraction / dt))
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

        WarnIfSlowerThanTheBit(riseKu, riseKd, "rising", samplesPerUi, dt, warnings);
        WarnIfSlowerThanTheBit(fallKu, fallKd, "falling", samplesPerUi, dt, warnings);

        // The schedule on the CONTINUOUS timeline. A transition starts its edge profile and the
        // profile then runs for as long as it is, across unit-interval boundaries; a held bit
        // simply lets it continue and, once it is exhausted, sits at its settled value (so the
        // steady level always agrees with the edge that reached it). A transition arriving
        // before the previous edge has settled takes over from where that edge had got to: the
        // new profile is entered at the point where its own drive balance Ku − Kd first passes
        // the present one. (The schedule used to be cut to one UI: a slower edge froze at
        // whatever it had reached at the UI boundary and the next edge restarted from the far
        // rail — a step in the drive.) Two passes, so the state the pattern ends in is the
        // state it starts from.
        var ku = new double[n];
        var kd = new double[n];
        bool level = bits[^1];
        var (activeKu, activeKd) = level ? (riseKu, riseKd) : (fallKu, fallKd);
        int position = activeKu.Length;                      // settled
        for (int pass = 0; pass < 2; pass++)
            for (int b = 0; b < bits.Count; b++)
            {
                if (bits[b] != level)
                {
                    // Where the drive IS: the last sample played (the settled end when the
                    // profile has run out).
                    int at = Math.Clamp(position - 1, 0, activeKu.Length - 1);
                    double balance = activeKu[at] - activeKd[at];
                    double settled = activeKu[^1] - activeKd[^1];
                    level = bits[b];
                    (activeKu, activeKd) = level ? (riseKu, riseKd) : (fallKu, fallKd);
                    position = Math.Abs(balance - settled) <= SettledBalance
                        ? 0
                        : EntryIndex(activeKu, activeKd, balance, rising: level);
                }
                for (int s = 0; s < samplesPerUi; s++, position++)
                {
                    int at = Math.Min(position, activeKu.Length - 1);
                    ku[b * samplesPerUi + s] = activeKu[at];
                    kd[b * samplesPerUi + s] = activeKd[at];
                }
            }
        switchingSource = riseSource == fallSource
            ? riseSource
            : $"rising: {riseSource}; falling: {fallSource}";
        return new IbisDriver(model, corner, vcc, dt, ku, kd);
    }

    /// <summary>An edge counts as settled when its drive balance Ku − Kd (which runs from −1 to
    /// +1) is within this of its final value: 2 % of the span.</summary>
    private const double SettledBalance = 0.04;

    /// <summary>Where to enter an edge profile so that it continues from the present drive
    /// balance: the first sample that has moved strictly past it in the edge's direction (the
    /// end of the profile when it never does — the buffer is already beyond that edge).</summary>
    private static int EntryIndex(double[] ku, double[] kd, double balance, bool rising)
    {
        for (int j = 0; j < ku.Length; j++)
        {
            double here = ku[j] - kd[j];
            if (rising ? here > balance : here < balance) return j;
        }
        return ku.Length - 1;
    }

    private static void WarnIfSlowerThanTheBit(double[] ku, double[] kd, string edge,
        int samplesPerUi, double dt, List<string> warnings)
    {
        double final = ku[^1] - kd[^1];
        int settle = 0;
        for (int j = 0; j < ku.Length; j++)
            if (Math.Abs(ku[j] - kd[j] - final) > SettledBalance) settle = j + 1;
        if (settle <= samplesPerUi) return;
        warnings.Add($"The buffer's {edge} edge takes {settle * dt * 1e9:g3} ns to settle, longer "
            + $"than the {samplesPerUi * dt * 1e9:g3} ns unit interval: an isolated bit does not "
            + "reach its full level before the next transition, and consecutive edges overlap.");
    }

    /// <summary>One edge's Ku/Kd from its start until it has settled: the measured two-/one-
    /// waveform extraction when the model carries waveforms for that edge (over the whole span
    /// of its tables, and at least one UI), else the [Ramp] line over its own edge time.</summary>
    private static (double[] Ku, double[] Kd, string Source) EdgeProfile(
        IbisModel model, IbisCornerSelection corner, IReadOnlyList<IbisWaveform> waveforms,
        bool rising, double dt, int samplesPerUi, int edge, int from, int to,
        List<string> warnings)
    {
        if (waveforms.Count is 1 or 2)
        {
            try
            {
                double span = 0;
                foreach (var waveform in waveforms)
                    if (waveform.Rows.Count > 0)
                        span = Math.Max(span, waveform.Rows.Max(r => r.TimeSeconds)
                                              - waveform.Rows.Min(r => r.TimeSeconds));
                int samples = Math.Max(samplesPerUi,
                    (int)Math.Min(1 << 20, Math.Floor(span / dt * (1 + 1e-12))));
                var sched = KuKdExtractor.Extract(model, corner, waveforms, rising, dt, samples);
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

        var ku = new double[edge];
        var kd = new double[edge];
        for (int s = 0; s < edge; s++)
        {
            double frac = from + (to - from) * (s + 1.0) / edge;
            ku[s] = frac;
            kd[s] = 1 - frac;
        }
        return (ku, kd, "[Ramp] slew (no usable waveform tables; a straight line over dt/0.6)");
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

/// <summary>The receiver termination for the SINGLE-LINE entry (linear R∥C; open = R = ∞). It
/// becomes a <see cref="LinearLoadElement"/> at the far port of the reference-terminated
/// reduction. A nonlinear (clamped) receiver goes through <see cref="NonlinearLink.SolveNPort"/>
/// directly, with an <see cref="IbisReceiverElement"/> there.</summary>
public sealed record NonlinearReceiver(double LoadOhms, double LoadCapacitanceFarads = 0)
{
    public Complex Admittance(double frequencyHz)
    {
        // A shorted far end has no finite node voltage to solve for, so it is refused here
        // rather than producing Infinity (and, from there, NaN samples the Newton loop would
        // blame on a non-monotone table). MtlNetwork.SolveTerminated carries the impedance-form
        // row that CAN express a short; the linear transient path reaches it directly.
        if (LoadOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(LoadOhms),
                $"Receiver resistance must be positive (got {LoadOhms} Ω). The nonlinear "
                + "driver engine solves the far end as a node; for a shorted far end use the "
                + "linear TransientLink path, which solves the short exactly.");
        double omega = 2 * Math.PI * frequencyHz;
        Complex y = double.IsPositiveInfinity(LoadOhms) ? Complex.Zero : 1.0 / LoadOhms;
        return y + new Complex(0, omega * LoadCapacitanceFarads);
    }
}

/// <summary>The result of a nonlinear link solve: one steady-state period at the driver node
/// and the receiver, plus the channel memory (truncated FIR length), its tail-energy bound, the
/// warm-up periods actually run and how far the reported period still moved from the one before
/// (see <see cref="NonlinearNPortResult"/>).</summary>
public sealed record NonlinearResult(
    double SampleIntervalSeconds, double[] DriverVolts, double[] ReceiverVolts,
    int ChannelMemorySamples, double TailEnergyFraction, int WarmupPeriods,
    double SettlingResidualVolts);

/// <summary>
/// The Stage S11 nonlinear transient engine: a NONLINEAR driver into a LINEAR channel with a
/// linear R∥C receiver. This entry is the single-line case of <see cref="SolveNPort"/>: the
/// receiver becomes a <see cref="LinearLoadElement"/> at the far port and both ends are
/// unknowns of the reference-terminated reduction.
///
/// <para><b>Why it is not reduced on its own any more (SI-15).</b> It used to fold the
/// receiver into two FIRs taken with the near end VOLTAGE-FORCED: the driving-point admittance
/// Y_in(ω) and the transfer H(ω) = V_far/V_near. With the near end forced, the near-end
/// reflection coefficient is −1; with an open far end it is +1; so on a low-loss line those
/// responses decay only through the line loss, and on a lossless one never — Y_in has its poles
/// on the real-frequency axis. Sampled on a finite DFT they wrapped around the window, and an
/// open lossless line did not even solve. The reference-terminated reduction loads every port
/// with g_ref while it is being characterised, so each echo loses (1 − |Γ_ref|) at the near end
/// and the responses decay in a few round trips whatever the far end is.</para>
///
/// <para>The engine does NOT claim the exact-periodic identity the linear <see cref="TransientLink"/>
/// holds — a nonlinear system has no closed-form periodic answer — but a LINEAR driver reduces
/// it to that engine (gated).</para>
/// </summary>
public static partial class NonlinearLink
{
    public static NonlinearResult Solve(MtlNetwork network, INonlinearDriver driver,
        NonlinearReceiver receiver, IReadOnlyList<bool> bits, int samplesPerUi,
        double sampleIntervalSeconds, int warmupPeriods = 4, double tailEnergyBound = 1e-4,
        int? maxDegreeOfParallelism = null)
    {
        if (network.ConductorCount != 1)
            throw new ArgumentException(
                "This entry takes ONE line with a linear receiver. For coupled lines — or for a "
                + "nonlinear (clamped) receiver — use " + nameof(SolveNPort) + ", which takes an "
                + "element at every port.");
        if (samplesPerUi < 2) throw new ArgumentOutOfRangeException(nameof(samplesPerUi));
        receiver.Admittance(0);                              // refuses a short, as it always has
        var load = new LinearLoadElement(receiver.LoadOhms, receiver.LoadCapacitanceFarads);

        var result = SolveNPort(network, new[] { driver }, new INonlinearDriver[] { load },
            bits.Count * samplesPerUi, sampleIntervalSeconds, warmupPeriods, tailEnergyBound,
            maxDegreeOfParallelism: maxDegreeOfParallelism);
        return new NonlinearResult(sampleIntervalSeconds, result.NearVolts[0], result.FarVolts[0],
            result.ChannelMemorySamples, result.TailEnergyFraction, result.WarmupPeriods,
            result.SettlingResidualVolts);
    }
}