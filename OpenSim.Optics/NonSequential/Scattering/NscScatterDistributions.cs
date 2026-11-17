using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Scattering;

/// <summary>
/// Where a specular ray leaves a face, in the face's tangent frame (u, v, n), n on the specular
/// ray's side: its projection β₀ = (B0X, B0Y), the angle of incidence of the incoming ray
/// (degrees, for the ABg rows) and the angle (degrees, counter-clockwise from the object's +X)
/// of the sample rotation the tabular BSDF data set is chosen by.
/// </summary>
public readonly record struct NscScatterIncidence(double B0X, double B0Y, double IncidenceDegrees, double RotationDegrees = 0)
{
    /// <summary>|β₀| = sin of the specular angle.</summary>
    public double B0 => Math.Min(1, Math.Sqrt(B0X * B0X + B0Y * B0Y));

    /// <summary>The specular angle from the normal, degrees.</summary>
    public double SpecularDegrees => Math.Asin(B0) * 180 / Math.PI;
}

/// <summary>
/// A scatter model at one wavelength (docs/Optics-Conventions.md §74). The scattered power per
/// unit projected area of the unit disk (β) is incident power × <see cref="Bsdf"/>, with BSDF =
/// <see cref="Fraction"/> · <see cref="Weight"/> · <see cref="Density"/>: the fraction that
/// leaves the specular ray, the share of it that is scattered rather than absorbed (1 except for
/// tabular BSDF data, where it is the file's TIS) and the normalised distribution of directions
/// (∫ Density dβ over the unit disk = 1). TIS = Fraction · Weight.
/// </summary>
public abstract class NscScatterDistribution
{
    public abstract string Name { get; }

    /// <summary>The fraction of the specular ray's energy that scatters (the probability of
    /// scattering without ray splitting).</summary>
    public abstract double Fraction(in NscScatterIncidence incidence);

    /// <summary>The share of the scattered fraction carried by scattered rays.</summary>
    public virtual double Weight(in NscScatterIncidence incidence) => 1;

    /// <summary>The density of scattered directions per unit projected solid angle at β; 0
    /// outside the unit disk.</summary>
    public abstract double Density(in NscScatterIncidence incidence, double bx, double by);

    /// <summary>An exact draw of β from <see cref="Density"/>.</summary>
    public abstract (double X, double Y) Sample(in NscScatterIncidence incidence, SplitMix64 rng);

    public double Bsdf(in NscScatterIncidence incidence, double bx, double by) =>
        Fraction(incidence) * Weight(incidence) * Density(incidence, bx, by);

    public double Tis(in NscScatterIncidence incidence) => Fraction(incidence) * Weight(incidence);

    protected static bool InDisk(double bx, double by) => bx * bx + by * by <= 1;

    internal static string Num(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}

/// <summary>Lambertian: "the scattered ray projection vector β has equal probability anywhere in
/// the unit circle, and the BSDF is just 1/π" (help, "Available Scatter Models"), times the
/// fraction to scatter.</summary>
public sealed class NscLambertianScatter : NscScatterDistribution
{
    private readonly double _fraction;
    public NscLambertianScatter(double fraction) => _fraction = fraction;
    public override string Name => $"Lambertian (fraction {Num(_fraction)})";
    public override double Fraction(in NscScatterIncidence incidence) => _fraction;
    public override double Density(in NscScatterIncidence incidence, double bx, double by) => InDisk(bx, by) ? 1 / Math.PI : 0;

    public override (double X, double Y) Sample(in NscScatterIncidence incidence, SplitMix64 rng)
    {
        double r = Math.Sqrt(rng.NextDouble()), phi = 2 * Math.PI * rng.NextDouble();
        return (r * Math.Cos(phi), r * Math.Sin(phi));
    }
}

/// <summary>Gaussian: BSDF(x) = A·exp(−|x|²/σ²) "where A is a normalizing constant" (help,
/// "Available Scatter Models"), normalised so the TIS is the fraction to scatter ("For all scatter
/// models other than ABg, the BSDF is normalized to yield a TIS equal to the fraction to scatter
/// parameter", "Bi-Directional Scatter Distribution Function"); the normalisation over the unit
/// disk depends on |β₀| and is ∮ (σ²/2)(1 − e^{−ρ²/σ²}) dφ (derived: ∫₀^R e^{−r²/σ²} r dr =
/// (σ²/2)(1 − e^{−R²/σ²})). Draws: x from the 2-D normal with standard deviation σ/√2 per axis,
/// kept if β₀ + x lies in the unit disk (exact by rejection).</summary>
public sealed class NscGaussianScatter : NscScatterDistribution
{
    private readonly double _fraction, _sigma;

    public NscGaussianScatter(double fraction, double sigma)
    {
        if (!(sigma > 0 && sigma <= 5)) throw new ArgumentException($"Gaussian scatter: σ must lie in (0, 5]; it is {sigma}.");
        _fraction = fraction;
        _sigma = sigma;
    }

    public double Sigma => _sigma;
    public override string Name => $"Gaussian (σ {Num(_sigma)}, fraction {Num(_fraction)})";
    public override double Fraction(in NscScatterIncidence incidence) => _fraction;

    /// <summary>∫ e^{−|β − β₀|²/σ²} dβ over the unit disk.</summary>
    public double Normalization(double b)
    {
        double s2 = _sigma * _sigma;
        return NscScatterMath.DiskIntegral(r => 0.5 * s2 * OneMinusExp(r * r / s2), b);
    }

    /// <summary>1 − e^(−t) without cancellation for small t (series to t³ below 1e-4; derived).</summary>
    private static double OneMinusExp(double t) => t < 1e-4 ? t * (1 - t / 2 * (1 - t / 3)) : 1 - Math.Exp(-t);

    public override double Density(in NscScatterIncidence incidence, double bx, double by)
    {
        if (!InDisk(bx, by)) return 0;
        double dx = bx - incidence.B0X, dy = by - incidence.B0Y;
        return Math.Exp(-(dx * dx + dy * dy) / (_sigma * _sigma)) / Normalization(incidence.B0);
    }

    public override (double X, double Y) Sample(in NscScatterIncidence incidence, SplitMix64 rng)
    {
        double s = _sigma / Math.Sqrt(2);
        for (int attempt = 0; attempt < 1_000_000; attempt++)
        {
            double x = incidence.B0X + s * rng.NextNormal(), y = incidence.B0Y + s * rng.NextNormal();
            if (InDisk(x, y)) return (x, y);
        }
        throw new InvalidOperationException($"Gaussian scatter: no draw landed in the unit disk in 10⁶ attempts (σ = {_sigma}, |β₀| = {incidence.B0}).");
    }
}

/// <summary>
/// One ABg profile at one wavelength (help, "Available Scatter Models", "ABg Scatter Data
/// Catalogs"): its rows' kernels; between two rows the BSDF is interpolated linearly in the cosine
/// of the angle of incidence (a mixture of the two rows' distributions with those weights; exact),
/// outside the rows the first or last row is used. The fraction is the TIS; "The TIS must be less
/// than 1.0", so a TIS of 1 or more throws, naming the profile and the angle.
/// </summary>
public sealed class NscAbgScatter : NscScatterDistribution
{
    private readonly string _name;
    private readonly double[] _cosAngles;
    private readonly NscAbgKernel[] _kernels;

    public NscAbgScatter(NscAbgProfile profile, double micrometers)
    {
        _name = profile.Name;
        var rows = profile.RowsAt(micrometers);
        _cosAngles = rows.Select(r => Math.Cos(r.AngleDegrees * Math.PI / 180)).ToArray();
        _kernels = rows.Select(r => new NscAbgKernel(r.A, r.B, r.G)).ToArray();
    }

    public IReadOnlyList<NscAbgKernel> Kernels => _kernels;
    public override string Name => $"ABg {_name}";

    /// <summary>The rows used at an angle of incidence and their weights.</summary>
    private (int I, double Wi, int J, double Wj) Rows(double incidenceDegrees)
    {
        double c = Math.Cos(incidenceDegrees * Math.PI / 180);
        int n = _kernels.Length;
        if (n == 1 || c >= _cosAngles[0]) return (0, 1, 0, 0);
        if (c <= _cosAngles[^1]) return (n - 1, 1, n - 1, 0);
        int k = 0;
        while (!(c <= _cosAngles[k] && c >= _cosAngles[k + 1])) k++;
        double t = (_cosAngles[k] - c) / (_cosAngles[k] - _cosAngles[k + 1]);
        return (k, 1 - t, k + 1, t);
    }

    public override double Fraction(in NscScatterIncidence incidence)
    {
        var (i, wi, j, wj) = Rows(incidence.IncidenceDegrees);
        double b = incidence.B0;
        double tis = wi * _kernels[i].Tis(b) + (wj > 0 ? wj * _kernels[j].Tis(b) : 0);
        if (!(tis < 1))
            throw new InvalidOperationException($"ABg profile {_name}: the total integrated scatter is {Num(tis)} at incidence {Num(incidence.IncidenceDegrees)}° (specular {Num(incidence.SpecularDegrees)}°); it must be less than 1 to conserve energy (help, \"ABg Scatter Data Catalogs\").");
        return tis;
    }

    public override double Density(in NscScatterIncidence incidence, double bx, double by)
    {
        if (!InDisk(bx, by)) return 0;
        var (i, wi, j, wj) = Rows(incidence.IncidenceDegrees);
        double r = Math.Sqrt((bx - incidence.B0X) * (bx - incidence.B0X) + (by - incidence.B0Y) * (by - incidence.B0Y));
        double bsdf = wi * _kernels[i].Bsdf(r) + (wj > 0 ? wj * _kernels[j].Bsdf(r) : 0);
        double tis = Fraction(incidence);
        return tis > 0 ? bsdf / tis : 0;
    }

    public override (double X, double Y) Sample(in NscScatterIncidence incidence, SplitMix64 rng)
    {
        var (i, wi, j, wj) = Rows(incidence.IncidenceDegrees);
        double b = incidence.B0;
        var kernel = _kernels[i];
        if (wj > 0)
        {
            double ti = wi * _kernels[i].Tis(b), tj = wj * _kernels[j].Tis(b);
            if (rng.NextDouble() * (ti + tj) >= ti) kernel = _kernels[j];
        }
        return DrawInDisk(kernel, incidence, rng, _name);
    }

    internal static (double X, double Y) DrawInDisk(NscAbgKernel kernel, in NscScatterIncidence incidence, SplitMix64 rng, string name)
    {
        for (int attempt = 0; attempt < 1_000_000; attempt++)
        {
            var (x, y) = kernel.Draw(rng);
            double bx = incidence.B0X + x, by = incidence.B0Y + y;
            if (InDisk(bx, by)) return (bx, by);
        }
        throw new InvalidOperationException($"ABg {name}: no draw landed in the unit disk in 10⁶ attempts.");
    }
}

/// <summary>ABg File: the weighted sum of profiles (help, "ABg File Scattering"), a mixture of the
/// profiles' distributions with weights coefficient × TIS (exact). Its TIS must stay below 1.</summary>
public sealed class NscAbgSumScatter : NscScatterDistribution
{
    private readonly string _name;
    private readonly double[] _coefficients;
    private readonly NscAbgScatter[] _parts;

    public NscAbgSumScatter(NscAbgSum sum, double micrometers)
    {
        _name = sum.Name;
        _coefficients = sum.Coefficients();
        _parts = sum.Terms.Select(t => new NscAbgScatter(t.Profile, micrometers)).ToArray();
    }

    public override string Name => $"ABg File {_name}";

    private double[] Parts(in NscScatterIncidence incidence)
    {
        var t = new double[_parts.Length];
        for (int k = 0; k < t.Length; k++) t[k] = _coefficients[k] == 0 ? 0 : _coefficients[k] * _parts[k].Fraction(incidence);
        return t;
    }

    public override double Fraction(in NscScatterIncidence incidence)
    {
        double tis = Parts(incidence).Sum();
        if (!(tis < 1))
            throw new InvalidOperationException($"ABg File {_name}: the total integrated scatter of the sum is {Num(tis)} at incidence {Num(incidence.IncidenceDegrees)}°; it must be less than 1 (help, \"ABg File Scattering\").");
        return tis;
    }

    public override double Density(in NscScatterIncidence incidence, double bx, double by)
    {
        var t = Parts(incidence);
        double total = t.Sum(), d = 0;
        if (!(total > 0)) return 0;
        for (int k = 0; k < t.Length; k++) if (t[k] > 0) d += t[k] * _parts[k].Density(incidence, bx, by);
        return d / total;
    }

    public override (double X, double Y) Sample(in NscScatterIncidence incidence, SplitMix64 rng)
    {
        var t = Parts(incidence);
        double u = rng.NextDouble() * t.Sum();
        int k = 0;
        while (k < t.Length - 1 && (u >= t[k] || t[k] == 0)) { u -= t[k]; k++; }
        return _parts[k].Sample(incidence, rng);
    }
}

/// <summary>Tabular BSDF data (<see cref="NscBsdfTable"/>): fraction to scatter × the file's TIS
/// are scattered, the rest of the fraction absorbed; directions from the nearest data set.</summary>
public sealed class NscTableScatter : NscScatterDistribution
{
    private readonly NscBsdfTable _table;
    private readonly double _fraction, _angle;

    /// <param name="angleDegrees">The BSDF "Angle" of the face.</param>
    public NscTableScatter(NscBsdfTable table, double fraction, double angleDegrees)
    {
        _table = table;
        _fraction = fraction;
        _angle = angleDegrees;
    }

    public override string Name => $"BSDF {_table.Name}";
    public override double Fraction(in NscScatterIncidence incidence) => _fraction;

    private (int R, int A) Set(in NscScatterIncidence incidence) => _table.Select(incidence.RotationDegrees - _angle, incidence.SpecularDegrees);

    public override double Weight(in NscScatterIncidence incidence)
    {
        var (r, a) = Set(incidence);
        return _table.Tis[r, a];
    }

    /// <summary>The specular direction r and e₁, e₂ about it in the tangent frame (n = z).</summary>
    private static (Vector3D R, Vector3D E1, Vector3D E2) Frame(in NscScatterIncidence incidence)
    {
        var r = new Vector3D(incidence.B0X, incidence.B0Y, Math.Sqrt(Math.Max(0, 1 - incidence.B0 * incidence.B0)));
        var toward = Vector3D.UnitZ - r * r.Z;
        var e1 = toward.Length > 1e-12 ? toward / toward.Length : -Vector3D.UnitX;
        return (r, e1, Vector3D.Cross(r, e1));
    }

    public override double Density(in NscScatterIncidence incidence, double bx, double by)
    {
        if (!InDisk(bx, by)) return 0;
        var (rot, set) = Set(incidence);
        var (r, e1, e2) = Frame(incidence);
        double cz = Math.Sqrt(Math.Max(0, 1 - bx * bx - by * by));
        if (!(cz > 0)) return 0;
        var w = new Vector3D(bx, by, cz);
        double radial = Math.Acos(Math.Clamp(Vector3D.Dot(w, r), -1, 1)) * 180 / Math.PI;
        double azimuth = Math.Atan2(Vector3D.Dot(w, e2), Vector3D.Dot(w, e1)) * 180 / Math.PI;
        if (azimuth < 0) azimuth += 360;
        return _table.BlockAt(rot, set).DensityPerSteradian(radial, azimuth) / cz;
    }

    /// <summary>The table's own BSDF value at a direction (for the plots).</summary>
    public double TableValue(in NscScatterIncidence incidence, double bx, double by)
    {
        if (!InDisk(bx, by)) return 0;
        var (rot, set) = Set(incidence);
        var (r, e1, e2) = Frame(incidence);
        var w = new Vector3D(bx, by, Math.Sqrt(Math.Max(0, 1 - bx * bx - by * by)));
        double radial = Math.Acos(Math.Clamp(Vector3D.Dot(w, r), -1, 1)) * 180 / Math.PI;
        double azimuth = Math.Atan2(Vector3D.Dot(w, e2), Vector3D.Dot(w, e1)) * 180 / Math.PI;
        if (azimuth < 0) azimuth += 360;
        return _table.BlockAt(rot, set).Value(radial, azimuth);
    }

    public override (double X, double Y) Sample(in NscScatterIncidence incidence, SplitMix64 rng)
    {
        var (rot, set) = Set(incidence);
        var block = _table.BlockAt(rot, set);
        if (!(block.Normalization > 0))
            throw new InvalidOperationException($"BSDF file {_table.Name}: the data set for rotation {Num(_table.Rotations[rot])}° and incidence {Num(_table.Incidences[set])}° has no scatter above the surface.");
        var (r, e1, e2) = Frame(incidence);
        for (int attempt = 0; attempt < 100_000; attempt++)
        {
            var (theta, phi) = block.Draw(rng);
            var w = r * Math.Cos(theta) + (e1 * Math.Cos(phi) + e2 * Math.Sin(phi)) * Math.Sin(theta);
            if (w.Z > 0) return (w.X, w.Y);
        }
        throw new InvalidOperationException($"BSDF file {_table.Name}: no scattered direction above the surface in 10⁵ draws (specular {Num(incidence.SpecularDegrees)}°).");
    }
}
