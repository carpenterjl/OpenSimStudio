using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// SURFACE_OF_LINEAR_EXTRUSION: S(u,v) = C(u) + v·d, with C the swept profile curve and
/// d the (magnitude-carrying) extrusion vector. Inversion alternates u = curve parameter
/// of the point pulled back by v·d, and v = axial component of the residual — the pair
/// decouples after a few sweeps for any profile transverse to d, and a failure to settle
/// is a loud error rather than a wrong UV.
/// </summary>
public sealed record StepLinearExtrusionSurface(int Id, StepCurve Profile, Vector3D Direction) : StepSurface
{
    public override Vector3D Point(double u, double v) => Profile.Point(u) + Direction * v;

    public override Vector3D PartialU(double u, double v) => Profile.Derivative(u);

    public override Vector3D PartialV(double u, double v) => Direction;

    public override double? PeriodU => Profile.Period;

    public override (double U, double V) InvertRaw(Vector3D p)
    {
        double v = 0;
        double u = Profile.ParameterOf(p);
        for (int it = 0; it < 16; it++)
        {
            v = Vector3D.Dot(p - Profile.Point(u), Direction) / Direction.LengthSquared;
            double uNext = Profile.ParameterOf(p - Direction * v);
            bool settled = Math.Abs(uNext - u) < 1e-12 * (1 + Math.Abs(u));
            u = uNext;
            if (settled) return (u, v);
        }
        // One more residual check before declaring failure — the alternation may have
        // converged in position without the parameter delta test firing (periodic wrap).
        if ((Point(u, v) - p).Length < 1e-9 * (1 + p.Length)) return (u, v);
        throw new StepGeometryException(
            $"#{Id}: linear-extrusion surface inversion did not converge (profile nearly " +
            "parallel to the extrusion direction?)");
    }
}
