using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// LINE: C(t) = Origin + t·Direction. Direction keeps the VECTOR's magnitude — the STEP
/// parameterization is not arc length unless the exporter made it so, and ParameterOf
/// must agree with Point exactly.
/// </summary>
public sealed record StepLine(Vector3D Origin, Vector3D Direction) : StepCurve
{
    public override Vector3D Point(double t) => Origin + Direction * t;

    public override Vector3D Derivative(double t) => Direction;

    public override double ParameterOf(Vector3D p, double? hint = null) =>
        Vector3D.Dot(p - Origin, Direction) / Direction.LengthSquared;
}
