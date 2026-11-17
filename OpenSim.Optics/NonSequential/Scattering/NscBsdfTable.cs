using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Scattering;

/// <summary>The "Symmetry" line of a tabular BSDF file.</summary>
public enum NscBsdfSymmetry
{
    /// <summary>Azimuth 0–180° reported; the other half is its mirror image.</summary>
    PlaneSymmetrical,
    /// <summary>Azimuth 0–360°, one sample rotation.</summary>
    Asymmetrical,
    /// <summary>Azimuth 0–360°, several sample rotations.</summary>
    Asymmetrical4D,
}

/// <summary>
/// A Zemax tabular BSDF file (".BSDF"; Knowledge Base, "Zemax Tabular BSDF data file format",
/// R. Rykowski; source notes in plans/sources/optics/op-80-help/README-op80.txt): sample
/// rotations, angles of incidence (the angle of the specular ray), scatter azimuths and radial
/// angles (degrees), and for each rotation and angle of incidence a TIS and a table of BSDF
/// values, one row per azimuth, one column per radial angle. Radial 0° is the specular direction;
/// azimuth 0° points from the specular direction toward the sample normal.
/// <para>
/// The scattering model built on it (docs/Optics-Conventions.md §74): the data set of the nearest
/// rotation and the nearest angle of incidence is used ("the closest angle values are chosen";
/// OpticStudio interpolates between rotations, this program takes the nearest, which is listed
/// as a difference). Within it the BSDF is constant on each cell between neighbouring azimuths and
/// radial angles (the mean of its four corners); nothing outside the tabulated range scatters
/// ("Data is not extrapolated"). A scattered direction has density ∝ BSDF·cos θ_s per steradian,
/// cos θ_s taken at the cell centre for the data set's own angle of incidence; the scattered
/// power is the incident power × fraction × the file's TIS for that angle ("the power of the
/// scattered rays will be scaled by the appropriate TIS value"); the rest of the fraction is
/// absorbed. Of a Tristimulus (XYZ) file only the Y group is read ("OpticStudio will only read
/// the data for the Tristimulus Y").
/// </para>
/// </summary>
public sealed class NscBsdfTable
{
    public string Name { get; }
    public NscBsdfSymmetry Symmetry { get; }
    public double[] Rotations { get; }
    public double[] Incidences { get; }
    public double[] Azimuths { get; }
    public double[] Radials { get; }

    /// <summary>TIS by [rotation, incidence].</summary>
    public double[,] Tis { get; }

    /// <summary>BSDF by [rotation][incidence][azimuth, radial], per steradian.</summary>
    public double[][][,] Data { get; }

    private readonly Block[,] _blocks;

    private NscBsdfTable(string name, NscBsdfSymmetry symmetry, double[] rotations, double[] incidences, double[] azimuths,
        double[] radials, double[,] tis, double[][][,] data)
    {
        Name = name; Symmetry = symmetry; Rotations = rotations; Incidences = incidences; Azimuths = azimuths;
        Radials = radials; Tis = tis; Data = data;
        _blocks = new Block[rotations.Length, incidences.Length];
        for (int r = 0; r < rotations.Length; r++)
            for (int a = 0; a < incidences.Length; a++)
            {
                _blocks[r, a] = new Block(this, data[r][a], incidences[a]);
                if (!(_blocks[r, a].Normalization > 0) && tis[r, a] > 0)
                    throw new FormatException($"BSDF file {name}: rotation {Num(rotations[r])}°, incidence {Num(incidences[a])}°: the table has no scatter above the surface but its TIS is {Num(tis[r, a])}.");
            }
    }

    private static string Num(double v) => v.ToString("g6", CultureInfo.InvariantCulture);

    /// <summary>Reads a tabular BSDF file's text; throws <see cref="FormatException"/> naming the
    /// problem.</summary>
    public static NscBsdfTable Parse(string text, string name)
    {
        string where = $"BSDF file {name}";
        var tokens = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            tokens.AddRange(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        int at = 0;
        string Next(string what) => at < tokens.Count ? tokens[at++] : throw new FormatException($"{where}: the file ends before {what}.");
        void Expect(string key)
        {
            string t = Next(key);
            if (!string.Equals(t, key, StringComparison.OrdinalIgnoreCase)) throw new FormatException($"{where}: expected '{key}', found '{t}'.");
        }
        double Number(string what)
        {
            string t = Next(what);
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
                ? v : throw new FormatException($"{where}: {what}: '{t}' is not a number.");
        }
        int Count(string key, int max)
        {
            Expect(key);
            string t = Next(key);
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                throw new FormatException($"{where}: {key} must be followed by a positive integer; found '{t}'.");
            if (n > max) throw new FormatException($"{where}: {n} {key} values; at most {max}.");
            return n;
        }
        double[] Enumeration(string key, int max, double upper)
        {
            int n = Count(key, max);
            var v = new double[n];
            for (int i = 0; i < n; i++)
            {
                v[i] = Number(key);
                if (!(v[i] >= 0 && v[i] <= upper)) throw new FormatException($"{where}: {key} value {Num(v[i])} is not in 0–{Num(upper)}.");
                if (i > 0 && !(v[i] > v[i - 1])) throw new FormatException($"{where}: the {key} values must increase ({Num(v[i - 1])} then {Num(v[i])}).");
            }
            return v;
        }

        Expect("Source");
        Next("the source");
        Expect("Symmetry");
        string sym = Next("the symmetry");
        var symmetry = sym.ToLowerInvariant() switch
        {
            "planesymmetrical" => NscBsdfSymmetry.PlaneSymmetrical,
            "asymmetrical" => NscBsdfSymmetry.Asymmetrical,
            "asymmetrical4d" => NscBsdfSymmetry.Asymmetrical4D,
            _ => throw new FormatException($"{where}: Symmetry '{sym}' is not PlaneSymmetrical, Asymmetrical or Asymmetrical4D."),
        };
        Expect("SpectralContent");
        string spectral = Next("the spectral content");
        bool xyz = string.Equals(spectral, "XYZ", StringComparison.OrdinalIgnoreCase);
        if (!xyz && !string.Equals(spectral, "Monochrome", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"{where}: SpectralContent '{spectral}' is not Monochrome or XYZ.");
        Expect("ScatterType");
        Next("the scatter type");   // "this data is not used by OpticStudio"
        var rotations = Enumeration("SampleRotation", 50, 360);
        if (symmetry != NscBsdfSymmetry.Asymmetrical4D && rotations.Length != 1)
            throw new FormatException($"{where}: {rotations.Length} sample rotations with Symmetry {sym}; several rotations need Asymmetrical4D.");
        var incidences = Enumeration("AngleOfIncidence", 100, 90);
        var azimuths = Enumeration("ScatterAzimuth", 1000, symmetry == NscBsdfSymmetry.PlaneSymmetrical ? 180 : 360);
        var radials = Enumeration("ScatterRadial", 1000, 180);
        if (azimuths.Length < 2 || radials.Length < 2)
            throw new FormatException($"{where}: at least two azimuths and two radial angles are needed to form a table.");

        int groups = xyz ? 3 : 1;
        double[,]? tis = null;
        double[][][,]? data = null;
        for (int group = 0; group < groups; group++)
        {
            string label = Next("the spectral group label");
            if (string.Equals(label, "DataBegin", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"{where}: a spectral content line (Monochrome, TristimulusX/Y/Z) must precede DataBegin.");
            Expect("DataBegin");
            var t = new double[rotations.Length, incidences.Length];
            var d = new double[rotations.Length][][,];
            for (int r = 0; r < rotations.Length; r++)
            {
                d[r] = new double[incidences.Length][,];
                for (int a = 0; a < incidences.Length; a++)
                {
                    Expect("TIS");
                    t[r, a] = Number("TIS");
                    if (!(t[r, a] >= 0 && t[r, a] <= 1)) throw new FormatException($"{where}: TIS {Num(t[r, a])} is not a fraction in 0–1.");
                    var table = new double[azimuths.Length, radials.Length];
                    for (int i = 0; i < azimuths.Length; i++)
                        for (int j = 0; j < radials.Length; j++)
                        {
                            table[i, j] = Number($"the BSDF table (rotation {Num(rotations[r])}°, incidence {Num(incidences[a])}°)");
                            if (!(table[i, j] >= 0)) throw new FormatException($"{where}: a negative BSDF value {Num(table[i, j])}.");
                        }
                    d[r][a] = table;
                }
            }
            Expect("DataEnd");
            // Monochrome: the only group. XYZ: the Y group (the second, or the one labelled Y).
            bool take = !xyz || label.EndsWith("Y", StringComparison.OrdinalIgnoreCase) || (group == 1 && tis is null);
            if (take && tis is null) { tis = t; data = d; }
        }
        return new NscBsdfTable(name, symmetry, rotations, incidences, azimuths, radials, tis!, data!);
    }

    private static int Nearest(double[] values, double x, bool circular)
    {
        int best = 0;
        double bestD = double.PositiveInfinity;
        for (int i = 0; i < values.Length; i++)
        {
            double d = Math.Abs(values[i] - x);
            if (circular) d = Math.Min(d, 360 - d);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>The data set for a sample rotation and a specular angle (degrees).</summary>
    internal (int Rotation, int Incidence) Select(double rotationDegrees, double specularDegrees)
    {
        double rot = ((rotationDegrees % 360) + 360) % 360;
        return (Nearest(Rotations, rot, circular: true), Nearest(Incidences, specularDegrees, circular: false));
    }

    internal Block BlockAt(int rotation, int incidence) => _blocks[rotation, incidence];

    /// <summary>
    /// One data set as a sampler. Angles about the specular ray r: ω = cos θ_r r + sin θ_r (cos φ e₁
    /// + sin φ e₂), e₁ the unit vector from r toward the normal, e₂ = r × e₁. For a data set of
    /// incidence θ_b, cos θ_s = cos θ_r cos θ_b + sin θ_r sin θ_b cos φ (derived).
    /// </summary>
    internal sealed class Block
    {
        private readonly NscBsdfTable _t;
        private readonly double[,] _value;      // cell values [azimuth cell, radial cell]
        private readonly double[] _cumulative;  // cumulative cell weights, azimuth-major
        private readonly double[,] _cosCentre;

        /// <summary>∫ BSDF cos θ_s dΩ over the tabulated directions above the surface (both
        /// mirror halves for plane-symmetric data), for the data set's own incidence.</summary>
        public double Normalization { get; }

        public Block(NscBsdfTable t, double[,] data, double incidence)
        {
            _t = t;
            int na = t.Azimuths.Length - 1, nr = t.Radials.Length - 1;
            _value = new double[na, nr];
            _cosCentre = new double[na, nr];
            _cumulative = new double[na * nr];
            double sb = Math.Sin(incidence * Math.PI / 180), cb = Math.Cos(incidence * Math.PI / 180);
            double total = 0;
            double mirror = t.Symmetry == NscBsdfSymmetry.PlaneSymmetrical ? 2 : 1;
            for (int i = 0; i < na; i++)
                for (int j = 0; j < nr; j++)
                {
                    double v = 0.25 * (data[i, j] + data[i + 1, j] + data[i, j + 1] + data[i + 1, j + 1]);
                    double tr = 0.5 * (t.Radials[j] + t.Radials[j + 1]) * Math.PI / 180;
                    double ph = 0.5 * (t.Azimuths[i] + t.Azimuths[i + 1]) * Math.PI / 180;
                    double cs = Math.Max(0, Math.Cos(tr) * cb + Math.Sin(tr) * sb * Math.Cos(ph));
                    double solid = (Math.Cos(t.Radials[j] * Math.PI / 180) - Math.Cos(t.Radials[j + 1] * Math.PI / 180))
                        * (t.Azimuths[i + 1] - t.Azimuths[i]) * Math.PI / 180;
                    _value[i, j] = v;
                    _cosCentre[i, j] = cs;
                    total += v * cs * solid * mirror;
                    _cumulative[i * nr + j] = total;
                }
            Normalization = total;
        }

        /// <summary>The cell's BSDF at angles (θ_r, φ) in degrees, φ in [0, 360); 0 outside the table.</summary>
        public double Value(double radialDegrees, double azimuthDegrees)
        {
            if (_t.Symmetry == NscBsdfSymmetry.PlaneSymmetrical && azimuthDegrees > 180) azimuthDegrees = 360 - azimuthDegrees;
            int i = Cell(_t.Azimuths, azimuthDegrees), j = Cell(_t.Radials, radialDegrees);
            return i < 0 || j < 0 ? 0 : _value[i, j];
        }

        /// <summary>The sampling density per steradian (∝ BSDF·cos θ_s at the cell centre).</summary>
        public double DensityPerSteradian(double radialDegrees, double azimuthDegrees)
        {
            if (_t.Symmetry == NscBsdfSymmetry.PlaneSymmetrical && azimuthDegrees > 180) azimuthDegrees = 360 - azimuthDegrees;
            int i = Cell(_t.Azimuths, azimuthDegrees), j = Cell(_t.Radials, radialDegrees);
            return i < 0 || j < 0 || !(Normalization > 0) ? 0 : _value[i, j] * _cosCentre[i, j] / Normalization;
        }

        private static int Cell(double[] edges, double x)
        {
            if (x < edges[0] || x > edges[^1]) return -1;
            int k = Array.BinarySearch(edges, x);
            if (k < 0) k = ~k - 1;
            return Math.Min(k, edges.Length - 2);
        }

        /// <summary>Draws (θ_r, φ) in radians from the cell weights.</summary>
        public (double Radial, double Azimuth) Draw(SplitMix64 rng)
        {
            int nr = _t.Radials.Length - 1;
            double target = rng.NextDouble() * _cumulative[^1];
            int k = NscScatterMath.UpperBound(_cumulative, target);   // never a zero-weight cell
            int i = k / nr, j = k % nr;
            double c0 = Math.Cos(_t.Radials[j] * Math.PI / 180), c1 = Math.Cos(_t.Radials[j + 1] * Math.PI / 180);
            double ct = c0 + rng.NextDouble() * (c1 - c0);
            double phi = (_t.Azimuths[i] + rng.NextDouble() * (_t.Azimuths[i + 1] - _t.Azimuths[i])) * Math.PI / 180;
            if (_t.Symmetry == NscBsdfSymmetry.PlaneSymmetrical && rng.NextDouble() < 0.5) phi = 2 * Math.PI - phi;
            return (Math.Acos(Math.Clamp(ct, -1, 1)), phi);
        }
    }
}
