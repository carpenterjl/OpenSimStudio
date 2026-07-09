using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// TOROIDAL_SURFACE: S(u,v) = O + (R + r·cos v)(cos u·X + sin u·Y) + r·sin v·Z, with
/// R the major and r the minor radius; both directions periodic. Degenerate tori
/// (r ≥ R, self-intersecting) are rejected at schema resolution, so InvertRaw has no
/// poles here.
/// </summary>
public sealed record StepTorus(Axis2Placement3D Frame, double MajorRadius, double MinorRadius) : StepSurface
{
    public override double? PeriodU => 2 * Math.PI;

    public override double? PeriodV => 2 * Math.PI;

    public override Vector3D Point(double u, double v)
    {
        double ring = MajorRadius + MinorRadius * Math.Cos(v);
        return Frame.Origin
               + Frame.XAxis * (ring * Math.Cos(u))
               + Frame.YAxis * (ring * Math.Sin(u))
               + Frame.ZAxis * (MinorRadius * Math.Sin(v));
    }

    public override Vector3D PartialU(double u, double v)
    {
        double ring = MajorRadius + MinorRadius * Math.Cos(v);
        return Frame.XAxis * (-ring * Math.Sin(u)) + Frame.YAxis * (ring * Math.Cos(u));
    }

    public override Vector3D PartialV(double u, double v)
    {
        double sv = Math.Sin(v);
        return Frame.XAxis * (-MinorRadius * sv * Math.Cos(u))
               + Frame.YAxis * (-MinorRadius * sv * Math.Sin(u))
               + Frame.ZAxis * (MinorRadius * Math.Cos(v));
    }

    public override (double U, double V) InvertRaw(Vector3D p)
    {
        var (x, y, z) = Frame.Local(p);
        double u = Math.Atan2(y, x);
        double rho = Math.Sqrt(x * x + y * y);
        double v = Math.Atan2(z, rho - MajorRadius);
        return (u < 0 ? u + 2 * Math.PI : u, v < 0 ? v + 2 * Math.PI : v);
    }
}
