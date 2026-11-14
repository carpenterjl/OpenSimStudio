using OpenSim.Core.Numerics;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential.Special;

/// <summary>
/// OpticStudio's ABCD surface (OpticStudio 2025 R2 help, "ABCD"): a "black box" whose eight
/// parameters Ax, Bx, Cx, Dx, Ay, By, Cy, Dy form two 2 × 2 matrices, and "the exit ray is
/// related to the incident ray by" [x′, ω′x] = [[Ax, Bx], [Cx, Dx]]·[x, ωx], "with a similar
/// expression for the y components". Plane shape.
/// <para>
/// The help does not say what ω is. This surface takes it as the slope ω = L/N, the variable
/// of the Paraxial surface's rule on the same help pages, so a matrix built from thin lenses and
/// gaps maps rays exactly as the Paraxial surfaces and gaps it stands for (the gate in
/// <c>IdealSurfaceTests</c>). The matrix acts on slopes, not reduced slopes: indices before and
/// after do not enter. The ray leaves from (x′, y′) on the vertex plane, which is where
/// <see cref="Intersect"/> puts it, and <see cref="TryRedirect"/> recovers x = (x′ − B·ω)/A to
/// form ω′ = C·x + D·ω; so A must not be zero (a matrix with A = 0 is the product of two with
/// A ≠ 0, which two ABCD surfaces can carry).
/// </para>
/// <para>
/// "Because there is no reliable way to compute the phase through an ABCD surface, any
/// calculation that requires OPD data ... will not be supported": the trace adds no path here
/// and <see cref="OpdLimitation"/> says so. The paraxial engine takes the y matrix
/// (<see cref="ParaxialSurface.SlopeMatrix"/>).
/// </para>
/// </summary>
public sealed class AbcdShape : SurfaceShape
{
    public AbcdShape(ParaxialMatrix x, ParaxialMatrix y)
    {
        X = Check(x, "x");
        Y = Check(y, "y");
    }

    private static ParaxialMatrix Check(ParaxialMatrix m, string axis)
    {
        if (!double.IsFinite(m.A) || !double.IsFinite(m.B) || !double.IsFinite(m.C) || !double.IsFinite(m.D))
            throw new ArgumentException($"ABCD surface: every entry of the {axis} matrix must be a number (it is {m}).");
        if (m.A == 0)
            throw new ArgumentException($"ABCD surface: A{axis} is zero, so the ray's height before the surface cannot be recovered from where it leaves; split the matrix into two ABCD surfaces whose A are not zero.");
        return m;
    }

    public override string TypeName => "ABCD";

    /// <summary>The x matrix on (x, ωx).</summary>
    public ParaxialMatrix X { get; }

    /// <summary>The y matrix on (y, ωy).</summary>
    public ParaxialMatrix Y { get; }

    public override double Sag(double x, double y, SequentialSurface surface) => 0;

    /// <summary>The ray crosses the vertex plane at (x, y) and leaves it at (x′, y′); the
    /// distance is to the crossing, so the path counts the incident segment only.</summary>
    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance)
    {
        double wx = direction.X / direction.Z, wy = direction.Y / direction.Z;
        point = new Vector3D(X.A * x + X.B * wx, Y.A * y + Y.B * wy, 0);
        distance = 0;
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    public override Vector3D Normal(Vector3D point, SequentialSurface surface) => Vector3D.UnitZ;

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { SlopeMatrix = Y };

    public override string? OpdLimitation(SequentialSurface surface) =>
        "OPD is not defined through an ABCD surface (OpticStudio: there is no reliable way to compute the phase through it).";

    public override bool TryRedirect(in SurfaceRedirectInput input, out Vector3D direction,
        out double extraOpticalPath, out RayStatus failure)
    {
        var d = input.Incoming;
        var p = input.Point;
        double wx = d.X / d.Z, wy = d.Y / d.Z;
        double x = (p.X - X.B * wx) / X.A, y = (p.Y - Y.B * wy) / Y.A;
        double sb = ShapeChecks.Travel(d), sa = input.Surface.IsMirror ? -sb : sb;
        var after = new Vector3D(X.C * x + X.D * wx, Y.C * y + Y.D * wy, 1) * sa;
        direction = after / after.Length;
        extraOpticalPath = 0;
        failure = double.IsFinite(direction.X) && double.IsFinite(direction.Y) ? RayStatus.Ok : RayStatus.MissedSurface;
        return true;
    }
}
