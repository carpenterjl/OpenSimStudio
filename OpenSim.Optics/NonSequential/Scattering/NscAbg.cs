using System.Globalization;
using System.Text;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Scattering;

/// <summary>One row of an ABg profile: the angle of incidence (degrees, 0–90) and A, B, g
/// (help, "ABg Scatter Data Catalogs").</summary>
public readonly record struct NscAbgRow(double AngleDegrees, double A, double B, double G);

/// <summary>
/// One ABg profile of a catalog (help, "ABg Scatter Data Catalogs"): an upper-case name, the
/// wavelength the data were measured at (µm; 0: no wavelength scaling) and up to 10 rows by angle
/// of incidence. The BSDF is A/(B + |x|^g) (help, "Available Scatter Models", "ABg Model
/// Scattering"); "The BSDF for intermediate angles of incidence are linearly interpolated in
/// cosine space"; below the smallest angle the first row is used, above the largest the last.
/// With a reference wavelength λ_ref, A′ = A(λ/λ_ref)^(g−4) and B′ = B(λ/λ_ref)^g ("Wavelength
/// scaling of ABg data").
/// </summary>
public sealed record NscAbgProfile
{
    public required string Name { get; init; }
    public double WavelengthMicrometers { get; init; }
    public required IReadOnlyList<NscAbgRow> Rows { get; init; }

    /// <summary>Every problem, one line each.</summary>
    public void Validate(string where, List<string> problems)
    {
        string F(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(Name) || Name.Any(char.IsWhiteSpace))
            problems.Add($"{where}: an ABg profile needs a name without spaces.");
        else if (Name != Name.ToUpperInvariant())
            problems.Add($"{where}: ABg profile names are upper case (help, \"ABg Scatter Data Catalogs\": \"This name must be in uppercase\").");
        if (!(WavelengthMicrometers >= 0) || !double.IsFinite(WavelengthMicrometers))
            problems.Add($"{where}: the reference wavelength must be zero (no scaling) or positive (µm); it is {F(WavelengthMicrometers)}.");
        if (Rows is null || Rows.Count == 0) { problems.Add($"{where}: the profile has no rows."); return; }
        if (Rows.Count > 10) problems.Add($"{where}: {Rows.Count} rows; \"Up to 10 data rows may be defined for each ABg data name\".");
        var seen = new HashSet<double>();
        foreach (var r in Rows)
        {
            if (!(r.AngleDegrees >= 0 && r.AngleDegrees <= 90)) problems.Add($"{where}: the angle of incidence {F(r.AngleDegrees)}° is not in 0–90.");
            else if (!seen.Add(r.AngleDegrees)) problems.Add($"{where}: the angle {F(r.AngleDegrees)}° appears twice.");
            if (!(r.A >= 0) || !double.IsFinite(r.A)) problems.Add($"{where}: A must be zero or positive (it is {F(r.A)}).");
            if (!double.IsFinite(r.G)) problems.Add($"{where}: g must be finite.");
            else if (r.G == 0 ? !(r.B >= 0) || !double.IsFinite(r.B) : !(r.B > 1e-12) || !double.IsFinite(r.B))
                problems.Add($"{where}, angle {F(r.AngleDegrees)}°: B must exceed 1e-12 unless g = 0 (then B ≥ 0) (help, \"Available Scatter Models\"); it is {F(r.B)}.");
        }
    }

    /// <summary>The rows at <paramref name="micrometers"/>, wavelength-scaled, sorted by angle.</summary>
    public NscAbgRow[] RowsAt(double micrometers)
    {
        var rows = Rows.OrderBy(r => r.AngleDegrees).ToArray();
        if (WavelengthMicrometers == 0) return rows;
        double ratio = micrometers / WavelengthMicrometers;
        return rows.Select(r => r with { A = r.A * Math.Pow(ratio, r.G - 4), B = r.B * Math.Pow(ratio, r.G) }).ToArray();
    }
}

/// <summary>
/// The project's ABg catalog (help, "ABg Scatter Data Catalogs": "the maximum number of profiles
/// in a catalog is 100"). OpticStudio keeps it in ABG_DATA.DAT, whose layout its help does not
/// document, so that file is not read; the catalog is saved with the project and written and read
/// here as plain text, one row per line: <c>NAME wavelength angle A B g</c> ('#' starts a comment).
/// </summary>
public sealed record NscAbgCatalog
{
    public IReadOnlyList<NscAbgProfile> Profiles { get; init; } = Array.Empty<NscAbgProfile>();

    public static NscAbgCatalog Empty { get; } = new();

    /// <summary>The profile named <paramref name="name"/> (exact, upper case), or null.</summary>
    public NscAbgProfile? Find(string name) => Profiles.FirstOrDefault(p => p.Name == name);

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Profiles.Count > 100) problems.Add($"The ABg catalog has {Profiles.Count} profiles; at most 100 (help, \"ABg Scatter Data Catalogs\").");
        foreach (var g in Profiles.GroupBy(p => p.Name).Where(g => g.Count() > 1))
            problems.Add($"The ABg catalog names profile '{g.Key}' {g.Count()} times.");
        foreach (var p in Profiles) p.Validate($"ABg profile '{p.Name}'", problems);
        return problems;
    }

    /// <summary>The catalog as text (see the type's summary).</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# NAME wavelength(µm, 0 = no scaling) angle(deg) A B g");
        foreach (var p in Profiles)
            foreach (var r in p.Rows)
                sb.AppendLine(string.Join(" ", p.Name, R(p.WavelengthMicrometers), R(r.AngleDegrees), R(r.A), R(r.B), R(r.G)));
        return sb.ToString();
        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the text <see cref="Format"/> writes; throws <see cref="FormatException"/>
    /// naming the line.</summary>
    public static NscAbgCatalog Parse(string text)
    {
        var byName = new List<(string Name, double Wave, List<NscAbgRow> Rows)>();
        int lineNumber = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            lineNumber++;
            string line = raw.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length != 6) throw new FormatException($"ABg catalog line {lineNumber}: expected NAME wavelength angle A B g (6 fields), found {t.Length}.");
            var v = new double[5];
            for (int i = 0; i < 5; i++)
                if (!double.TryParse(t[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    throw new FormatException($"ABg catalog line {lineNumber}: '{t[i + 1]}' is not a number.");
            int at = byName.FindIndex(p => p.Name == t[0]);
            if (at < 0) { byName.Add((t[0], v[0], new List<NscAbgRow>())); at = byName.Count - 1; }
            else if (byName[at].Wave != v[0]) throw new FormatException($"ABg catalog line {lineNumber}: profile {t[0]} has two reference wavelengths.");
            byName[at].Rows.Add(new NscAbgRow(v[1], v[2], v[3], v[4]));
        }
        return new NscAbgCatalog { Profiles = byName.Select(p => new NscAbgProfile { Name = p.Name, WavelengthMicrometers = p.Wave, Rows = p.Rows }).ToArray() };
    }
}

/// <summary>
/// An ABg File (help, "Available Scatter Models", "ABg File Scattering"): a weighted sum of
/// catalog profiles. "If the profile weights are taken to be relative, the sum of BSDF values is
/// divided by the sum of the weights"; with absolute weights the sum is not normalised. At most
/// 20 profiles.
/// </summary>
public sealed record NscAbgSum
{
    public required string Name { get; init; }
    public bool Absolute { get; init; }
    public required IReadOnlyList<(NscAbgProfile Profile, double Weight)> Terms { get; init; }

    /// <summary>The coefficient each profile's BSDF is multiplied by.</summary>
    public double[] Coefficients()
    {
        double sum = Terms.Sum(t => t.Weight);
        return Terms.Select(t => Absolute ? t.Weight : t.Weight / sum).ToArray();
    }

    /// <summary>
    /// Reads an ABGF file's text (help, "ABg File Scattering"): the first line holds the number of
    /// profiles and an optional flag (non-zero: absolute weights), then one line per profile, the
    /// name (upper case, as in the catalog) and its weight. Each name is looked up in
    /// <paramref name="catalog"/>. Throws <see cref="FormatException"/> naming the line or profile.
    /// </summary>
    public static NscAbgSum Parse(string text, string name, NscAbgCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string where = $"ABGF file {name}";
        if (lines.Count == 0) throw new FormatException($"{where}: the file is empty.");
        var head = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (head.Length is < 1 or > 2 || !int.TryParse(head[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            throw new FormatException($"{where}: the first line must hold the number of profiles and an optional absolute-weights flag.");
        double flag = 0;
        if (head.Length == 2 && !double.TryParse(head[1], NumberStyles.Float, CultureInfo.InvariantCulture, out flag))
            throw new FormatException($"{where}: the weights flag '{head[1]}' is not a number.");
        if (count < 1 || count > 20) throw new FormatException($"{where}: {count} profiles; 1 to 20 (\"The maximum number of profiles which may be specified in an ABGF file is 20\").");
        if (lines.Count - 1 != count) throw new FormatException($"{where}: the first line says {count} profiles; {lines.Count - 1} profile lines follow.");
        var terms = new List<(NscAbgProfile, double)>();
        for (int i = 1; i <= count; i++)
        {
            var t = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length != 2 || !double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
                throw new FormatException($"{where}, line {i + 1}: expected a profile name and a weight.");
            if (t[0] != t[0].ToUpperInvariant())
                throw new FormatException($"{where}, line {i + 1}: '{t[0]}': \"all profiles must be specified using upper case letters\".");
            if (!(w >= 0) || !double.IsFinite(w)) throw new FormatException($"{where}, line {i + 1}: the weight must be zero or positive.");
            var profile = catalog.Find(t[0]) ?? throw new FormatException($"{where}, line {i + 1}: the profile {t[0]} is not in the ABg catalog.");
            terms.Add((profile, w));
        }
        if (flag == 0 && !(terms.Sum(t => t.Item2) > 0)) throw new FormatException($"{where}: relative weights must not all be zero.");
        return new NscAbgSum { Name = name, Absolute = flag != 0, Terms = terms };
    }
}

/// <summary>
/// The ABg BSDF A/(B + r^g) of one row (A, B, g already wavelength-scaled) as a function of
/// r = |x| (docs/Optics-Conventions.md §74): F(R) = ∫₀^R r/(B + r^g) dr, its total integrated
/// scatter over the unit disk for a specular projection b, TIS(b) = A·∮F(ρ(φ)) dφ
/// (<see cref="NscScatterMath.DiskIntegral"/>), and exact draws of x with density ∝ 1/(B + |x|^g)
/// over |x| ≤ 2.
/// <para>
/// F has closed forms for g = 0 (R²/(2(B + 1))) and g = 2 (½ ln((B + R²)/B)) (derived). For
/// other g it is tabulated in s = ln r with step 0.01 from r_lo = 10⁻⁴·min(1, B^{1/g}) (g &gt; 0)
/// or 10⁻⁸ (g &lt; 0) to 2, each panel integrated by 10-point Gauss–Legendre in s, and read by
/// cubic Hermite interpolation with the exact derivative dF/ds = r²/(B + r^g); below r_lo it is
/// integrated directly. The interpolation error falls as h⁴; the tests compare the table with
/// direct quadrature (NscScatteringTests).
/// </para>
/// <para>
/// Draws: |x| is split into cells r_k (0, r_lo, then geometric with ratio q = e^{0.02/|g|} up to
/// 2) on which 1/(B + r^g) varies by at most e^{0.02}; a cell is chosen with probability ∝ its
/// envelope mass h_max·(r_{k+1}² − r_k²)/2, r is drawn ∝ r on the cell (r² uniform), and accepted
/// with probability h(r)/h_max. Accepted draws have density ∝ r/(B + r^g) exactly (rejection
/// sampling; derived), whatever the cell widths.
/// </para>
/// </summary>
public sealed class NscAbgKernel
{
    public double A { get; }
    public double B { get; }
    public double G { get; }

    private readonly double _sLo, _h, _fLo;
    private readonly double[]? _f;          // F at s_k = _sLo + k·_h
    private readonly double[] _edges;       // cell edges in r
    private readonly double[] _hMax;        // envelope height per cell
    private readonly double[] _cumulative;  // cumulative envelope mass

    public NscAbgKernel(double a, double b, double g)
    {
        if (!(a >= 0) || !double.IsFinite(a)) throw new ArgumentException($"ABg: A must be zero or positive (it is {a}).");
        if (!double.IsFinite(g)) throw new ArgumentException("ABg: g must be finite.");
        if (g == 0 ? !(b >= 0) : !(b > 1e-12)) throw new ArgumentException($"ABg: B must exceed 1e-12 unless g = 0 (it is {b}).");
        A = a; B = b; G = g;

        double rLo = g > 0 ? 1e-4 * Math.Min(1, Math.Pow(b, 1 / g)) : 1e-8;
        rLo = Math.Max(rLo, 1e-30);
        _sLo = Math.Log(rLo);
        _h = 0.01;
        if (g != 0 && g != 2)
        {
            _fLo = DirectF(rLo);
            int panels = (int)Math.Ceiling((Math.Log(2) - _sLo) / _h);
            _f = new double[panels + 1];
            _f[0] = _fLo;
            var (nodes, weights) = GaussLegendre.Rule(10, 0, _h);
            for (int k = 0; k < panels; k++)
            {
                double s0 = _sLo + k * _h, sum = 0;
                for (int j = 0; j < nodes.Length; j++) sum += weights[j] * DFds(s0 + nodes[j]);
                _f[k + 1] = _f[k] + sum;
            }
        }

        // Sampling cells.
        var edges = new List<double> { 0 };
        if (g == 0) edges.Add(2);
        else
        {
            edges.Add(rLo);
            double q = Math.Exp(0.02 / Math.Abs(g));
            while (edges[^1] < 2) edges.Add(Math.Min(2, edges[^1] * q));
        }
        _edges = edges.ToArray();
        _hMax = new double[_edges.Length - 1];
        _cumulative = new double[_edges.Length - 1];
        double total = 0;
        for (int k = 0; k < _hMax.Length; k++)
        {
            _hMax[k] = Math.Max(H(_edges[k]), H(_edges[k + 1]));
            total += _hMax[k] * (_edges[k + 1] * _edges[k + 1] - _edges[k] * _edges[k]) / 2;
            _cumulative[k] = total;
        }
    }

    /// <summary>1/(B + r^g), with its limit at r = 0.</summary>
    public double H(double r)
    {
        if (G == 0) return 1 / (B + 1);
        if (r == 0) return G > 0 ? 1 / B : 0;
        return 1 / (B + Math.Pow(r, G));
    }

    /// <summary>The BSDF A/(B + r^g) at r = |x|.</summary>
    public double Bsdf(double r) => A * H(r);

    private double DFds(double s)
    {
        double r = Math.Exp(s);
        return r * r * H(r);
    }

    private double DirectF(double r)
    {
        var (nodes, weights) = GaussLegendre.Rule(20, 0, r);
        double sum = 0;
        for (int j = 0; j < nodes.Length; j++) sum += weights[j] * nodes[j] * H(nodes[j]);
        return sum;
    }

    /// <summary>F(R) = ∫₀^R r/(B + r^g) dr, 0 ≤ R ≤ 2.</summary>
    public double F(double r)
    {
        if (r <= 0) return 0;
        if (G == 0) return r * r / (2 * (B + 1));
        if (G == 2) return 0.5 * Math.Log((B + r * r) / B);
        double s = Math.Log(r);
        if (s <= _sLo) return DirectF(r);
        double t = (s - _sLo) / _h;
        int k = Math.Min((int)t, _f!.Length - 2);
        double u = t - k;
        double s0 = _sLo + k * _h;
        double f0 = _f[k], f1 = _f[k + 1], d0 = DFds(s0) * _h, d1 = DFds(s0 + _h) * _h;
        double u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * f0 + (u3 - 2 * u2 + u) * d0 + (-2 * u3 + 3 * u2) * f1 + (u3 - u2) * d1;
    }

    /// <summary>The total integrated scatter for a specular projection of length b.</summary>
    public double Tis(double b) => A == 0 ? 0 : A * NscScatterMath.DiskIntegral(F, b);

    /// <summary>An exact draw of x (density ∝ 1/(B + |x|^g) over |x| ≤ 2).</summary>
    public (double X, double Y) Draw(SplitMix64 rng)
    {
        for (int attempt = 0; attempt < 1_000_000; attempt++)
        {
            double target = rng.NextDouble() * _cumulative[^1];
            int k = NscScatterMath.UpperBound(_cumulative, target);
            double r0 = _edges[k], r1 = _edges[k + 1];
            double r = Math.Sqrt(r0 * r0 + rng.NextDouble() * (r1 * r1 - r0 * r0));
            if (rng.NextDouble() * _hMax[k] > H(r)) continue;
            double phi = 2 * Math.PI * rng.NextDouble();
            return (r * Math.Cos(phi), r * Math.Sin(phi));
        }
        throw new InvalidOperationException($"ABg draw: no sample accepted in 10⁶ attempts (A = {A}, B = {B}, g = {G}).");
    }
}
