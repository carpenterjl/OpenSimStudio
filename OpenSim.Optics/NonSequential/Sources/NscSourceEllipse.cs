using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// Source Ellipse and Source Rectangle (help, "Source Ellipse"; "Source Rectangle": "The
/// parameters are identical to the Source Ellipse, but the shape of the source is a rectangle"):
/// a flat emitter in the local XY plane at z = 0. "The origin of each ray launched lies on the
/// surface ... with a uniform distribution"; the parameters after 1–5 are
/// 6 X Half Width, 7 Y Half Width, 8 Source Distance, 9 Cosine Exponent Cn, 10 Gaussian Gx,
/// 11 Gaussian Gy, 12 Source X, 13 Source Y, 14 Min X Half Width, 15 Min Y Half Width.
/// <list type="bullet">
/// <item><b>Cosine</b> (Cn &gt; 0): I(θ) ∝ cos^Cn θ about the local z axis ("If Cn = 1.0, the
/// distribution is Lambertian"). Per unit cos θ the density is μ^Cn on [0, 1], so
/// cos θ = a^(1/(Cn+1)), φ = 2πb (derived). The help says the cosine mode applies "If Cn is 1.0
/// or greater" and allows 0 ≤ Cn ≤ 100; a Cn between 0 and 1 is taken as a cosine too (a reading).
/// OpticStudio samples these cosines at random only; this inversion takes Sobol points as well.</item>
/// <item><b>Gaussian</b> (Cn = 0, Gx or Gy &gt; 0): I(l, m) ∝ exp(−(Gx·l² + Gy·m²)), l and m the
/// x and y direction cosines, taken as intensity per solid angle over the forward hemisphere.
/// Sampled by rejection from the per-ray stream: a direction uniform in solid angle over the
/// hemisphere is kept with probability exp(−(Gx·l² + Gy·m²)) ≤ 1 (exact; about 2√(Gx·Gy) tries
/// per ray when both are large).</item>
/// <item><b>Point</b> (Cn = Gx = Gy = 0): every ray comes from (Source X, Source Y, −d), d the
/// Source Distance ("If positive, the apparent source point is behind the object"); for a
/// negative d the rays converge toward (Source X, Source Y, |d|) (a reading). With d = 0 the rays
/// are collimated with direction cosines (Source X, Source Y, √(1 − X² − Y²)).</item>
/// <item><b>Annulus</b>: Min X and Min Y half widths both above zero remove the inner ellipse
/// (rectangle) of those half widths (with either one zero the inner region has no area, so
/// nothing is removed); start points are then drawn by rejection from the per-ray
/// stream ("The ray coordinates do not support Sobol sampling when this parameter is not
/// zero").</item>
/// </list>
/// </summary>
public class NscSourceEllipse : NscSource
{
    public double HalfWidthX { get; }
    public double HalfWidthY { get; }
    public double SourceDistance { get; }
    public double CosineExponent { get; }
    public double GaussianX { get; }
    public double GaussianY { get; }
    public double SourceX { get; }
    public double SourceY { get; }
    public double MinHalfWidthX { get; }
    public double MinHalfWidthY { get; }

    /// <summary>True for Source Rectangle.</summary>
    public bool IsRectangle { get; }

    public override string TypeName => IsRectangle ? "Source Rectangle" : "Source Ellipse";
    public override Aabb LocalBounds => new(new Vector3D(-HalfWidthX, -HalfWidthY, 0), new Vector3D(HalfWidthX, HalfWidthY, 0));
    public override int Dimensions => 4;

    /// <summary>The direction mode the parameters select.</summary>
    public string Mode => CosineExponent > 0 ? "cosine" : GaussianX > 0 || GaussianY > 0 ? "Gaussian" : SourceDistance == 0 ? "collimated" : "point";

    private bool Annular => MinHalfWidthX > 0 && MinHalfWidthY > 0;

    public override string Distribution =>
        $"{(IsRectangle ? "rectangle" : "ellipse")}{(Annular ? " annulus" : "")}, {Mode} directions";

    /// <param name="halfWidthX">Meters, ≥ 0.</param>
    /// <param name="halfWidthY">Meters, ≥ 0.</param>
    /// <param name="sourceDistance">Meters.</param>
    /// <param name="sourceX">Meters, or a direction cosine when <paramref name="sourceDistance"/> is 0.</param>
    /// <param name="sourceY">As <paramref name="sourceX"/>.</param>
    public NscSourceEllipse(double halfWidthX, double halfWidthY, double sourceDistance = 0, double cosineExponent = 0,
        double gaussianX = 0, double gaussianY = 0, double sourceX = 0, double sourceY = 0,
        double minHalfWidthX = 0, double minHalfWidthY = 0, NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : this(false, halfWidthX, halfWidthY, sourceDistance, cosineExponent, gaussianX, gaussianY, sourceX, sourceY, minHalfWidthX, minHalfWidthY, common, properties)
    {
    }

    protected NscSourceEllipse(bool rectangle, double halfWidthX, double halfWidthY, double sourceDistance, double cosineExponent,
        double gaussianX, double gaussianY, double sourceX, double sourceY,
        double minHalfWidthX, double minHalfWidthY, NscSourceCommon? common, NscSourceProperties? properties)
        : base(common, properties)
    {
        IsRectangle = rectangle;
        string type = TypeName;
        HalfWidthX = Need(halfWidthX, "the X half width (meters)", type, 0);
        HalfWidthY = Need(halfWidthY, "the Y half width (meters)", type, 0);
        SourceDistance = Need(sourceDistance, "the source distance (meters)", type);
        CosineExponent = Need(cosineExponent, "the cosine exponent", type, 0, 100);
        GaussianX = Need(gaussianX, "Gaussian Gx", type, 0);
        GaussianY = Need(gaussianY, "Gaussian Gy", type, 0);
        SourceX = Need(sourceX, "Source X", type);
        SourceY = Need(sourceY, "Source Y", type);
        MinHalfWidthX = Need(minHalfWidthX, "the minimum X half width (meters)", type, 0);
        MinHalfWidthY = Need(minHalfWidthY, "the minimum Y half width (meters)", type, 0);
        if (MinHalfWidthX > 0 && !(MinHalfWidthX < HalfWidthX) || MinHalfWidthY > 0 && !(MinHalfWidthY < HalfWidthY))
            throw new ArgumentException($"{type}: the minimum half widths ({minHalfWidthX}, {minHalfWidthY}) must be less than the half widths ({halfWidthX}, {halfWidthY}) to define an annulus.");
        if (Mode == "collimated" && SourceX * SourceX + SourceY * SourceY > 1)
            throw new ArgumentException($"{type}: with a source distance of 0 the rays are collimated and Source X, Source Y ({sourceX}, {sourceY}) are direction cosines; X² + Y² must not exceed 1.");
    }

    public override void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction)
    {
        double x, y;
        if (Annular)
        {
            long tries = 0;
            while (true)
            {
                (x, y) = Outer(extra.NextDouble(), extra.NextDouble());
                if (!InHole(x, y)) break;
                if (++tries > 100_000_000)
                    throw new InvalidOperationException($"{TypeName}: no start point outside the inner region after 10⁸ tries; the annulus is too thin.");
            }
        }
        else
        {
            (x, y) = Outer(u[0], u[1]);
        }
        position = new Vector3D(x, y, 0);

        if (CosineExponent > 0)
        {
            direction = FromPolar(Math.Pow(1 - u[2], 1 / (CosineExponent + 1)), u[3]);
        }
        else if (GaussianX > 0 || GaussianY > 0)
        {
            long tries = 0;
            while (true)
            {
                var d = FromPolar(1 - extra.NextDouble(), extra.NextDouble());
                if (extra.NextDouble() < Math.Exp(-(GaussianX * d.X * d.X + GaussianY * d.Y * d.Y))) { direction = d; break; }
                if (++tries > 100_000_000)
                    throw new InvalidOperationException($"{TypeName}: no Gaussian direction accepted after 10⁸ tries; Gx = {GaussianX}, Gy = {GaussianY} are too large.");
            }
        }
        else if (SourceDistance == 0)
        {
            direction = new Vector3D(SourceX, SourceY, Math.Sqrt(Math.Max(0, 1 - SourceX * SourceX - SourceY * SourceY)));
        }
        else
        {
            double s = Math.Sign(SourceDistance);
            direction = new Vector3D(s * (x - SourceX), s * (y - SourceY), s * SourceDistance);
        }
    }

    private (double, double) Outer(double a, double b) =>
        IsRectangle ? InRectangle(HalfWidthX, HalfWidthY, a, b) : InEllipse(HalfWidthX, HalfWidthY, a, b);

    private bool InHole(double x, double y)
    {
        if (!Annular) return false;
        if (IsRectangle) return Math.Abs(x) < MinHalfWidthX && Math.Abs(y) < MinHalfWidthY;
        double p = x / MinHalfWidthX, q = y / MinHalfWidthY;
        return p * p + q * q < 1;
    }

    public override string Describe(Func<double, string> length) =>
        $"half widths {length(HalfWidthX)} × {length(HalfWidthY)}, {Mode} ({(Mode == "cosine" ? "Cn " + CosineExponent.ToString("g6", CultureInfo.InvariantCulture) : Mode == "Gaussian" ? $"Gx {GaussianX.ToString("g6", CultureInfo.InvariantCulture)}, Gy {GaussianY.ToString("g6", CultureInfo.InvariantCulture)}" : "distance " + length(SourceDistance))})";
}

/// <summary>Source Rectangle (help, "Source Rectangle"): the parameters of
/// <see cref="NscSourceEllipse"/> on a rectangle |x| ≤ X Half Width, |y| ≤ Y Half Width.</summary>
public sealed class NscSourceRectangle : NscSourceEllipse
{
    public NscSourceRectangle(double halfWidthX, double halfWidthY, double sourceDistance = 0, double cosineExponent = 0,
        double gaussianX = 0, double gaussianY = 0, double sourceX = 0, double sourceY = 0,
        double minHalfWidthX = 0, double minHalfWidthY = 0, NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : base(true, halfWidthX, halfWidthY, sourceDistance, cosineExponent, gaussianX, gaussianY, sourceX, sourceY, minHalfWidthX, minHalfWidthY, common, properties)
    {
    }
}
