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
/// <param name="SwitchNodeCapacitance">Switch node to earth (heatsink, chassis, ground plane under
/// the inductor): Feature 7's net-to-plane capacitance of the switch-node net, or a measured
/// value.</param>
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

    /// <summary>A buck converter's input: the switch draws I_out while on (edges as the voltage's),
    /// and the switch node swings 0 → V_in.</summary>
    public static ConverterInput Buck(double vIn, double iOut, double frequencyHz, double duty, double rise, double fall,
        double switchNodeCapacitance, double inputCapacitance, double esr, double esl) =>
        new(PiecewiseLinearWaveform.Trapezoid(iOut, frequencyHz, duty, rise, fall),
            PiecewiseLinearWaveform.Trapezoid(vIn, frequencyHz, duty, rise, fall),
            switchNodeCapacitance, inputCapacitance, esr, esl);
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
        "Linear network: the converter is two ideal sources (switch current, switch-node voltage); ringing, diode recovery and the inductor's own capacitance are not in it unless put in the waveforms.",
        "LISN as commonly drawn for the 50 Ω/50 µH + 5 Ω V-network; limit lines " + EmiLimits.Unverified + ".",
        "The common-mode path is the switch-node capacitance to earth only; other paths (transformer interwinding, heatsink of other parts) are not modelled."
    };

    public static ConductedEmissionResult Estimate(ConverterInput input, EmiLimitLine? limit = null, LisnModel? lisn = null,
        double minFrequencyHz = 150e3, double maxFrequencyHz = 30e6)
    {
        lisn ??= new LisnModel();
        if (Math.Abs(input.SwitchCurrent.Period - input.SwitchNodeVoltage.Period) > 1e-12 * input.SwitchCurrent.Period)
            throw new ArgumentException("The switch current and switch-node voltage must share one period.");
        var circuit = Build(input, lisn);
        var current = input.SwitchCurrent;
        var voltage = input.SwitchNodeVoltage;
        var lines = new List<EmissionLine>();
        int first = Math.Max(1, (int)Math.Ceiling(minFrequencyHz * current.Period - 1e-9));
        for (int n = first; n * current.Fundamental <= maxFrequencyHz * (1 + 1e-12); n++)
        {
            double f = n * current.Fundamental;
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
        return new ConductedEmissionResult(lines, limit, Assumptions);
    }

    /// <summary>The input network: the converter's rails "p" (+) and "n" (−); the switch draws the
    /// DM current from p to n; the switch node "sw" sits at V_sw above n and couples to earth
    /// through C_sw; the input capacitor across p–n; then the optional filter, Y capacitors and the
    /// line ("L") and neutral ("N") LISNs.</summary>
    internal static AcCircuit Build(ConverterInput input, LisnModel lisn)
    {
        var c = new AcCircuit();
        // The sources' amplitudes are given per harmonic at each solve.
        c.Current("Idm", "p", "n", _ => Complex.Zero);
        c.Voltage("Vsw", "sw", "n", _ => Complex.Zero);
        c.Capacitor("Csw", "sw", "0", input.SwitchNodeCapacitance);
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
    /// current (the switch current minus what the input draws), the cable current is the
    /// common-mode current through the switch-node capacitance, both per harmonic, against an
    /// optional radiated limit.</summary>
    public static IReadOnlyList<(double FrequencyHz, double LoopDbuVm, double CableDbuVm, double? LimitDbuVm)> Converter(
        ConverterInput input, double loopAreaM2, double cableLengthM, double distanceM, EmiLimitLine? limit = null,
        double minFrequencyHz = 30e6, double maxFrequencyHz = 1e9, LisnModel? lisn = null)
    {
        lisn ??= new LisnModel();
        var circuit = ConductedEmission.Build(input, lisn);
        var result = new List<(double, double, double, double?)>();
        var current = input.SwitchCurrent;
        int first = Math.Max(1, (int)Math.Ceiling(minFrequencyHz * current.Period - 1e-9));
        int last = (int)Math.Floor(maxFrequencyHz * current.Period + 1e-9);
        // Above a few hundred lines per decade the estimate is a smooth envelope; sample it.
        int stride = Math.Max(1, (last - first) / 2000);
        for (int n = first; n <= last; n += stride)
        {
            double f = n * current.Fundamental;
            var iN = 2 * current.Coefficient(n);
            var vN = 2 * input.SwitchNodeVoltage.Coefficient(n);
            var solution = circuit.Solve(f, new Dictionary<string, Complex> { ["Idm"] = iN, ["Vsw"] = vN });
            var zCin = new Complex(input.InputEsr, 2 * Math.PI * f * input.InputEsl - 1 / (2 * Math.PI * f * input.InputCapacitance));
            var loopCurrent = solution.Between("p", "n") / zCin;
            var zSw = new Complex(0, -1 / (2 * Math.PI * f * input.SwitchNodeCapacitance));
            var cmCurrent = solution["sw"] / zSw;
            result.Add((f, DbuVPerM(LoopField(f, loopAreaM2, loopCurrent.Magnitude, distanceM)),
                DbuVPerM(CableField(f, cableLengthM, cmCurrent.Magnitude, distanceM)), limit?.At(f)));
        }
        return result;
    }
}
