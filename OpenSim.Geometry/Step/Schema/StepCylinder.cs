using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// CYLINDRICAL_SURFACE: S(u,v) = O + r·(cos u·X + sin u·Y) + v·Z. The natural normal
/// ∂u×∂v points radially outward.
/// </summary>
public sealed record StepCylinder(Axis2Placement3D Frame, double Radius) : StepSurface
{
    public override double? PeriodU => 2 * Math.PI;

    public override Vector3D Point(double u, double v) =>
        Frame.Origin + Frame.XAxis * (Radius * Math.Cos(u)) + Frame.YAxis * (Radius * Math.Sin(u))
        + Frame.ZAxis * v;

    public override Vector3D PartialU(double u, double v) =>
        Frame.XAxis * (-Radius * Math.Sin(u)) + Frame.YAxis * (Radius * Math.Cos(u));

    public override Vector3D PartialV(double u, double v) => Frame.ZAxis;

    public override (double U, double V) InvertRaw(Vector3D p)
    {
        var (x, y, z) = Frame.Local(p);
        double u = Math.Atan2(y, x);
        return (u < 0 ? u + 2 * Math.PI : u, z);
    }
}
