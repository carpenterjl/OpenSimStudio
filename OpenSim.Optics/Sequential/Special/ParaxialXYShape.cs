using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Special;

/// <summary>
/// OpticStudio's Paraxial XY surface (OpticStudio 2025 R2 help, "Paraxial XY"): "similar to the
/// paraxial surface except the optical power can be specified in the X and Y directions
/// separately", so a paraxial cylindrical or toroidal lens; powers in inverse lens units; plane
/// shape. Rays turn by the Paraxial surface's rule with φx in x and φy in y:
/// u′x = (n_s·ux − x·φx)/n′_s, u′y = (n_s·uy − y·φy)/n′_s (see <see cref="ParaxialShape"/>).
/// <para>
/// The help says its OPD "is computed using mode = 1" of the Paraxial surface, which it does not
/// define. An anamorphic ideal lens has line foci, not a point image, so no exact path rule like
/// the Paraxial surface's exists. This surface adds the thin-lens path −(φx·x² + φy·y²)/2,
/// which is the paraxial limit of the Paraxial surface's exact term (derived in the remarks of
/// docs/Optics-Conventions.md §18), and reports the OPD as approximate through
/// <see cref="OpdLimitation"/>. The paraxial engine sees the y power.
/// </para>
/// </summary>
public sealed class ParaxialXYShape : SurfaceShape
{
    /// <param name="powerX">Power in x, 1/m.</param>
    /// <param name="powerY">Power in y, 1/m.</param>
    public ParaxialXYShape(double powerX, double powerY)
    {
        PowerX = ShapeChecks.Finite(powerX, "Paraxial XY surface: the x power");
        PowerY = ShapeChecks.Finite(powerY, "Paraxial XY surface: the y power");
    }

    public override string TypeName => "Paraxial XY";

    public double PowerX { get; }
    public double PowerY { get; }

    public override double Sag(double x, double y, SequentialSurface surface) => 0;

    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance)
    {
        point = new Vector3D(x, y, 0);
        distance = 0;
        return true;
    }

    public override Vector3D Normal(Vector3D point, SequentialSurface surface) => Vector3D.UnitZ;

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { ThinLensPower = PowerY };

    public override string? OpdLimitation(SequentialSurface surface) =>
        PowerX == PowerY && PowerX == 0 ? null
            : "A Paraxial XY surface adds only the paraxial thin-lens path −(φx·x² + φy·y²)/2, so OPD through it is approximate.";

    public override bool TryRedirect(in SurfaceRedirectInput input, out Vector3D direction,
        out double extraOpticalPath, out RayStatus failure)
    {
        var d = input.Incoming;
        var p = input.Point;
        double sb = ShapeChecks.Travel(d), sa = input.Surface.IsMirror ? -sb : sb;
        double nb = sb * input.IndexBefore, na = sa * input.IndexAfter;
        double vx = (nb * d.X / d.Z - p.X * PowerX) / na, vy = (nb * d.Y / d.Z - p.Y * PowerY) / na;
        var after = new Vector3D(vx, vy, 1) * sa;
        direction = after / after.Length;
        extraOpticalPath = -(PowerX * p.X * p.X + PowerY * p.Y * p.Y) / 2;
        failure = double.IsFinite(direction.X) && double.IsFinite(direction.Y) ? RayStatus.Ok : RayStatus.MissedSurface;
        return true;
    }
}
