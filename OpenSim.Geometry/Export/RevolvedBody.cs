using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Export;

// OP-96: the format-neutral geometry the CAD writers read. Every optical body the export builds
// (a lens element, a mirror with its substrate, a bare mirror surface) is a surface of revolution
// about its own axis, so it is described once, by its meridian in the (r, z) half-plane of its own
// frame, and each writer turns that one description into its format's entities.

/// <summary>
/// The shape of a cap: a rotationally symmetric surface z(r) through a vertex on the axis, z
/// measured along the body's axis from the vertex. <see cref="Sag"/> and <see cref="Slope"/> are
/// exact (the optical surface itself, not an approximation of it); a writer that cannot carry the
/// shape exactly fits it to the export tolerance.
/// </summary>
public abstract class CapShape
{
    /// <summary>z(r), meters, for 0 ≤ r ≤ the cap's rim radius.</summary>
    public abstract double Sag(double r);

    /// <summary>dz/dr at r.</summary>
    public abstract double Slope(double r);

    /// <summary>A short name for messages ("plane", "sphere R = …", the surface type).</summary>
    public abstract string Describe();
}

/// <summary>A plane perpendicular to the axis through the vertex.</summary>
public sealed class PlaneCap : CapShape
{
    public static PlaneCap Instance { get; } = new();

    public override double Sag(double r) => 0;

    public override double Slope(double r) => 0;

    public override string Describe() => "plane";
}

/// <summary>
/// A sphere of signed radius <see cref="Radius"/> through the vertex: its centre lies on the axis
/// at z = <see cref="Radius"/> (positive toward +z, the sign rule of docs/Optics-Conventions.md §3).
/// z(r) = c·r²/(1 + √(1 − c²r²)) with c = 1/R, the rationalised root of the circle
/// r² + (z − R)² = R² (derived; the same form as the layout's ConicSag with k = 0), and
/// dz/dr = c·r/√(1 − c²r²) (its derivative).
/// </summary>
public sealed class SphereCap : CapShape
{
    public SphereCap(double radius)
    {
        if (!double.IsFinite(radius) || radius == 0)
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "A sphere cap needs a finite, non-zero radius (a plane is PlaneCap).");
        Radius = radius;
    }

    /// <summary>Signed radius, meters.</summary>
    public double Radius { get; }

    public override double Sag(double r)
    {
        double c = 1 / Radius;
        double arg = 1 - c * c * r * r;
        return arg < 0 ? double.NaN : c * r * r / (1 + Math.Sqrt(arg));
    }

    public override double Slope(double r)
    {
        double c = 1 / Radius;
        double arg = 1 - c * c * r * r;
        return arg <= 0 ? double.NaN : c * r / Math.Sqrt(arg);
    }

    public override string Describe() => FormattableString.Invariant($"sphere R = {Radius:g8} m");
}

/// <summary>
/// Any other rotationally symmetric sag (a conic that is not a sphere, an asphere): exact values
/// from the optical model, fitted by each writer that needs a spline (<see cref="BSplineFit"/>).
/// </summary>
public sealed class GeneralCap : CapShape
{
    private readonly Func<double, (double Z, double Slope)> _evaluate;

    /// <param name="name">The surface's description for messages.</param>
    /// <param name="evaluate">z(r) and dz/dr; NaN where undefined.</param>
    public GeneralCap(string name, Func<double, (double Z, double Slope)> evaluate)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
    }

    public string Name { get; }

    public override double Sag(double r) => _evaluate(r).Z;

    public override double Slope(double r) => _evaluate(r).Slope;

    public override string Describe() => Name;
}

/// <summary>One piece of a body's meridian, from <see cref="Start"/> to <see cref="End"/> in
/// (r, z) of the body's frame (meters, r ≥ 0).</summary>
public abstract record MeridianPiece
{
    public abstract (double R, double Z) Start { get; }
    public abstract (double R, double Z) End { get; }
}

/// <summary>
/// A cap: the surface <see cref="Shape"/> with its vertex at z = <see cref="VertexZ"/> on the axis,
/// from the axis to the rim radius <see cref="RimRadius"/> (or back from the rim to the axis when
/// <see cref="TowardAxis"/>).
/// </summary>
public sealed record CapPiece(CapShape Shape, double VertexZ, double RimRadius, bool TowardAxis) : MeridianPiece
{
    /// <summary>The rim's z: the vertex plus the sag at the rim radius.</summary>
    public double RimZ => VertexZ + Shape.Sag(RimRadius);

    public override (double R, double Z) Start => TowardAxis ? (RimRadius, RimZ) : (0, VertexZ);

    public override (double R, double Z) End => TowardAxis ? (0, VertexZ) : (RimRadius, RimZ);
}

/// <summary>
/// A straight piece: a plane annulus when z is constant, a cylinder when r is constant, a cone
/// otherwise.
/// </summary>
public sealed record LinePiece(double R0, double Z0, double R1, double Z1) : MeridianPiece
{
    public override (double R, double Z) Start => (R0, Z0);

    public override (double R, double Z) End => (R1, Z1);

    public bool IsRadial => Z0 == Z1;

    public bool IsAxial => R0 == R1;
}

/// <summary>What a body is, for names and layers.</summary>
public enum CadBodyKind
{
    Lens = 0,
    Mirror = 1,
}

/// <summary>
/// One body of revolution, in meters. <see cref="Placement"/> takes the body's frame (vertex of
/// the first cap at the origin, the axis along z) to the export's global frame. The pieces run
/// continuously, each starting where the last ended. A closed body (a solid) starts and ends on
/// the axis; an open one (a bare mirror surface, a sheet) starts on the axis and ends at a rim.
/// </summary>
public sealed record RevolvedBody(string Name, CadBodyKind Kind, RigidTransform Placement,
    IReadOnlyList<MeridianPiece> Pieces, bool IsClosed)
{
    /// <summary>Throws <see cref="ArgumentException"/> naming the body when the pieces do not
    /// join, a closed body does not start and end on the axis, or a radius is negative.</summary>
    public void Validate()
    {
        if (Pieces is null || Pieces.Count == 0) throw new ArgumentException($"{Name}: the body has no pieces.");
        for (int i = 0; i < Pieces.Count; i++)
        {
            var p = Pieces[i];
            if (p.Start.R < 0 || p.End.R < 0 || !double.IsFinite(p.Start.R) || !double.IsFinite(p.End.R)
                || !double.IsFinite(p.Start.Z) || !double.IsFinite(p.End.Z))
                throw new ArgumentException(FormattableString.Invariant(
                    $"{Name}: piece {i + 1} has a negative or undefined point ({p.Start.R:g6}, {p.Start.Z:g6}) to ({p.End.R:g6}, {p.End.Z:g6}) m."));
            if (p.Start == p.End)
                throw new ArgumentException($"{Name}: piece {i + 1} has zero length.");
            if (i > 0 && Pieces[i - 1].End != p.Start)
                throw new ArgumentException(FormattableString.Invariant(
                    $"{Name}: piece {i + 1} starts at ({p.Start.R:g9}, {p.Start.Z:g9}) m, not where piece {i} ends ({Pieces[i - 1].End.R:g9}, {Pieces[i - 1].End.Z:g9}) m."));
            if (i > 0 && i < Pieces.Count - 1 && p is CapPiece)
                throw new ArgumentException($"{Name}: a cap can only be the first or the last piece.");
        }
        if (Pieces[0].Start.R != 0)
            throw new ArgumentException($"{Name}: the meridian must start on the axis.");
        if (IsClosed && Pieces[^1].End.R != 0)
            throw new ArgumentException($"{Name}: a closed body's meridian must end on the axis.");
        if (!IsClosed && Pieces[^1].End.R == 0)
            throw new ArgumentException($"{Name}: an open body's meridian must end at a rim.");
    }

    /// <summary>The meridian points where pieces meet, first to last.</summary>
    public IReadOnlyList<(double R, double Z)> Joints()
    {
        var points = new List<(double, double)> { Pieces[0].Start };
        foreach (var p in Pieces) points.Add(p.End);
        return points;
    }

    /// <summary>The largest radius the body reaches.</summary>
    public double MaxRadius => Joints().Max(j => j.R);

    /// <summary>A point of the body's frame (r, z) at azimuth φ (radians from the frame's x) in
    /// the global frame.</summary>
    public Vector3D Global(double r, double z, double cosPhi, double sinPhi) =>
        Placement.Apply(new Vector3D(r * cosPhi, r * sinPhi, z));

    /// <summary>
    /// +1 when walking the meridian from first to last point and back along the axis encloses
    /// the region counterclockwise in the (r, z) plane (r to the right, z up), −1 when clockwise.
    /// For a closed body this fixes which side of each piece is outside (shoelace area, derived).
    /// </summary>
    public int MeridianSense()
    {
        var j = Joints();
        double twiceArea = 0;
        // The polygon of the joints; the caps' bulges do not change the sign for a body whose
        // faces do not cross (the writers refuse those), so the joints suffice.
        for (int i = 0; i < j.Count; i++)
        {
            var a = j[i];
            var b = j[(i + 1) % j.Count];
            twiceArea += a.R * b.Z - b.R * a.Z;
        }
        if (twiceArea == 0) throw new ArgumentException($"{Name}: the meridian encloses no area.");
        return twiceArea > 0 ? 1 : -1;
    }
}

/// <summary>A polyline in the global frame (a traced ray), meters.</summary>
public sealed record CadPolyline(string Name, IReadOnlyList<Vector3D> Points);

/// <summary>Everything one export writes: the bodies and the ray polylines, meters, global frame.</summary>
public sealed record CadScene(IReadOnlyList<RevolvedBody> Bodies, IReadOnlyList<CadPolyline> Rays);
