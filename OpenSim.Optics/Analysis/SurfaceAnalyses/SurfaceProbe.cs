using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Analysis;

/// <summary>The slope a surface slope analysis shows (OpticStudio's Surface Slope "Data":
/// "tangential, sagittal, x, or y slope, or Modulus").</summary>
public enum SurfaceSlopeData
{
    /// <summary>dz along the radial direction from the vertex (x/r, y/r).</summary>
    Tangential,
    /// <summary>dz along the azimuthal direction (−y/r, x/r).</summary>
    Sagittal,
    /// <summary>∂z/∂x.</summary>
    X,
    /// <summary>∂z/∂y.</summary>
    Y,
    /// <summary>"The maximum length of the slope vector": |∇z|.</summary>
    Modulus,
}

/// <summary>The curvature a surface curvature analysis shows (OpticStudio's Surface Curvature
/// "Data": "tangential. sagittal, x, or y curvature or Modulus").</summary>
public enum SurfaceCurvatureData
{
    /// <summary>The normal curvature in the radial direction.</summary>
    Tangential,
    /// <summary>The normal curvature in the azimuthal direction.</summary>
    Sagittal,
    /// <summary>The normal curvature in the x direction.</summary>
    X,
    /// <summary>The normal curvature in the y direction.</summary>
    Y,
    /// <summary>The larger magnitude of the two principal curvatures (this program's reading of
    /// "the maximum length of the curvature vector", docs/Optics-Conventions.md §28).</summary>
    Modulus,
}

/// <summary>The sag of a surface and its first and second derivatives at one point, local
/// frame, meters (the second derivatives in 1/m).</summary>
public readonly record struct SurfaceDerivatives(double Z, double Zx, double Zy, double Zxx, double Zxy, double Zyy);

/// <summary>
/// Reads the shape of one sequential surface for the surface analyses (OP-43,
/// docs/Optics-Conventions.md §28): the sag, its slopes and its second derivatives at any
/// (x, y) of the surface's local frame, through <see cref="SurfaceShape.Sag"/> and
/// <see cref="SurfaceShape.Normal"/>, or the conic for the Standard surface.
/// <para>
/// <b>Slopes.</b> The Standard conic's are analytic (<see cref="ConicSag"/>); a sag-type shape
/// (<see cref="SagSurfaceShape"/>, the aspheres) gives its own analytic slopes; any other shape
/// gives them through its normal n as −n<sub>x</sub>/n<sub>z</sub>, −n<sub>y</sub>/n<sub>z</sub>,
/// the slope the trace refracts with.
/// </para>
/// <para>
/// <b>Second derivatives.</b> The Standard conic's are analytic: with φ = √(1 − (1 + k)c²r²),
/// d²z/dr² = c/φ³ and (dz/dr)/r = c/φ (derived by differentiating dz/dr = c·r/φ), and the
/// Cartesian ones follow from the chain rule for z(r). Every other shape's are central
/// differences of its slopes with step h, Richardson-extrapolated from h and h/2:
/// (4·D(h/2) − D(h))/3, whose truncation error is −h⁴/480 times the sixth derivative of the
/// sag (derived from the Taylor series of the central difference, D(h) = z″ + h²z⁗/6 +
/// h⁴z⁽⁶⁾/120 + …), and whose rounding error is about ε·|slope|/h. The analyses take
/// h = 10⁻³ of the clear semi-diameter. Where a stencil point falls outside the sag's domain
/// the derivatives are not defined and the point is reported, never filled in.
/// </para>
/// Immutable and safe to share between threads.
/// </summary>
public sealed class SurfaceProbe
{
    /// <param name="surface">The surface; not a coordinate break.</param>
    /// <param name="differenceStep">h for the differenced second derivatives, meters (unused
    /// for the Standard surface; must be positive otherwise).</param>
    public SurfaceProbe(SequentialSurface surface, double differenceStep)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.IsCoordinateBreak)
            throw new ArgumentException("A coordinate break has no shape to analyse.", nameof(surface));
        if (surface.Shape is not null && !(differenceStep > 0 && double.IsFinite(differenceStep)))
            throw new ArgumentOutOfRangeException(nameof(differenceStep), differenceStep,
                "The difference step for the second derivatives must be positive and finite.");
        Surface = OpenSim.Optics.Sequential.Composite.CompositeSurfaces.WithEffectiveShape(surface);   // OP-32: the composite sum, as OpticStudio's sag analyses show by default
        if (Surface.Shape is not null && !(differenceStep > 0 && double.IsFinite(differenceStep)))
            throw new ArgumentOutOfRangeException(nameof(differenceStep), differenceStep,
                "The difference step for the second derivatives must be positive and finite.");
        DifferenceStep = differenceStep;
    }

    /// <summary>A sphere (conic 0) of curvature <paramref name="curvature"/>, 1/m.</summary>
    public static SurfaceProbe Sphere(double curvature) => new(new SequentialSurface { Curvature = curvature }, 0);

    public SequentialSurface Surface { get; }

    /// <summary>h of the differenced second derivatives, meters.</summary>
    public double DifferenceStep { get; }

    /// <summary>True when the second derivatives are analytic (the Standard conic).</summary>
    public bool AnalyticSecondDerivatives => Surface.Shape is null;

    /// <summary>True for a shape known to be rotationally symmetric (the Standard conic and the
    /// radial sags); false when it may not be.</summary>
    public bool IsRotationallySymmetric => Surface.Shape is null or RadialSagShape;

    /// <summary>The surface type's name.</summary>
    public string TypeName => Surface.Shape?.TypeName ?? "Standard";

    /// <summary>The sag at (x, y), meters, or NaN where it is not defined.</summary>
    public double Sag(double x, double y)
    {
        if (Surface.Shape is { } shape) return shape.Sag(x, y, Surface);
        double r = Math.Sqrt(x * x + y * y);
        return ConicSag.TryEvaluate(r, Surface.Curvature, Surface.Conic, out double z, out _) ? z : double.NaN;
    }

    /// <summary>The sag and its slopes at (x, y); false where any is not defined or finite.</summary>
    public bool TrySlope(double x, double y, out double z, out double zx, out double zy)
    {
        zx = zy = double.NaN;
        switch (Surface.Shape)
        {
            case null:
            {
                double r = Math.Sqrt(x * x + y * y);
                if (!ConicSag.TryEvaluate(r, Surface.Curvature, Surface.Conic, out z, out double dzdr)) return Fail(out z);
                if (r > 0) { zx = dzdr * (x / r); zy = dzdr * (y / r); }
                else zx = zy = 0;
                break;
            }
            case SagSurfaceShape sag:
                if (!sag.TrySag(x, y, Surface, out z, out zx, out zy)) return Fail(out z);
                break;
            case { } shape:
            {
                z = shape.Sag(x, y, Surface);
                if (!double.IsFinite(z)) return Fail(out z);
                var n = shape.Normal(new Vector3D(x, y, z), Surface);
                if (!(n.Z > 0)) return Fail(out z);
                zx = -n.X / n.Z;
                zy = -n.Y / n.Z;
                break;
            }
        }
        return double.IsFinite(z) && double.IsFinite(zx) && double.IsFinite(zy) || Fail(out z);
    }

    /// <summary>The sag, slopes and second derivatives at (x, y); false where any is not
    /// defined (for a differenced shape, also where a stencil point is not).</summary>
    public bool TryDerivatives(double x, double y, out SurfaceDerivatives d)
    {
        d = default;
        if (!TrySlope(x, y, out double z, out double zx, out double zy)) return false;
        double zxx, zxy, zyy;
        if (Surface.Shape is null)
        {
            double c = Surface.Curvature, k = Surface.Conic;
            double r2 = x * x + y * y, r = Math.Sqrt(r2);
            double phi = Math.Sqrt(1 - (1 + k) * c * c * r2);
            double second = c / (phi * phi * phi), firstOverR = c / phi;
            if (r > 0)
            {
                double cs = x / r, sn = y / r;
                zxx = second * cs * cs + firstOverR * sn * sn;
                zyy = second * sn * sn + firstOverR * cs * cs;
                zxy = (second - firstOverR) * cs * sn;
            }
            else
            {
                zxx = zyy = c;
                zxy = 0;
            }
        }
        else
        {
            double h = DifferenceStep;
            if (!TryCentral(x, y, h, out double axx, out double axy, out double ayy)
                || !TryCentral(x, y, 0.5 * h, out double bxx, out double bxy, out double byy))
                return false;
            zxx = (4 * bxx - axx) / 3;
            zxy = (4 * bxy - axy) / 3;
            zyy = (4 * byy - ayy) / 3;
        }
        if (!double.IsFinite(zxx) || !double.IsFinite(zxy) || !double.IsFinite(zyy)) return false;
        d = new SurfaceDerivatives(z, zx, zy, zxx, zxy, zyy);
        return true;
    }

    private bool TryCentral(double x, double y, double h, out double zxx, out double zxy, out double zyy)
    {
        zxx = zxy = zyy = double.NaN;
        if (!TrySlope(x + h, y, out _, out double xpx, out double xpy)
            || !TrySlope(x - h, y, out _, out double xmx, out double xmy)
            || !TrySlope(x, y + h, out _, out double ypx, out double ypy)
            || !TrySlope(x, y - h, out _, out double ymx, out double ymy))
            return false;
        double twoH = 2 * h;
        zxx = (xpx - xmx) / twoH;
        zyy = (ypy - ymy) / twoH;
        zxy = 0.5 * ((xpy - xmy) / twoH + (ypx - ymx) / twoH);
        return true;
    }

    private static bool Fail(out double z)
    {
        z = double.NaN;
        return false;
    }

    /// <summary>The unit direction in the vertex plane of the tangential (radial) direction at
    /// (x, y); +x on the axis, where it is not defined.</summary>
    public static (double U, double V) Radial(double x, double y)
    {
        double r = Math.Sqrt(x * x + y * y);
        return r > 0 ? (x / r, y / r) : (1, 0);
    }

    /// <summary>The slope <paramref name="data"/> at (x, y) from the derivatives there
    /// (dimensionless dz along a direction of the vertex plane).</summary>
    public static double Slope(in SurfaceDerivatives d, SurfaceSlopeData data, double x, double y)
    {
        var (u, v) = Radial(x, y);
        return data switch
        {
            SurfaceSlopeData.Tangential => d.Zx * u + d.Zy * v,
            SurfaceSlopeData.Sagittal => -d.Zx * v + d.Zy * u,
            SurfaceSlopeData.X => d.Zx,
            SurfaceSlopeData.Y => d.Zy,
            SurfaceSlopeData.Modulus => Math.Sqrt(d.Zx * d.Zx + d.Zy * d.Zy),
            _ => throw new ArgumentOutOfRangeException(nameof(data), data, "Unknown slope data."),
        };
    }

    /// <summary>
    /// The curvature <paramref name="data"/> at (x, y), 1/m, positive where the surface bends
    /// toward +z (the sign of <see cref="SequentialSurface.Curvature"/>). A directional
    /// curvature is the normal curvature of the surface in the direction whose projection on
    /// the vertex plane is (u, v): with the first fundamental form E = 1 + z<sub>x</sub>²,
    /// F = z<sub>x</sub>z<sub>y</sub>, G = 1 + z<sub>y</sub>², the second L = z<sub>xx</sub>/W,
    /// M = z<sub>xy</sub>/W, N = z<sub>yy</sub>/W, W = √(1 + z<sub>x</sub>² + z<sub>y</sub>²),
    /// κ = (Lu² + 2Muv + Nv²)/(Eu² + 2Fuv + Gv²) (the Monge-patch forms of differential
    /// geometry; for z(r) this gives z″/(1 + z′²)^{3/2} tangentially and z′/(r√(1 + z′²))
    /// sagittally, derived). The modulus is the larger magnitude of the principal curvatures,
    /// the eigenvalues of the Weingarten matrix I⁻¹·II (I = [E F; F G], II = [L M; M N]).
    /// </summary>
    public static double Curvature(in SurfaceDerivatives d, SurfaceCurvatureData data, double x, double y)
    {
        double w = Math.Sqrt(1 + d.Zx * d.Zx + d.Zy * d.Zy);
        double e = 1 + d.Zx * d.Zx, f = d.Zx * d.Zy, g = 1 + d.Zy * d.Zy;
        double l = d.Zxx / w, m = d.Zxy / w, n = d.Zyy / w;
        double Normal(double u, double v) => (l * u * u + 2 * m * u * v + n * v * v) / (e * u * u + 2 * f * u * v + g * v * v);
        var (ru, rv) = Radial(x, y);
        switch (data)
        {
            case SurfaceCurvatureData.Tangential: return Normal(ru, rv);
            case SurfaceCurvatureData.Sagittal: return Normal(-rv, ru);
            case SurfaceCurvatureData.X: return Normal(1, 0);
            case SurfaceCurvatureData.Y: return Normal(0, 1);
            case SurfaceCurvatureData.Modulus:
            {
                // The Weingarten matrix S = I⁻¹·II; its eigenvalues are the principal
                // curvatures, (S11 + S22)/2 ± √(((S11 − S22)/2)² + S12·S21). Written so, the
                // root does not lose half the digits at an umbilic point (a sphere), as
                // √(H² − K) would.
                double det = e * g - f * f;
                double s11 = (g * l - f * m) / det, s12 = (g * m - f * n) / det;
                double s21 = (e * m - f * l) / det, s22 = (e * n - f * m) / det;
                double half = 0.5 * (s11 - s22);
                double root = Math.Sqrt(Math.Max(half * half + s12 * s21, 0));
                return Math.Abs(0.5 * (s11 + s22)) + root;
            }
            default: throw new ArgumentOutOfRangeException(nameof(data), data, "Unknown curvature data.");
        }
    }
}
