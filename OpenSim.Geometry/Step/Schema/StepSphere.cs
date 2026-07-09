using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// SPHERICAL_SURFACE: S(u,v) = O + r·(cos v·cos u·X + cos v·sin u·Y + sin v·Z),
/// u ∈ [0, 2π), v ∈ [−π/2, π/2]. The poles v = ±π/2 are parameterization poles
/// (u undefined → NaN from InvertRaw).
/// </summary>
public sealed record StepSphere(Axis2Placement3D Frame, double Radius) : StepSurface
{
    public override double? PeriodU => 2 * Math.PI;

    public override Vector3D Point(double u, double v)
    {
        double cv = Math.Cos(v);
        return Frame.Origin
               + Frame.XAxis * (Radius * cv * Math.Cos(u))
               + Frame.YAxis * (Radius * cv * Math.Sin(u))
               + Frame.ZAxis * (Radius * Math.Sin(v));
    }

    public override Vector3D PartialU(double u, double v)
    {
        double cv = Math.Cos(v);
        return Frame.XAxis * (-Radius * cv * Math.Sin(u)) + Frame.YAxis * (Radius * cv * Math.Cos(u));
    }

    public override Vector3D PartialV(double u, double v)
    {
        double sv = Math.Sin(v);
        return Frame.XAxis * (-Radius * sv * Math.Cos(u))
               + Frame.YAxis * (-Radius * sv * Math.Sin(u))
               + Frame.ZAxis * (Radius * Math.Cos(v));
    }

    public override (double U, double V) InvertRaw(Vector3D p)
    {
        var (x, y, z) = Frame.Local(p);
        double v = Math.Asin(Math.Clamp(z / Radius, -1, 1));
        double rho = Math.Sqrt(x * x + y * y);
        if (rho < 1e-12 * Radius) return (double.NaN, v); // pole
        double u = Math.Atan2(y, x);
        return (u < 0 ? u + 2 * Math.PI : u, v);
    }
}
