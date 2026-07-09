using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// A resolved parametric surface in meters. The tessellator consumes only this contract:
/// evaluation, first partials (whose cross product orients triangles), periodicity, and
/// raw UV inversion of a point known to lie on the surface. <see cref="InvertRaw"/>
/// returns the canonical branch (u ∈ [0, 2π) on periodic surfaces) and signals an
/// undefined u at parameterization poles (sphere pole, cone apex) with NaN — the UV
/// mapper resolves those by loop continuity, which is the only correct source of truth
/// there.
/// </summary>
public abstract record StepSurface
{
    public abstract Vector3D Point(double u, double v);

    public abstract Vector3D PartialU(double u, double v);

    public abstract Vector3D PartialV(double u, double v);

    /// <summary>Surface normal ∂S/∂u × ∂S/∂v, normalized. Undefined at poles.</summary>
    public Vector3D Normal(double u, double v) =>
        Vector3D.Cross(PartialU(u, v), PartialV(u, v)).Normalized();

    public virtual double? PeriodU => null;

    public virtual double? PeriodV => null;

    /// <summary>
    /// UV of a point on the surface, canonical branch; U = NaN at a parameterization pole.
    /// B-spline surfaces cannot invert without a seed and override the Newton entry point
    /// instead — calling this on one is a programming error and throws.
    /// </summary>
    public abstract (double U, double V) InvertRaw(Vector3D p);
}
