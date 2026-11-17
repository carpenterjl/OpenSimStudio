using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Volume;

/// <summary>
/// A spectrum of pairs (wavelength, value) from a ZAS (absorption) or ZQE (quantum yield) file
/// (help, "Phosphors &amp; Fluorescence", "Spectrum files"): "!comment" lines, then
/// "#_of_points wavelength_unit_flag" (0 microns, 1 nanometers), then the pairs. Wavelengths are
/// held in µm, ascending. Between points the value is interpolated linearly (this program's
/// choice; the help does not say).
/// </summary>
public sealed record NscSpectrumTable
{
    public required IReadOnlyList<double> Micrometers { get; init; }
    public required IReadOnlyList<double> Values { get; init; }
    public string Name { get; init; } = "";

    public double Min => Micrometers[0];
    public double Max => Micrometers[^1];

    /// <summary>True when <paramref name="um"/> lies within the table (a one-point table covers
    /// every wavelength).</summary>
    public bool Covers(double um) => Micrometers.Count == 1 || (um >= Min && um <= Max);

    /// <summary>The value at <paramref name="um"/>; a one-point table's value everywhere.
    /// Throws <see cref="InvalidOperationException"/> outside the table.</summary>
    public double At(double um)
    {
        if (Micrometers.Count == 1) return Values[0];
        if (!(um >= Min && um <= Max))
            throw new InvalidOperationException($"{Name}: {um.ToString("g8", CultureInfo.InvariantCulture)} µm lies outside the spectrum's {Min.ToString("g8", CultureInfo.InvariantCulture)}–{Max.ToString("g8", CultureInfo.InvariantCulture)} µm.");
        int i = 0;
        while (i < Micrometers.Count - 2 && um > Micrometers[i + 1]) i++;
        double w0 = Micrometers[i], w1 = Micrometers[i + 1];
        double t = w1 > w0 ? (um - w0) / (w1 - w0) : 0;
        return Values[i] + t * (Values[i + 1] - Values[i]);
    }

    /// <summary>Reads a ZAS or ZQE file's text. <paramref name="minimumPoints"/> is 5 for an
    /// absorption spectrum ("Absorption spectra must contain at least five wavelengths"), 1 for
    /// quantum yield ("if only one efficiency value is listed ... it is used for all wavelengths").</summary>
    public static NscSpectrumTable Parse(string text, string name, int minimumPoints)
    {
        var lines = DataLines(text);
        if (lines.Count == 0) throw new FormatException($"{name}: no header line \"#_of_points wavelength_unit_flag\".");
        var head = Numbers(lines[0].Text, name, lines[0].Number);
        if (head.Length != 2) throw new FormatException($"{name}, line {lines[0].Number}: the header must be \"#_of_points wavelength_unit_flag\".");
        int count = Count(head[0], name, lines[0].Number);
        double unit = Unit(head[1], name, lines[0].Number);
        if (count < minimumPoints) throw new FormatException($"{name}: {count} points; the help requires at least {minimumPoints}.");
        if (lines.Count - 1 != count) throw new FormatException($"{name}: the header says {count} points; the file has {lines.Count - 1} data lines.");
        var w = new double[count];
        var v = new double[count];
        for (int i = 0; i < count; i++)
        {
            var row = Numbers(lines[i + 1].Text, name, lines[i + 1].Number);
            if (row.Length != 2) throw new FormatException($"{name}, line {lines[i + 1].Number}: expected \"wavelength value\".");
            w[i] = row[0] * unit;
            v[i] = row[1];
            if (!(w[i] > 0)) throw new FormatException($"{name}, line {lines[i + 1].Number}: the wavelength must be positive.");
            if (!(v[i] >= 0)) throw new FormatException($"{name}, line {lines[i + 1].Number}: the value must be zero or positive.");
            if (i > 0 && !(w[i] > w[i - 1])) throw new FormatException($"{name}, line {lines[i + 1].Number}: wavelengths must ascend.");
        }
        return new NscSpectrumTable { Micrometers = w, Values = v, Name = name };
    }

    internal static List<(int Number, string Text)> DataLines(string text)
    {
        var list = new List<(int, string)>();
        int n = 0;
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            n++;
            var t = raw.Trim();
            if (t.Length == 0 || t.StartsWith('!')) continue;
            list.Add((n, t));
        }
        return list;
    }

    internal static double[] Numbers(string line, string name, int number)
    {
        var parts = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var r = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i]) || !double.IsFinite(r[i]))
                throw new FormatException($"{name}, line {number}: \"{parts[i]}\" is not a number.");
        return r;
    }

    internal static int Count(double v, string name, int line)
    {
        if (v != Math.Floor(v) || v < 1 || v > 1_000_000) throw new FormatException($"{name}, line {line}: a count must be a whole number from 1; it is {v}.");
        return (int)v;
    }

    /// <summary>µm per file unit: flag 0 microns, 1 nanometers.</summary>
    internal static double Unit(double flag, string name, int line) => flag switch
    {
        0 => 1,
        1 => 1e-3,
        _ => throw new FormatException($"{name}, line {line}: the wavelength unit flag must be 0 (microns) or 1 (nanometers); it is {flag}."),
    };
}

/// <summary>
/// An emission spectrum grid from a ZES file (help, "Phosphors &amp; Fluorescence", "Emission
/// Spectrum"): "#_emission_wavelengths #_incident_wavelengths wavelength_unit_flag", the incident
/// wavelengths, then one row per emission wavelength with its relative photon power for each
/// incident wavelength. "If five or fewer incident wavelengths are defined then intermediate values
/// will be linearly interpolated. If more than five ... cubic spline interpolation will be used. If
/// only one incident wavelength is used then those values are used for all incident wavelengths."
/// The spline is the natural cubic spline (this program's choice); an incident wavelength beyond
/// the grid takes the nearest column, and a spline value below zero is taken as zero (this
/// program's readings, Conventions §10 item 73). "Emission spectra must contain at least five
/// emission wavelengths."
/// </summary>
public sealed record NscEmissionTable
{
    public required IReadOnlyList<double> IncidentMicrometers { get; init; }
    public required IReadOnlyList<double> EmissionMicrometers { get; init; }

    /// <summary>[emission row][incident column].</summary>
    public required double[][] Power { get; init; }

    public string Name { get; init; } = "";

    /// <summary>The relative photon power at every emission wavelength for an incident wavelength.</summary>
    public double[] At(double incidentMicrometers)
    {
        int ni = IncidentMicrometers.Count, ne = EmissionMicrometers.Count;
        var r = new double[ne];
        if (ni == 1)
        {
            for (int e = 0; e < ne; e++) r[e] = Power[e][0];
            return r;
        }
        double x = Math.Clamp(incidentMicrometers, IncidentMicrometers[0], IncidentMicrometers[^1]);
        if (ni <= 5)
        {
            int i = 0;
            while (i < ni - 2 && x > IncidentMicrometers[i + 1]) i++;
            double t = (x - IncidentMicrometers[i]) / (IncidentMicrometers[i + 1] - IncidentMicrometers[i]);
            for (int e = 0; e < ne; e++) r[e] = Power[e][i] + t * (Power[e][i + 1] - Power[e][i]);
        }
        else
        {
            var xs = IncidentMicrometers.ToArray();
            for (int e = 0; e < ne; e++) r[e] = Math.Max(0, new NaturalCubicSpline(xs, Power[e]).Evaluate(x));
        }
        return r;
    }

    public static NscEmissionTable Parse(string text, string name)
    {
        var lines = NscSpectrumTable.DataLines(text);
        if (lines.Count < 2) throw new FormatException($"{name}: expected the header and the incident wavelengths line.");
        var head = NscSpectrumTable.Numbers(lines[0].Text, name, lines[0].Number);
        if (head.Length != 3) throw new FormatException($"{name}, line {lines[0].Number}: the header must be \"#_emission_wavelengths #_incident_wavelengths wavelength_unit_flag\".");
        int ne = NscSpectrumTable.Count(head[0], name, lines[0].Number), ni = NscSpectrumTable.Count(head[1], name, lines[0].Number);
        double unit = NscSpectrumTable.Unit(head[2], name, lines[0].Number);
        if (ne < 5) throw new FormatException($"{name}: {ne} emission wavelengths; the help requires at least five.");
        var inc = NscSpectrumTable.Numbers(lines[1].Text, name, lines[1].Number);
        if (inc.Length != ni) throw new FormatException($"{name}, line {lines[1].Number}: {inc.Length} incident wavelengths; the header says {ni}.");
        for (int i = 0; i < ni; i++)
        {
            inc[i] *= unit;
            if (!(inc[i] > 0) || (i > 0 && !(inc[i] > inc[i - 1]))) throw new FormatException($"{name}, line {lines[1].Number}: incident wavelengths must be positive and ascend.");
        }
        if (lines.Count - 2 != ne) throw new FormatException($"{name}: the header says {ne} emission wavelengths; the file has {lines.Count - 2} rows.");
        var em = new double[ne];
        var power = new double[ne][];
        for (int e = 0; e < ne; e++)
        {
            var row = NscSpectrumTable.Numbers(lines[e + 2].Text, name, lines[e + 2].Number);
            if (row.Length != ni + 1) throw new FormatException($"{name}, line {lines[e + 2].Number}: expected the emission wavelength and {ni} powers.");
            em[e] = row[0] * unit;
            if (!(em[e] > 0) || (e > 0 && !(em[e] > em[e - 1]))) throw new FormatException($"{name}, line {lines[e + 2].Number}: emission wavelengths must be positive and ascend.");
            power[e] = row.Skip(1).ToArray();
            if (power[e].Any(p => !(p >= 0))) throw new FormatException($"{name}, line {lines[e + 2].Number}: powers must be zero or positive.");
        }
        return new NscEmissionTable { IncidentMicrometers = inc, EmissionMicrometers = em, Power = power, Name = name };
    }

    /// <summary>
    /// An emission wavelength for an incident one: the photon spectrum at that incident wavelength,
    /// linear between the emission points and restricted to wavelengths longer than the incident
    /// one ("A ray will only be shifted to a longer wavelength than its original value"), sampled
    /// by inverting its piecewise-quadratic cumulative distribution (derived). Null when no
    /// emission lies above the incident wavelength.
    /// </summary>
    public double? Sample(double incidentMicrometers, double u)
    {
        var p = At(incidentMicrometers);
        var w = EmissionMicrometers;
        int n = w.Count;
        // Segments [w_i, w_{i+1}] clipped to (λ_in, ∞): the clipped start value is interpolated.
        var a = new List<(double X0, double X1, double F0, double F1)>();
        for (int i = 0; i < n - 1; i++)
        {
            double x0 = w[i], x1 = w[i + 1], f0 = p[i], f1 = p[i + 1];
            if (x1 <= incidentMicrometers) continue;
            if (x0 < incidentMicrometers)
            {
                double t = (incidentMicrometers - x0) / (x1 - x0);
                f0 += t * (f1 - f0);
                x0 = incidentMicrometers;
            }
            a.Add((x0, x1, f0, f1));
        }
        double total = 0;
        foreach (var s in a) total += (s.X1 - s.X0) * (s.F0 + s.F1) / 2;
        if (!(total > 0)) return null;
        double target = u * total, acc = 0;
        for (int k = 0; k < a.Count; k++)
        {
            var s = a[k];
            double area = (s.X1 - s.X0) * (s.F0 + s.F1) / 2;
            if (acc + area < target && k < a.Count - 1) { acc += area; continue; }
            double need = Math.Clamp(target - acc, 0, area), h = s.X1 - s.X0, slope = (s.F1 - s.F0) / h;
            // ∫₀^t (F0 + slope·τ) dτ = need → slope/2·t² + F0·t − need = 0.
            double t;
            if (!(need > 0)) t = 0;
            else if (Math.Abs(slope) * h <= 1e-12 * Math.Max(s.F0, s.F1)) t = need / s.F0;
            else t = 2 * need / (s.F0 + Math.Sqrt(Math.Max(0, s.F0 * s.F0 + 2 * slope * need)));   // the stable root
            return Math.Clamp(s.X0 + t, s.X0, s.X1);
        }
        return a[^1].X1;
    }
}
