using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpenSim.Rf.Emi;

/// <summary>
/// The line impedance stabilisation network as it is commonly drawn for CISPR 16-1-2's
/// 50 Ω / 50 µH + 5 Ω V-network: from the equipment terminal, 50 µH to the mains side, which is
/// held to earth by 1 µF in series with 5 Ω; and from the terminal to earth, 0.1 µF into the
/// 50 Ω receiver in parallel with a 1 kΩ discharge resistor. The values are those of the common
/// reproductions (the standard's text was not on this machine) and are public so they can be
/// changed.
/// </summary>
public sealed record LisnModel(double SeriesInductance = 50e-6, double MainsCapacitance = 1e-6, double MainsResistance = 5,
    double CouplingCapacitance = 0.1e-6, double ReceiverOhms = 50, double DischargeOhms = 1000)
{
    /// <summary>Adds one LISN to <paramref name="circuit"/> for the line at <paramref name="eutNode"/>;
    /// the receiver voltage is node "<paramref name="prefix"/>.rx" to ground.</summary>
    public void AddTo(AcCircuit circuit, string eutNode, string prefix)
    {
        string mains = prefix + ".mains", mid = prefix + ".mid", rx = prefix + ".rx";
        circuit.Inductor(prefix + ".L", eutNode, mains, SeriesInductance);
        circuit.Capacitor(prefix + ".Cm", mains, mid, MainsCapacitance);
        circuit.Resistor(prefix + ".Rm", mid, "0", MainsResistance);
        circuit.Capacitor(prefix + ".Cc", eutNode, rx, CouplingCapacitance);
        circuit.Resistor(prefix + ".Rrx", rx, "0", ReceiverOhms);
        circuit.Resistor(prefix + ".Rd", rx, "0", DischargeOhms);
    }
}

/// <summary>The input side of a switching converter: what makes the noise and what holds it in.</summary>
/// <param name="SwitchCurrent">The current the switch draws from the input rails (a buck: the
/// load current in pulses at the duty cycle) — the differential-mode source.</param>
/// <param name="SwitchNodeVoltage">The switch node against the input return — drives the
/// common-mode current through <see cref="SwitchNodeCapacitance"/>.</param>
/// <param name="SwitchNodeCapacitance">Switch node to earth — a heatsink, the chassis — farads, a
/// measured or estimated value: the common-mode source's path. The net's capacitance to the
/// board's own planes is not it (that is <see cref="SwitchNodeToReturnCapacitance"/>, read from
/// the board); a ground plane under the switch node shields it from earth.</param>
/// <param name="InputCapacitance">The converter's input capacitor, with its ESR and ESL; add the
/// mounting loop's inductance (Feature 7's loop extraction) to the ESL.</param>
public sealed record ConverterInput(
    PiecewiseLinearWaveform SwitchCurrent, PiecewiseLinearWaveform SwitchNodeVoltage, double SwitchNodeCapacitance,
    double InputCapacitance, double InputEsr, double InputEsl)
{
    /// <summary>An optional differential filter between the input capacitor and the LISNs:
    /// a series inductor in the line (with its DCR) and a capacitor across the input.</summary>
    public double FilterInductance { get; init; }
    public double FilterResistance { get; init; }
    public double FilterCapacitance { get; init; }
    public double FilterCapacitorEsr { get; init; }
    public double FilterCapacitorEsl { get; init; }

    /// <summary>Line-to-earth capacitors (Y capacitors) at the input, each line, farads.</summary>
    public double YCapacitance { get; init; }

    // ---------------------------------------------------------------- the hot loop and its ringing

    /// <summary>Inductance of the hot loop — input capacitor, high-side switch, low-side switch (or
    /// diode) and back — henries: <see cref="ConverterBoard"/> reads it from the board, or the
    /// loop extraction does. With <see cref="SwitchNodeToReturnCapacitance"/> it sets the ringing
    /// each turn-on leaves on the switch node and in the loop; 0 leaves ringing out.</summary>
    public double HotLoopInductance { get; init; }

    /// <summary>Resistance of the hot loop at the ringing frequency, ohms (the copper's, plus the
    /// switches' on-resistance): damps the ring. 0 is an undamped ring.</summary>
    public double HotLoopResistance { get; init; }

    /// <summary>Capacitance of the switch node to the converter's own return — the switches'
    /// output capacitance plus the net's capacitance to the ground plane (read from the board) —
    /// farads. It resonates with the hot loop; it does not carry common-mode current.</summary>
    public double SwitchNodeToReturnCapacitance { get; init; }

    /// <summary>Reverse-recovery charge of the low-side diode (or the synchronous switch's body
    /// diode), coulombs: swept out at each turn-on as a current pulse in the hot loop, and the
    /// energy it stores in the loop's inductance rings the switch node.</summary>
    public double ReverseRecoveryCharge { get; init; }

    /// <summary>The recovery's softness t_b/t_a: how long the current takes to return to zero
    /// against how long it took to reach its peak. 1 is a triangle with equal slopes; a snappy
    /// diode is near 0.</summary>
    public double RecoverySoftness { get; init; } = 1;

    // ---------------------------------------------------------------- a transformer's interwinding path

    /// <summary>Capacitance from the primary switch node to the secondary winding, farads (an
    /// isolated converter): the switch-node swing drives it, and what passes reaches earth
    /// through <see cref="SecondaryToEarthCapacitance"/> or comes back through
    /// <see cref="SecondaryYCapacitance"/>. 0 for a non-isolated converter.</summary>
    public double InterwindingCapacitance { get; init; }

    /// <summary>The secondary side's capacitance to earth (its output, load and cable against the
    /// chassis), farads.</summary>
    public double SecondaryToEarthCapacitance { get; init; }

    /// <summary>A Y capacitor across the isolation barrier, secondary to the primary return, farads:
    /// the usual way back for the interwinding current.</summary>
    public double SecondaryYCapacitance { get; init; }

    /// <summary>Area of the hot loop [m²] when the board was read (for the radiated estimate); 0
    /// when unknown.</summary>
    public double HotLoopAreaM2 { get; init; }

    /// <summary>A buck converter's input: the switch draws I_out while on (edges as the voltage's),
    /// and the switch node swings 0 → V_in.</summary>
    public static ConverterInput Buck(double vIn, double iOut, double frequencyHz, double duty, double rise, double fall,
        double switchNodeCapacitance, double inputCapacitance, double esr, double esl) =>
        new(PiecewiseLinearWaveform.Trapezoid(iOut, frequencyHz, duty, rise, fall),
            PiecewiseLinearWaveform.Trapezoid(vIn, frequencyHz, duty, rise, fall),
            switchNodeCapacitance, inputCapacitance, esr, esl);

    /// <summary>The diode's recovery pulse at each turn-on, from <see cref="ReverseRecoveryCharge"/>
    /// and the switch current's own edge; null when no charge is given or the current has no
    /// rising edge.</summary>
    public DiodeRecovery? Recovery()
    {
        if (!(ReverseRecoveryCharge > 0) || SwitchCurrent.SteepestRise is not { } rise || !(rise.Step > 0)) return null;
        if (!(RecoverySoftness >= 0)) throw new ArgumentOutOfRangeException(nameof(RecoverySoftness));
        return DiodeRecovery.Of(ReverseRecoveryCharge, rise.Step / rise.Duration, RecoverySoftness, rise.EndTime, SwitchCurrent.Period);
    }

    /// <summary>The ring each turn-on leaves on the switch node and in the hot loop; null when the
    /// loop's inductance or the node's capacitance is not given, or the loop is overdamped.</summary>
    public SwitchingRing? Ringing()
    {
        if (!(HotLoopInductance > 0) || !(SwitchNodeToReturnCapacitance > 0)) return null;
        if (!(HotLoopResistance > 0))
            throw new ArgumentException("The hot loop's resistance is needed to damp its ring: the copper's at the ringing frequency plus the switches' on-resistance.");
        if (SwitchNodeVoltage.SteepestRise is not { } edge || !(edge.Step > 0)) return null;
        return SwitchingRing.Of(HotLoopInductance, HotLoopResistance, SwitchNodeToReturnCapacitance,
            edge.Step, edge.Duration, edge.EndTime, Recovery()?.PeakAmps ?? 0, SwitchCurrent.Period);
    }

    /// <summary>The two sources as the estimate uses them: the ideal waveforms with the diode's
    /// recovery pulse added to the current and the ring added to both.</summary>
    public (IPeriodicWaveform Current, IPeriodicWaveform Voltage) Sources()
    {
        var current = new List<IPeriodicWaveform> { SwitchCurrent };
        var voltage = new List<IPeriodicWaveform> { SwitchNodeVoltage };
        if (Recovery() is { } recovery) current.Add(recovery.Waveform);
        if (Ringing() is { } ring)
        {
            current.Add(ring.LoopCurrent);
            voltage.Add(ring.NodeVoltage);
        }
        return (current.Count == 1 ? SwitchCurrent : new CompositeWaveform(current),
            voltage.Count == 1 ? SwitchNodeVoltage : new CompositeWaveform(voltage));
    }

    /// <summary>What of the switching-edge physics is in the sources, and what is not.</summary>
    public IReadOnlyList<string> SourceNotes()
    {
        var notes = new List<string>();
        var recovery = Recovery();
        var ring = Ringing();
        notes.Add(recovery is null
            ? "No diode recovery in the switch current (give the reverse-recovery charge to add it)."
            : recovery.Describe());
        if (ring is not null) notes.Add(ring.Describe());
        else if (HotLoopInductance > 0 && SwitchNodeToReturnCapacitance > 0)
            notes.Add($"The hot loop ({HotLoopInductance * 1e9:g3} nH, {HotLoopResistance:g3} Ω) with {SwitchNodeToReturnCapacitance * 1e12:g3} pF "
                + "on the switch node is overdamped: no ring.");
        else
            notes.Add("No ringing on the switch node (give the hot-loop inductance and the switch node's capacitance to the return to add it).");
        if (ring is not null || recovery is not null)
            notes.Add("Edge model: the ring and the recovery pulse are added at the turn-on edge only (at turn-off the low side clamps the node); "
                + "the ring starts where the voltage edge ends, at zero phase, with the recovery energy and the edge's own excitation added in quadrature; "
                + "the loop current ring is C·dv/dt of the node ring.");
        return notes;
    }
}

/// <summary>
/// A diode's reverse recovery as a current pulse in the hot loop: the switch current keeps
/// rising at the edge's di/dt past the load current until the charge Q_rr is swept out, then
/// returns to zero. Taken as a triangle — up at di/dt for t_a to the peak I_rr, down for
/// t_b = S·t_a — so that its area is Q_rr: ½·I_rr·(t_a + t_b) = Q_rr with t_a = I_rr/(di/dt)
/// gives I_rr = √(2·Q_rr·(di/dt)/(1 + S)).
/// </summary>
public sealed record DiodeRecovery(double PeakAmps, double RiseSeconds, double FallSeconds, double StartTime,
    PiecewiseLinearWaveform Waveform)
{
    public double ChargeCoulombs => 0.5 * PeakAmps * (RiseSeconds + FallSeconds);

    public static DiodeRecovery Of(double chargeCoulombs, double currentSlopeAmpsPerSecond, double softness, double startTime, double period)
    {
        if (!(currentSlopeAmpsPerSecond > 0)) throw new ArgumentOutOfRangeException(nameof(currentSlopeAmpsPerSecond));
        double peak = Math.Sqrt(2 * chargeCoulombs * currentSlopeAmpsPerSecond / (1 + softness));
        double ta = peak / currentSlopeAmpsPerSecond, tb = softness * ta;
        if (startTime + ta + tb >= period)
            throw new ArgumentException($"The recovery pulse ({(ta + tb) * 1e9:g3} ns from {startTime * 1e9:g3} ns) does not fit in the period.");
        var points = new List<(double, double)>();
        if (startTime > 0) points.Add((0.0, 0.0));
        points.Add((startTime, 0.0));
        points.Add((startTime + ta, peak));
        if (tb > 0) points.Add((startTime + ta + tb, 0.0));
        else points.Add((startTime + ta + 1e-3 * ta, 0.0));                     // a snappy fall, resolved by the Fourier sum
        return new DiodeRecovery(peak, ta, tb, startTime, new PiecewiseLinearWaveform(period, points));
    }

    public string Describe() =>
        $"Diode recovery: {ChargeCoulombs * 1e9:g3} nC swept out at each turn-on as a {PeakAmps:g3} A peak lasting {(RiseSeconds + FallSeconds) * 1e9:g3} ns "
        + $"(I_rr = √(2·Q_rr·di/dt/(1 + S)), S = {(RiseSeconds > 0 ? FallSeconds / RiseSeconds : 0):g3}).";
}

/// <summary>
/// The ring a turn-on edge leaves: the hot loop's inductance L and resistance R with the switch
/// node's capacitance C to the return form a series RLC, ω₀ = 1/√(LC), α = R/2L, ringing at
/// ω_d = √(ω₀² − α²) with Z₀ = √(L/C). Two things excite it: the edge itself — an undamped LC
/// fed by a ramp of V over t_r rings afterwards with amplitude V·|sin(ω₀t_r/2)|/(ω₀t_r/2) — and
/// the recovery current I_rr left in the loop when the diode snaps off, whose energy ½LI_rr²
/// becomes ½CV² (amplitude I_rr·Z₀). The two are a quarter period apart and are added in
/// quadrature. The node voltage ring is V_os·e^{−αt}·sin(ω_d t) from the end of the voltage
/// edge; the loop current ring is its C·dv/dt, C·ω_d·V_os·e^{−αt}·cos(ω_d t).
/// </summary>
public sealed record SwitchingRing(double FrequencyHz, double DecayPerSecond, double CharacteristicOhms,
    double RampOvershootVolts, double RecoveryOvershootVolts, double StartTime,
    DampedSinusoidTrain NodeVoltage, DampedSinusoidTrain LoopCurrent)
{
    public double OvershootVolts => NodeVoltage.Amplitude;
    public double Quality => Math.PI * FrequencyHz / DecayPerSecond;

    public static SwitchingRing? Of(double inductance, double resistance, double capacitance, double voltageStep,
        double edgeDuration, double edgeEnd, double recoveryPeakAmps, double period)
    {
        double w0 = 1 / Math.Sqrt(inductance * capacitance);
        double alpha = resistance / (2 * inductance);
        if (alpha >= w0) return null;
        double wd = Math.Sqrt(w0 * w0 - alpha * alpha);
        double z0 = Math.Sqrt(inductance / capacitance);
        double x = 0.5 * w0 * edgeDuration;
        double ramp = voltageStep * Math.Abs(x < 1e-9 ? 1 : Math.Sin(x) / x);
        double recovery = recoveryPeakAmps * z0;
        double overshoot = Math.Sqrt(ramp * ramp + recovery * recovery);
        // A ring that has not decayed within a period is still exact in the lines (the periodic
        // sum is in the closed form); R > 0 is required, since an undamped ring has no lines.
        return new SwitchingRing(wd / (2 * Math.PI), alpha, z0, ramp, recovery, edgeEnd,
            new DampedSinusoidTrain(period, edgeEnd, overshoot, alpha, wd),
            new DampedSinusoidTrain(period, edgeEnd, capacitance * wd * overshoot, alpha, wd, Math.PI / 2));
    }

    public string Describe() =>
        $"Ringing at {FrequencyHz / 1e6:g4} MHz (Q {Quality:g3}, Z₀ {CharacteristicOhms:g3} Ω): {OvershootVolts:g3} V on the switch node "
        + $"({RampOvershootVolts:g3} V from the edge, {RecoveryOvershootVolts:g3} V from the recovery current), "
        + $"{LoopCurrent.Amplitude:g3} A in the hot loop.";
}

/// <summary>One spectral line of the estimate, receiver voltages in dBµV (peak of the line).</summary>
public sealed record EmissionLine(double FrequencyHz, double LineDbuV, double NeutralDbuV, double DifferentialDbuV,
    double CommonDbuV, double? LimitDbuV)
{
    public double WorstDbuV => Math.Max(LineDbuV, NeutralDbuV);
    public double? MarginDb => LimitDbuV is { } l ? l - WorstDbuV : null;
}

public sealed record ConductedEmissionResult(IReadOnlyList<EmissionLine> Lines, EmiLimitLine? Limit, IReadOnlyList<string> Assumptions)
{
    /// <summary>The line closest to (or furthest over) the limit.</summary>
    public EmissionLine? Worst => Lines.Where(l => l.MarginDb is not null).MinBy(l => l.MarginDb!.Value);

    public string ToCsv()
    {
        var sb = new StringBuilder("frequency_Hz,line_dBuV,neutral_dBuV,differential_dBuV,common_dBuV,limit_dBuV,margin_dB\n");
        foreach (var l in Lines)
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{l.FrequencyHz},{l.LineDbuV:F2},{l.NeutralDbuV:F2},{l.DifferentialDbuV:F2},{l.CommonDbuV:F2},{(l.LimitDbuV is { } x ? x.ToString("F2", CultureInfo.InvariantCulture) : "")},{(l.MarginDb is { } m ? m.ToString("F2", CultureInfo.InvariantCulture) : "")}\n"));
        return sb.ToString();
    }

    public string Describe()
    {
        var lines = new List<string>();
        if (Worst is { } w)
            lines.Add(w.MarginDb >= 0
                ? $"Lowest margin {w.MarginDb:F1} dB at {w.FrequencyHz / 1e6:g4} MHz ({w.WorstDbuV:F1} dBµV against {w.LimitDbuV:F1}, {Limit!.Name})."
                : $"OVER the limit by {-w.MarginDb:F1} dB at {w.FrequencyHz / 1e6:g4} MHz ({w.WorstDbuV:F1} dBµV against {w.LimitDbuV:F1}, {Limit!.Name}).");
        int over = Lines.Count(l => l.MarginDb < 0);
        if (Limit is not null) lines.Add($"{over} of {Lines.Count(l => l.MarginDb is not null)} lines over the limit.");
        var top = Lines.OrderByDescending(l => l.WorstDbuV).FirstOrDefault();
        if (top is not null)
            lines.Add($"Strongest line {top.WorstDbuV:F1} dBµV at {top.FrequencyHz / 1e6:g4} MHz — "
                + (top.DifferentialDbuV >= top.CommonDbuV ? "differential mode dominates." : "common mode dominates."));
        lines.AddRange(Assumptions);
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Conducted-emission estimate for a switching converter on two LISNs: every harmonic of the
/// switch current and switch-node voltage drives the input network (input capacitor, optional
/// filter and Y capacitors, the two LISNs) at its own frequency, both sources at once with their
/// true phases, and the receivers' voltages are read in dBµV. Each line is also solved with one
/// source at a time to split differential from common mode.
/// </summary>
public static class ConductedEmission
{
    public static IReadOnlyList<string> Assumptions { get; } = new[]
    {
        "Pre-compliance estimate: each spectral line's amplitude (what a peak detector with a 9 kHz bandwidth reads when the switching frequency is above 9 kHz). Quasi-peak and average readings of a steady line are not higher, so comparing with the quasi-peak limit is conservative; against the average limit it is not.",
        "Linear network: the converter is two ideal sources (switch current, switch-node voltage) with the turn-on ring and the diode's recovery added to them when their parameters are given; the inductor's own capacitance is not in it.",
        "LISN as commonly drawn for the 50 Ω/50 µH + 5 Ω V-network; limit lines " + EmiLimits.Unverified + ".",
    };

    /// <summary>The assumptions for one converter: the general ones, its edge model and its
    /// common-mode paths.</summary>
    public static IReadOnlyList<string> AssumptionsFor(ConverterInput input)
    {
        var list = Assumptions.ToList();
        list.AddRange(input.SourceNotes());
        var paths = new List<string>();
        if (input.SwitchNodeCapacitance > 0) paths.Add($"the switch node's {input.SwitchNodeCapacitance * 1e12:g3} pF to earth");
        if (input.InterwindingCapacitance > 0)
            paths.Add($"the transformer's {input.InterwindingCapacitance * 1e12:g3} pF interwinding capacitance to the secondary, which has "
                + string.Join(" and ", new[]
                {
                    input.SecondaryToEarthCapacitance > 0 ? $"{input.SecondaryToEarthCapacitance * 1e12:g3} pF to earth" : null,
                    input.SecondaryYCapacitance > 0 ? $"a {input.SecondaryYCapacitance * 1e12:g3} pF Y capacitor to the primary return" : null
                }.Where(s => s is not null)));
        list.Add(paths.Count == 0
            ? "No common-mode path: the switch node has no capacitance to earth and no interwinding path."
            : "Common-mode paths: " + string.Join("; ", paths) + ". Other paths (heatsinks of other parts, the output cable of a non-isolated converter) are not modelled.");
        return list;
    }

    public static ConductedEmissionResult Estimate(ConverterInput input, EmiLimitLine? limit = null, LisnModel? lisn = null,
        double minFrequencyHz = 150e3, double maxFrequencyHz = 30e6)
    {
        lisn ??= new LisnModel();
        if (Math.Abs(input.SwitchCurrent.Period - input.SwitchNodeVoltage.Period) > 1e-12 * input.SwitchCurrent.Period)
            throw new ArgumentException("The switch current and switch-node voltage must share one period.");
        var circuit = Build(input, lisn);
        var (current, voltage) = input.Sources();
        double fundamental = 1 / current.Period;
        var lines = new List<EmissionLine>();
        int first = Math.Max(1, (int)Math.Ceiling(minFrequencyHz * current.Period - 1e-9));
        for (int n = first; n * fundamental <= maxFrequencyHz * (1 + 1e-12); n++)
        {
            double f = n * fundamental;
            var iN = 2 * current.Coefficient(n);
            var vN = 2 * voltage.Coefficient(n);
            var both = circuit.Solve(f, new Dictionary<string, Complex> { ["Idm"] = iN, ["Vsw"] = vN });
            var dm = circuit.Solve(f, new Dictionary<string, Complex> { ["Idm"] = iN });
            var cm = circuit.Solve(f, new Dictionary<string, Complex> { ["Vsw"] = vN });
            // Differential mode: half the difference of the two receivers; common: half the sum.
            double Db(Complex v) => 20 * Math.Log10(Math.Max(v.Magnitude, 1e-15) / 1e-6);
            lines.Add(new EmissionLine(f, Db(both["L.rx"]), Db(both["N.rx"]),
                Db(0.5 * (dm["L.rx"] - dm["N.rx"])), Db(0.5 * (cm["L.rx"] + cm["N.rx"])), limit?.At(f)));
        }
        return new ConductedEmissionResult(lines, limit, AssumptionsFor(input));
    }

    /// <summary>The common-mode current on the cable: what both LISNs together pass to earth
    /// (through their 5 Ω, receiver and discharge resistors — the LISN's only earth returns), so
    /// every common-mode path of the converter is in it. Positive from the LISNs into earth, i.e.
    /// minus the current the converter's paths push into earth.</summary>
    internal static Complex CableCommonModeCurrent(AcSolution solution, LisnModel lisn)
    {
        Complex sum = Complex.Zero;
        foreach (string prefix in new[] { "L", "N" })
            sum += solution[prefix + ".mid"] / lisn.MainsResistance
                + solution[prefix + ".rx"] * (1 / lisn.ReceiverOhms + 1 / lisn.DischargeOhms);
        return sum;
    }

    /// <summary>The input network: the converter's rails "p" (+) and "n" (−); the switch draws the
    /// DM current from p to n; the switch node "sw" sits at V_sw above n and couples to earth
    /// through C_sw and, through a transformer's interwinding capacitance, to the secondary "sec"
    /// (which has its own capacitance to earth and a Y capacitor back to n); the input capacitor
    /// across p–n; then the optional filter, Y capacitors and the line ("L") and neutral ("N")
    /// LISNs.</summary>
    internal static AcCircuit Build(ConverterInput input, LisnModel lisn)
    {
        var c = new AcCircuit();
        // The sources' amplitudes are given per harmonic at each solve.
        c.Current("Idm", "p", "n", _ => Complex.Zero);
        c.Voltage("Vsw", "sw", "n", _ => Complex.Zero);
        if (input.SwitchNodeCapacitance > 0) c.Capacitor("Csw", "sw", "0", input.SwitchNodeCapacitance);
        if (input.InterwindingCapacitance > 0)
        {
            if (!(input.SecondaryToEarthCapacitance > 0) && !(input.SecondaryYCapacitance > 0))
                throw new ArgumentException("The interwinding capacitance needs a way on from the secondary: its capacitance to earth, a Y capacitor to the primary return, or both.");
            c.Capacitor("Ciw", "sw", "sec", input.InterwindingCapacitance);
            if (input.SecondaryToEarthCapacitance > 0) c.Capacitor("Csec", "sec", "0", input.SecondaryToEarthCapacitance);
            if (input.SecondaryYCapacitance > 0) c.Capacitor("Cy.iso", "sec", "n", input.SecondaryYCapacitance);
        }
        c.Capacitor("Cin", "p", "n", input.InputCapacitance, input.InputEsr, input.InputEsl);
        string line = "p", neutral = "n";
        if (input.FilterInductance > 0)
        {
            c.Inductor("Lf", "p", "pf", input.FilterInductance, input.FilterResistance);
            line = "pf";
            if (input.FilterCapacitance > 0)
                c.Capacitor("Cf", "pf", "n", input.FilterCapacitance, input.FilterCapacitorEsr, input.FilterCapacitorEsl);
        }
        if (input.YCapacitance > 0)
        {
            c.Capacitor("Cy.L", line, "0", input.YCapacitance);
            c.Capacitor("Cy.N", neutral, "0", input.YCapacitance);
        }
        lisn.AddTo(c, line, "L");
        lisn.AddTo(c, neutral, "N");
        return c;
    }
}

/// <summary>
/// First-order radiated estimates from currents, for COMPARING layouts and fixes rather than
/// predicting a chamber reading (the commercial tools' absolute accuracy here is poor too):
/// <list type="bullet">
/// <item>a small current loop of area A (a converter's hot loop): |E| = η₀k²AI/(4πr) broadside in
/// free space, times 2 for a reflecting ground — 1.316·10⁻¹⁴·f²AI/r without it;</item>
/// <item>a common-mode current on a short cable of length ℓ: |E| = η₀kℓI/(4πr), times 2 for the
/// ground — 6.28·10⁻⁷·fℓI/r without it, valid while ℓ ≪ λ/4; above that the cable is taken as
/// a quarter-wave at most (ℓ capped at λ/4), the usual bound.</item>
/// </list>
/// </summary>
public static class RadiatedEstimate
{
    private const double Eta0 = 376.730313668;
    private const double C0 = 299792458;

    public static double LoopField(double frequencyHz, double areaM2, double currentA, double distanceM, bool groundReflection = true)
    {
        double k = 2 * Math.PI * frequencyHz / C0;
        return (groundReflection ? 2 : 1) * Eta0 * k * k * areaM2 * currentA / (4 * Math.PI * distanceM);
    }

    public static double CableField(double frequencyHz, double lengthM, double currentA, double distanceM, bool groundReflection = true)
    {
        double lambda = C0 / frequencyHz;
        double l = Math.Min(lengthM, lambda / 4);
        double k = 2 * Math.PI / lambda;
        return (groundReflection ? 2 : 1) * Eta0 * k * l * currentA / (4 * Math.PI * distanceM);
    }

    public static double DbuVPerM(double voltsPerMetre) => 20 * Math.Log10(Math.Max(voltsPerMetre, 1e-15) / 1e-6);

    /// <summary>Radiated lines of a converter: the hot-loop current is the input capacitor's
    /// current (the switch current, with its recovery pulse and ring, minus what the input
    /// draws), the cable current is the common-mode current the LISNs pass to earth (every
    /// common-mode path together), both per harmonic, against an optional radiated limit. The
    /// loop area is the board's (<see cref="ConverterInput.HotLoopAreaM2"/>) when
    /// <paramref name="loopAreaM2"/> is 0.</summary>
    public static IReadOnlyList<(double FrequencyHz, double LoopDbuVm, double CableDbuVm, double? LimitDbuVm)> Converter(
        ConverterInput input, double loopAreaM2, double cableLengthM, double distanceM, EmiLimitLine? limit = null,
        double minFrequencyHz = 30e6, double maxFrequencyHz = 1e9, LisnModel? lisn = null)
    {
        lisn ??= new LisnModel();
        if (!(loopAreaM2 > 0)) loopAreaM2 = input.HotLoopAreaM2;
        if (!(loopAreaM2 > 0)) throw new ArgumentException("The hot loop's area is needed: give it, or read the board.", nameof(loopAreaM2));
        var circuit = ConductedEmission.Build(input, lisn);
        var result = new List<(double, double, double, double?)>();
        var (current, voltage) = input.Sources();
        double fundamental = 1 / current.Period;
        int first = Math.Max(1, (int)Math.Ceiling(minFrequencyHz * current.Period - 1e-9));
        int last = (int)Math.Floor(maxFrequencyHz * current.Period + 1e-9);
        // Above a few hundred lines per decade the estimate is a smooth envelope; sample it.
        int stride = Math.Max(1, (last - first) / 2000);
        for (int n = first; n <= last; n += stride)
        {
            double f = n * fundamental;
            var iN = 2 * current.Coefficient(n);
            var vN = 2 * voltage.Coefficient(n);
            var solution = circuit.Solve(f, new Dictionary<string, Complex> { ["Idm"] = iN, ["Vsw"] = vN });
            var zCin = new Complex(input.InputEsr, 2 * Math.PI * f * input.InputEsl - 1 / (2 * Math.PI * f * input.InputCapacitance));
            var loopCurrent = solution.Between("p", "n") / zCin;
            var cmCurrent = ConductedEmission.CableCommonModeCurrent(solution, lisn);
            result.Add((f, DbuVPerM(LoopField(f, loopAreaM2, loopCurrent.Magnitude, distanceM)),
                DbuVPerM(CableField(f, cableLengthM, cmCurrent.Magnitude, distanceM)), limit?.At(f)));
        }
        return result;
    }
}
