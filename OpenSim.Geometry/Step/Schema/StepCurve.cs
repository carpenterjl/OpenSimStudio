using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// A resolved 3D curve in its STEP parameterization (already scaled to meters). Edge
/// trimming works from vertex GEOMETRY, not file parameters: <see cref="ParameterOf"/>
/// recovers the parameter of an endpoint that is known to lie on the curve, so the
/// importer never trusts exporter-specific parameter conventions.
/// </summary>
public abstract record StepCurve
{
    public abstract Vector3D Point(double t);

    /// <summary>First derivative dC/dt.</summary>
    public abstract Vector3D Derivative(double t);

    /// <summary>Parameter period for closed periodic curves (2π for circle/ellipse), else null.</summary>
    public virtual double? Period => null;

    /// <summary>
    /// Parameter of a point lying on the curve. For periodic curves the result is on the
    /// canonical branch [0, period); callers unwrap by continuity. <paramref name="hint"/>
    /// disambiguates only where the geometry alone cannot (B-spline multi-candidate).
    /// </summary>
    public abstract double ParameterOf(Vector3D p, double? hint = null);
}
