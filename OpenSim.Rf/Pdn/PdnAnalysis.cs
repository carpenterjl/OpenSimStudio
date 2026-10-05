using System.Globalization;
using System.Numerics;
using System.Text;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Pdn;

/// <summary>How a vendor's capacitor S-parameter file was measured.</summary>
public enum CapacitorFixture
{
    /// <summary>One port across the part: Z = Z₀(1 + S11)/(1 − S11).</summary>
    OnePort,
    /// <summary>Two ports with the part from the through line to ground: Z = Z₀·S21/(2(1 − S21)).</summary>
    TwoPortShunt,
    /// <summary>Two ports with the part in the through line: Z = 2Z₀(1 − S21)/S21.</summary>
    TwoPortSeries
}

/// <summary>
/// A capacitor as the network sees it: C, ESR and ESL in series, or a measured impedance
/// curve from the vendor's S-parameters (which then replaces all three).
/// </summary>
public sealed record CapacitorModel
{
    public string PartName { get; init; } = "";
    public double CapacitanceFarads { get; init; }
    public double EsrOhms { get; init; }
    public double EslHenries { get; init; }

    /// <summary>Measured impedance by ascending frequency; null uses C, ESR and ESL.</summary>
    public IReadOnlyList<(double Hz, Complex Ohms)>? Measured { get; init; }

    public Complex Impedance(double frequencyHz)
    {
        double omega = 2 * Math.PI * frequencyHz;
        if (Measured is not { Count: > 0 } data)
        {
            if (!(CapacitanceFarads > 0))
                throw new InvalidOperationException($"Capacitor model '{PartName}' has no capacitance.");
            return new Complex(EsrOhms, omega * EslHenries - 1 / (omega * CapacitanceFarads));
        }
        // Below and above the measured band the part is continued as the capacitor and the
        // inductor its end points imply, with the end point's resistance.
        if (frequencyHz <= data[0].Hz)
            return new Complex(data[0].Ohms.Real, data[0].Ohms.Imaginary * data[0].Hz / frequencyHz);
        if (frequencyHz >= data[^1].Hz)
            return new Complex(data[^1].Ohms.Real, data[^1].Ohms.Imaginary * frequencyHz / data[^1].Hz);
        int hi = 1;
        while (data[hi].Hz < frequencyHz) hi++;
        double t = Math.Log(frequencyHz / data[hi - 1].Hz) / Math.Log(data[hi].Hz / data[hi - 1].Hz);
        return data[hi - 1].Ohms + (data[hi].Ohms - data[hi - 1].Ohms) * t;
    }

    /// <summary>The measured band, when the model is a measured curve.</summary>
    public (double FromHz, double ToHz)? MeasuredBand =>
        Measured is { Count: > 0 } data ? (data[0].Hz, data[^1].Hz) : null;

    public static CapacitorModel FromTouchstone(string partName, TouchstoneData data, CapacitorFixture fixture)
    {
        int needed = fixture == CapacitorFixture.OnePort ? 1 : 2;
        if (data.Ports != needed)
            throw new InvalidOperationException(
                $"A {fixture} capacitor model needs a {needed}-port file; this one has {data.Ports} ports.");
        double z0 = data.ReferenceOhms;
        var curve = new List<(double, Complex)>();
        for (int k = 0; k < data.FrequenciesHz.Count; k++)
        {
            var s = data.Scattering[k];
            Complex z = fixture switch
            {
                CapacitorFixture.OnePort => z0 * (1 + s[0, 0]) / (1 - s[0, 0]),
                CapacitorFixture.TwoPortShunt => z0 * s[1, 0] / (2 * (1 - s[1, 0])),
                _ => 2 * z0 * (1 - s[1, 0]) / s[1, 0]
            };
            curve.Add((data.FrequenciesHz[k], z));
        }
        return new CapacitorModel { PartName = partName, Measured = curve };
    }
}

/// <summary>A capacitor placed on the plane pair.</summary>
/// <param name="Port">Index of the plane port it connects at.</param>
/// <param name="MountingInductanceHenries">Inductance of its pads, escape traces and vias
/// above the planes, in series with the part.</param>
public sealed record PlacedCapacitor(string Name, int Port, CapacitorModel Model, double MountingInductanceHenries)
{
    public Complex Impedance(double frequencyHz) =>
        Model.Impedance(frequencyHz) + new Complex(0, 2 * Math.PI * frequencyHz * MountingInductanceHenries);

    /// <summary>Series resonance of the mounted part (C, ESL and mounting inductance), or
    /// NaN for a measured model.</summary>
    public double MountedResonanceHz => Model.Measured is null && Model.CapacitanceFarads > 0
        ? 1 / (2 * Math.PI * Math.Sqrt(Model.CapacitanceFarads * (Model.EslHenries + MountingInductanceHenries)))
        : double.NaN;
}

/// <summary>The regulator as a resistance and an inductance behind an ideal source: its
/// output impedance well below its loop bandwidth, and the inductance it turns into above.</summary>
public sealed record Regulator(string Name, int Port, double ResistanceOhms, double InductanceHenries)
{
    public Complex Impedance(double frequencyHz) =>
        new(ResistanceOhms, 2 * Math.PI * frequencyHz * InductanceHenries);
}

/// <summary>The impedance the supply has to stay under, by frequency.</summary>
public sealed record TargetImpedance(IReadOnlyList<(double Hz, double Ohms)> Points)
{
    /// <summary>V·ripple/ΔI, flat up to <paramref name="upToHz"/>.</summary>
    public static TargetImpedance Flat(double volts, double ripplePercent, double stepAmps, double upToHz)
    {
        if (!(volts > 0 && ripplePercent > 0 && stepAmps > 0 && upToHz > 0))
            throw new ArgumentException("Voltage, ripple, current step and frequency must be positive.");
        double z = volts * ripplePercent / 100 / stepAmps;
        return new TargetImpedance(new[] { (0.0, z), (upToHz, z) });
    }

    /// <summary>The limit at a frequency, or NaN above the last point (no requirement).</summary>
    public double At(double frequencyHz)
    {
        if (Points.Count == 0 || frequencyHz > Points[^1].Hz) return double.NaN;
        if (frequencyHz <= Points[0].Hz) return Points[0].Ohms;
        int hi = 1;
        while (Points[hi].Hz < frequencyHz) hi++;
        var (f0, z0) = Points[hi - 1];
        var (f1, z1) = Points[hi];
        if (f0 <= 0 || z0 <= 0 || z1 <= 0) return z0 + (z1 - z0) * (frequencyHz - f0) / (f1 - f0);
        double t = Math.Log(frequencyHz / f0) / Math.Log(f1 / f0);
        return z0 * Math.Pow(z1 / z0, t);
    }
}

public sealed record PdnSetup
{
    public required PlanePairSpec Planes { get; init; }

    /// <summary>The plane ports of the load, taken as tied together at the part.</summary>
    public required IReadOnlyList<int> LoadPorts { get; init; }

    public IReadOnlyList<PlacedCapacitor> Capacitors { get; init; } = Array.Empty<PlacedCapacitor>();
    public IReadOnlyList<Regulator> Regulators { get; init; } = Array.Empty<Regulator>();
    public TargetImpedance? Target { get; init; }

    public double MinFrequencyHz { get; init; } = 1e4;
    public double MaxFrequencyHz { get; init; } = 1e9;
    public int PointsPerDecade { get; init; } = 40;

    /// <summary>Plane resonances to list.</summary>
    public int ModeCount { get; init; } = 6;
}

/// <param name="IsMaximum">A peak of |Z| (an anti-resonance); false for a dip.</param>
public sealed record PdnExtremum(double FrequencyHz, double Ohms, bool IsMaximum);

/// <summary>A band over which |Z| is above the target.</summary>
public sealed record PdnViolation(double FromHz, double ToHz, double WorstHz, double WorstOhms, double TargetOhms);

public sealed record PdnResult
{
    public required IReadOnlyList<double> FrequenciesHz { get; init; }

    /// <summary>Impedance at the load with the capacitors and regulators in place.</summary>
    public required IReadOnlyList<Complex> Impedance { get; init; }

    /// <summary>The same with the plane pair alone.</summary>
    public required IReadOnlyList<Complex> BareImpedance { get; init; }

    public required IReadOnlyList<PdnExtremum> Extrema { get; init; }
    public required IReadOnlyList<PdnViolation> Violations { get; init; }
    public required IReadOnlyList<PlaneMode> Modes { get; init; }

    /// <summary>True with a target and no violation; null with no target.</summary>
    public required bool? Pass { get; init; }

    public required IReadOnlyList<string> Assumptions { get; init; }
    public required IReadOnlyList<string> Summary { get; init; }

    public IReadOnlyList<string> Describe() => Summary;

    /// <summary>frequency, |Z|, phase, real, imaginary, |Z| of the bare planes, target.</summary>
    public string ToCsv(TargetImpedance? target = null)
    {
        var text = new StringBuilder("frequency_Hz,Z_ohm,phase_deg,re_ohm,im_ohm,Z_bare_planes_ohm,target_ohm\n");
        for (int k = 0; k < FrequenciesHz.Count; k++)
        {
            double limit = target?.At(FrequenciesHz[k]) ?? double.NaN;
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $"{FrequenciesHz[k]:G9},{Impedance[k].Magnitude:G9},{Impedance[k].Phase * 180 / Math.PI:G6},"
                + $"{Impedance[k].Real:G9},{Impedance[k].Imaginary:G9},{BareImpedance[k].Magnitude:G9},"
                + $"{(double.IsNaN(limit) ? "" : limit.ToString("G6", CultureInfo.InvariantCulture))}\n"));
        }
        return text.ToString();
    }

    /// <summary>The loaded impedance as a one-port Touchstone file.</summary>
    public string ToTouchstone(double referenceOhms = 50) =>
        TouchstoneWriter.Write(FrequenciesHz,
            Impedance.Select(z => new[,] { { (z - referenceOhms) / (z + referenceOhms) } }).ToList(),
            referenceOhms, new[] { "PDN at the load" });
}

/// <summary>
/// Power-distribution impedance against frequency at a load: the plane pair solved by
/// <see cref="PlanePair"/> between every port, then each capacitor and regulator hung on its
/// port as a shunt branch,
/// <code>  Z = (I + Z_planes·Y_parts)⁻¹·Z_planes,</code>
/// and the load's ports tied together. The plane solve is the expensive part and does not
/// depend on the parts, so <see cref="Evaluate"/> re-prices a changed capacitor set on the
/// same plane response.
/// </summary>
public static class PdnAnalysis
{
    public static IReadOnlyList<double> LogSweep(double fromHz, double toHz, int pointsPerDecade)
    {
        if (!(fromHz > 0 && toHz > fromHz)) throw new ArgumentException("The sweep needs 0 < from < to.");
        int count = Math.Max(2, (int)Math.Ceiling(Math.Log10(toHz / fromHz) * Math.Max(pointsPerDecade, 2)) + 1);
        var f = new double[count];
        for (int k = 0; k < count; k++) f[k] = fromHz * Math.Pow(toHz / fromHz, (double)k / (count - 1));
        return f;
    }

    public static PdnResult Solve(PdnSetup setup, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pair = new PlanePair(setup.Planes with { MaxFrequencyHz = setup.MaxFrequencyHz });
        var frequencies = LogSweep(setup.MinFrequencyHz, setup.MaxFrequencyHz, setup.PointsPerDecade).ToList();
        var planes = pair.Sweep(frequencies, progress, cancellationToken).ToList();

        // A peak between two grid points is higher than either: twice over, put nine more
        // points around every local maximum of the loaded impedance.
        for (int round = 0; round < 2; round++)
        {
            var loaded = frequencies.Select((f, k) => AtLoad(planes[k], f, setup, true).Magnitude).ToList();
            var extra = new SortedSet<double>();
            for (int k = 1; k + 1 < frequencies.Count; k++)
            {
                if (!(loaded[k] >= loaded[k - 1] && loaded[k] > loaded[k + 1])) continue;
                for (int s = 1; s < 10; s++)
                {
                    double lo = frequencies[k - 1] * Math.Pow(frequencies[k] / frequencies[k - 1], s / 10.0);
                    double hi = frequencies[k] * Math.Pow(frequencies[k + 1] / frequencies[k], s / 10.0);
                    extra.Add(lo);
                    extra.Add(hi);
                }
            }
            if (extra.Count == 0) break;
            var added = extra.ToList();
            var solved = pair.Sweep(added, null, cancellationToken);
            var merged = frequencies.Select((f, k) => (f, planes[k]))
                .Concat(added.Select((f, k) => (f, solved[k])))
                .OrderBy(p => p.f).ToList();
            frequencies = merged.Select(p => p.f).ToList();
            planes = merged.Select(p => p.Item2).ToList();
        }

        var modes = setup.ModeCount > 0 ? pair.Modes(setup.ModeCount, cancellationToken) : Array.Empty<PlaneMode>();
        return Evaluate(pair, frequencies, planes, setup, modes);
    }

    /// <summary>Impedance at the load for one frequency's plane matrix.</summary>
    private static Complex AtLoad(Complex[,] planes, double f, PdnSetup setup, bool withParts)
    {
        int n = planes.GetLength(0);
        Complex[,] z = planes;
        if (withParts && (setup.Capacitors.Count > 0 || setup.Regulators.Count > 0))
        {
            var admittance = new Complex[n];
            foreach (var c in setup.Capacitors) admittance[c.Port] += 1 / c.Impedance(f);
            foreach (var r in setup.Regulators) admittance[r.Port] += 1 / r.Impedance(f);
            var system = new Complex[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    system[i, j] = (i == j ? 1 : 0) + planes[i, j] * admittance[j];
            z = NetworkParameters.Multiply(NetworkParameters.Invert(system), planes);
        }
        if (setup.LoadPorts.Count == 1) return z[setup.LoadPorts[0], setup.LoadPorts[0]];
        // The load's ports tied together: 1/(1ᵀ·S⁻¹·1) with S the block of Z among them.
        int m = setup.LoadPorts.Count;
        var block = new Complex[m, m];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
                block[i, j] = z[setup.LoadPorts[i], setup.LoadPorts[j]];
        var inverse = NetworkParameters.Invert(block);
        Complex total = Complex.Zero;
        foreach (Complex y in inverse) total += y;
        return 1 / total;
    }

    /// <summary>Hang the setup's parts on an already solved plane response.</summary>
    public static PdnResult Evaluate(PlanePair pair, IReadOnlyList<double> frequenciesHz,
        IReadOnlyList<Complex[,]> planeImpedance, PdnSetup setup, IReadOnlyList<PlaneMode>? modes = null)
    {
        int ports = pair.Spec.Ports.Count;
        if (setup.LoadPorts.Count == 0) throw new InvalidOperationException("The PDN needs a load port.");
        foreach (int p in setup.LoadPorts.Concat(setup.Capacitors.Select(c => c.Port)).Concat(setup.Regulators.Select(r => r.Port)))
            if (p < 0 || p >= ports) throw new InvalidOperationException($"Port index {p} is not one of the {ports} plane ports.");

        var loaded = new Complex[frequenciesHz.Count];
        var bare = new Complex[frequenciesHz.Count];
        for (int k = 0; k < frequenciesHz.Count; k++)
        {
            loaded[k] = AtLoad(planeImpedance[k], frequenciesHz[k], setup, true);
            bare[k] = AtLoad(planeImpedance[k], frequenciesHz[k], setup, false);
        }

        var extrema = new List<PdnExtremum>();
        for (int k = 1; k + 1 < loaded.Length; k++)
        {
            double a = loaded[k - 1].Magnitude, b = loaded[k].Magnitude, c = loaded[k + 1].Magnitude;
            if (b >= a && b > c) extrema.Add(new PdnExtremum(frequenciesHz[k], b, true));
            else if (b <= a && b < c) extrema.Add(new PdnExtremum(frequenciesHz[k], b, false));
        }

        var violations = new List<PdnViolation>();
        if (setup.Target is { } target)
        {
            int start = -1, worst = -1;
            for (int k = 0; k <= loaded.Length; k++)
            {
                double limit = k < loaded.Length ? target.At(frequenciesHz[k]) : double.NaN;
                bool over = k < loaded.Length && !double.IsNaN(limit) && loaded[k].Magnitude > limit;
                if (over)
                {
                    if (start < 0) { start = k; worst = k; }
                    else if (loaded[k].Magnitude / limit > loaded[worst].Magnitude / target.At(frequenciesHz[worst])) worst = k;
                }
                else if (start >= 0)
                {
                    violations.Add(new PdnViolation(frequenciesHz[start], frequenciesHz[k - 1], frequenciesHz[worst],
                        loaded[worst].Magnitude, target.At(frequenciesHz[worst])));
                    start = -1;
                }
            }
        }

        modes ??= Array.Empty<PlaneMode>();
        bool? pass = setup.Target is null ? null : violations.Count == 0;
        var spec = pair.Spec;

        var summary = new List<string>();
        string load = string.Join(" + ", setup.LoadPorts.Select(p => spec.Ports[p].Name));
        int top = Enumerable.Range(0, loaded.Length).MaxBy(k => loaded[k].Magnitude);
        summary.Add($"PDN impedance at {load}, {Hz(frequenciesHz[0])} to {Hz(frequenciesHz[^1])}"
            + (pass is null ? "" : pass.Value ? " — within the target" : " — OVER the target")
            + $": highest {Ohm(loaded[top].Magnitude)} at {Hz(frequenciesHz[top])}.");
        summary.Add($"  Plane pair: {pair.AreaSquareMeters * 1e4:g4} cm², {spec.SeparationMeters * 1e6:g4} µm apart, "
            + $"εr {spec.RelativePermittivity:g3}, tan δ {spec.LossTangent:g2} — {Farad(pair.StaticCapacitanceFarads)}; "
            + $"{pair.NodeCount} unknowns at a {pair.MeshEdgeMeters * 1e3:g3} mm element size.");
        foreach (var v in violations)
            summary.Add($"  over the target from {Hz(v.FromHz)} to {Hz(v.ToHz)}: worst {Ohm(v.WorstOhms)} at "
                + $"{Hz(v.WorstHz)} against {Ohm(v.TargetOhms)}.");
        foreach (var e in extrema.Where(e => e.IsMaximum).OrderByDescending(e => e.Ohms).Take(8).OrderBy(e => e.FrequencyHz))
            summary.Add($"  peak (anti-resonance) {Ohm(e.Ohms)} at {Hz(e.FrequencyHz)}.");
        foreach (var m in modes)
            summary.Add($"  plane resonance {Hz(m.FrequencyHz)}: seen at the load with "
                + $"{setup.LoadPorts.Max(p => m.PortCoupling[p]):p0} of the mode's peak voltage.");
        foreach (var c in setup.Capacitors.GroupBy(c => (c.Model, Math.Round(c.MountingInductanceHenries * 1e11))))
        {
            var first = c.First();
            string what = first.Model.Measured is null
                ? $"{Farad(first.Model.CapacitanceFarads)}, ESR {Ohm(first.Model.EsrOhms)}, ESL {Henry(first.Model.EslHenries)}"
                : "measured model";
            summary.Add($"  {c.Count()} × {(first.Model.PartName.Length > 0 ? first.Model.PartName + " " : "")}({what}) "
                + $"+ {Henry(first.MountingInductanceHenries)} mounting"
                + (double.IsNaN(first.MountedResonanceHz) ? "" : $": resonates mounted at {Hz(first.MountedResonanceHz)}")
                + $" — {string.Join(", ", c.Take(6).Select(x => x.Name))}{(c.Count() > 6 ? ", …" : "")}.");
        }
        foreach (var r in setup.Regulators)
            summary.Add($"  regulator {r.Name}: {Ohm(r.ResistanceOhms)} + {Henry(r.InductanceHenries)}.");

        var assumptions = new List<string>
        {
            "The plane pair is two copper sheets with the field uniform across the gap (a 2D model, valid while "
            + "the gap is far below a wavelength); the edges are open and their fringing field is left out, so "
            + "resonances come out a fraction of the gap-to-size ratio high.",
            "A port spreads its current uniformly over its patch and reads the mean voltage over it: the "
            + "inductance of the current converging on a via between the planes is in the plane solve, the part of "
            + "a via above the planes is not (for a capacitor it is the mounting inductance; for the load it is "
            + "left out, so the result is the impedance at the planes under the load).",
            spec.WidebandDielectric && spec.LossTangent > 0
                ? $"Dielectric: Djordjevic–Sarkar, εr {spec.RelativePermittivity:g3} and tan δ {spec.LossTangent:g2} "
                  + $"taken as the values at {Hz(spec.DielectricReferenceHz)}."
                : $"Dielectric: εr {spec.RelativePermittivity:g3} and tan δ {spec.LossTangent:g2} at every frequency.",
            "Copper loss is the surface impedance of each plane's inner face, smooth copper.",
            "The regulator is a resistance and an inductance in series; its control loop is not modelled.",
            "Parts interact only through the planes: no mutual inductance between the mounting loops of "
            + "neighbouring capacitors."
        };
        foreach (var c in setup.Capacitors.Select(c => c.Model).Distinct().Where(m => m.MeasuredBand is not null))
        {
            var (from, to) = c.MeasuredBand!.Value;
            if (frequenciesHz[0] < from || frequenciesHz[^1] > to)
                assumptions.Add($"The measured model of {c.PartName} covers {Hz(from)} to {Hz(to)}; outside it the "
                    + "part is continued as the capacitor below and the inductor above that its end points imply.");
        }

        return new PdnResult
        {
            FrequenciesHz = frequenciesHz.ToList(),
            Impedance = loaded,
            BareImpedance = bare,
            Extrema = extrema,
            Violations = violations,
            Modes = modes,
            Pass = pass,
            Assumptions = assumptions,
            Summary = summary
        };
    }

    internal static string Hz(double f) => f >= 1e9 ? $"{f / 1e9:g4} GHz" : f >= 1e6 ? $"{f / 1e6:g4} MHz"
        : f >= 1e3 ? $"{f / 1e3:g4} kHz" : $"{f:g4} Hz";
    internal static string Ohm(double z) => z >= 1 ? $"{z:g4} Ω" : z >= 1e-3 ? $"{z * 1e3:g4} mΩ" : $"{z * 1e6:g4} µΩ";
    internal static string Henry(double l) => l >= 1e-6 ? $"{l * 1e6:g4} µH" : l >= 1e-9 ? $"{l * 1e9:g4} nH" : $"{l * 1e12:g4} pH";
    internal static string Farad(double c) => c >= 1e-6 ? $"{c * 1e6:g4} µF" : c >= 1e-9 ? $"{c * 1e9:g4} nF" : $"{c * 1e12:g4} pF";
}
