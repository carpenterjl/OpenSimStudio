using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>PLANE: S(u,v) = O + u·X + v·Y; normal = Z.</summary>
public sealed record StepPlane(Axis2Placement3D Frame) : StepSurface
{
    public override Vector3D Point(double u, double v) =>
        Frame.Origin + Frame.XAxis * u + Frame.YAxis * v;

    public override Vector3D PartialU(double u, double v) => Frame.XAxis;

    public override Vector3D PartialV(double u, double v) => Frame.YAxis;

    public override (double U, double V) InvertRaw(Vector3D p)
    {
        var (x, y, _) = Frame.Local(p);
        return (x, y);
    }
}
