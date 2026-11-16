using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// A source object (plan item OP-77; docs/Optics-Conventions.md §72): an entry in the scene's
/// object list like any lens, placed by its reference object, position and tilts, and nested by
/// its "Inside Of" ("Any source may be placed inside of any object, or not in any object, but not
/// both (a source may not straddle an object boundary)"; help, "Non-sequential Sources").
/// <para>
/// <b>Rays never meet a source.</b> The help describes sources only as emitters and names no
/// interaction of a ray with a source object (help, "Non-sequential Sources", "Parameters Common
/// to All Source Objects"); here a source is a surface with no intersections, so the tracer
/// passes through it (a reading, Conventions §10 item 68). It keeps its object number, so
/// references, Inside Of and per-object energy keep their meaning.
/// </para>
/// <para>
/// <b>Sampling.</b> Each type turns <see cref="Dimensions"/> numbers in [0, 1) into a start point
/// and direction in its local frame by inverting its distributions, so Sobol points (or
/// pseudo-random ones) map one to one onto rays. The few steps with no inversion (an annular
/// region, the Gaussian far field of Source Ellipse) draw by rejection from a separate per-ray
/// stream, as OpticStudio says those parts do "not support Sobol sampling".
/// </para>
/// </summary>
public abstract class NscSource : NscGeometry
{
    private readonly IReadOnlyDictionary<int, string> _faces;

    protected NscSource(NscSourceCommon? common, NscSourceProperties? properties)
    {
        Common = common ?? new NscSourceCommon();
        Properties = properties ?? NscSourceProperties.Default;
        _faces = new Dictionary<int, string> { [0] = "Source" };
    }

    /// <summary>Parameters 1–5.</summary>
    public NscSourceCommon Common { get; }

    /// <summary>The Sources tab.</summary>
    public NscSourceProperties Properties { get; }

    public sealed override bool IsVolume => false;
    public sealed override IReadOnlyDictionary<int, string> Faces => _faces;

    /// <summary>Never: rays pass through sources (see the class summary).</summary>
    public sealed override void Intersect(Vector3D origin, Vector3D direction, double tMin, double tMax, List<NscLocalHit> hits) { }

    public sealed override bool IsInside(Vector3D point) => false;

    /// <summary>How many numbers in [0, 1) one ray takes by inversion.</summary>
    public abstract int Dimensions { get; }

    /// <summary>One ray in the local frame (meters; the direction need not be unit) from the
    /// numbers <paramref name="u"/> (length <see cref="Dimensions"/>) and, for rejection steps,
    /// the ray's own stream <paramref name="extra"/>.</summary>
    public abstract void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction);

    /// <summary>The distribution in words, for reports.</summary>
    public abstract string Distribution { get; }

    /// <summary>What the type did to its data (a spline's negative overshoot, ...).</summary>
    public virtual IReadOnlyList<string> Notes => Array.Empty<string>();

    /// <summary>The seed the type's own parameters fix (Source Ray's "Random Seed"); 0 for none.</summary>
    public virtual long OwnSeed => 0;

    // ------------------------------------------------------------------ shared sampling pieces

    /// <summary>Uniform in solid angle over the whole sphere: cos θ = 1 − 2a, φ = 2πb (derived:
    /// dΩ = d(cos θ)·dφ).</summary>
    public static Vector3D Isotropic(double a, double b)
    {
        double c = 1 - 2 * a, s = Math.Sqrt(Math.Max(0, 1 - c * c)), phi = 2 * Math.PI * b;
        return new Vector3D(s * Math.Cos(phi), s * Math.Sin(phi), c);
    }

    /// <summary>A direction with cos θ = <paramref name="cosTheta"/> about +z at azimuth 2πb.</summary>
    public static Vector3D FromPolar(double cosTheta, double b)
    {
        double s = Math.Sqrt(Math.Max(0, 1 - cosTheta * cosTheta)), phi = 2 * Math.PI * b;
        return new Vector3D(s * Math.Cos(phi), s * Math.Sin(phi), cosTheta);
    }

    /// <summary>Uniform over the ellipse of half widths a, b: r = √u, φ = 2πv (derived: the area
    /// element of the unit disk is r·dr·dφ, and the ellipse is its affine image).</summary>
    public static (double X, double Y) InEllipse(double halfX, double halfY, double u, double v)
    {
        double r = Math.Sqrt(u), phi = 2 * Math.PI * v;
        return (halfX * r * Math.Cos(phi), halfY * r * Math.Sin(phi));
    }

    /// <summary>Uniform over the rectangle |x| ≤ a, |y| ≤ b.</summary>
    public static (double X, double Y) InRectangle(double halfX, double halfY, double u, double v) =>
        (halfX * (2 * u - 1), halfY * (2 * v - 1));

    /// <summary>Checks a parameter: finite, and at least <paramref name="minimum"/>.</summary>
    protected static double Need(double value, string name, string type, double minimum = double.NegativeInfinity, double maximum = double.PositiveInfinity)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value,
                $"{type}: {name} must be finite" + (double.IsFinite(minimum) ? $" and at least {minimum}" : "") + (double.IsFinite(maximum) ? $" and at most {maximum}" : "") + $"; it is {value}.");
        return value;
    }
}
