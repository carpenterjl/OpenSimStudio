using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>CIRCLE: C(t) = O + r·(cos t·X + sin t·Y), t ∈ [0, 2π).</summary>
public sealed record StepCircle(Axis2Placement3D Frame, double Radius) : StepCurve
{
    public override double? Period => 2 * Math.PI;

    public override Vector3D Point(double t) =>
        Frame.Origin + Frame.XAxis * (Radius * Math.Cos(t)) + Frame.YAxis * (Radius * Math.Sin(t));

    public override Vector3D Derivative(double t) =>
        Frame.XAxis * (-Radius * Math.Sin(t)) + Frame.YAxis * (Radius * Math.Cos(t));

    public override double ParameterOf(Vector3D p, double? hint = null)
    {
        var (x, y, _) = Frame.Local(p);
        double t = Math.Atan2(y, x);
        return t < 0 ? t + 2 * Math.PI : t;
    }
}
