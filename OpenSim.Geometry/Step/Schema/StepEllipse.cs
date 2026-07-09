using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>ELLIPSE: C(t) = O + a·cos t·X + b·sin t·Y, t ∈ [0, 2π).</summary>
public sealed record StepEllipse(Axis2Placement3D Frame, double SemiAxis1, double SemiAxis2) : StepCurve
{
    public override double? Period => 2 * Math.PI;

    public override Vector3D Point(double t) =>
        Frame.Origin + Frame.XAxis * (SemiAxis1 * Math.Cos(t)) + Frame.YAxis * (SemiAxis2 * Math.Sin(t));

    public override Vector3D Derivative(double t) =>
        Frame.XAxis * (-SemiAxis1 * Math.Sin(t)) + Frame.YAxis * (SemiAxis2 * Math.Cos(t));

    public override double ParameterOf(Vector3D p, double? hint = null)
    {
        var (x, y, _) = Frame.Local(p);
        double t = Math.Atan2(y / SemiAxis2, x / SemiAxis1);
        return t < 0 ? t + 2 * Math.PI : t;
    }
}
