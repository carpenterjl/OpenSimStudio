using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// CONICAL_SURFACE per ISO 10303-42: S(u,v) = O + (R + v·tan α)(cos u·X + sin u·Y) + v·Z,
/// with R the radius at v = 0 and α the semi-angle. The apex (radius → 0) is a
/// parameterization pole: InvertRaw reports u = NaN there.
/// </summary>
public sealed record StepCone(Axis2Placement3D Frame, double Radius, double SemiAngle) : StepSurface
{
    private double TanAlpha => Math.Tan(SemiAngle);

    public override double? PeriodU => 2 * Math.PI;

    public override Vector3D Point(double u, double v)
    {
        double r = Radius + v * TanAlpha;
        return Frame.Origin + Frame.XAxis * (r * Math.Cos(u)) + Frame.YAxis * (r * Math.Sin(u))
               + Frame.ZAxis * v;
    }

    public override Vector3D PartialU(double u, double v)
    {
        double r = Radius + v * TanAlpha;
        return Frame.XAxis * (-r * Math.Sin(u)) + Frame.YAxis * (r * Math.Cos(u));
    }

    public override Vector3D PartialV(double u, double v) =>
        Frame.XAxis * (TanAlpha * Math.Cos(u)) + Frame.YAxis * (TanAlpha * Math.Sin(u)) + Frame.ZAxis;

    public override (double U, double V) InvertRaw(Vector3D p)
    {
        var (x, y, z) = Frame.Local(p);
        double rho = Math.Sqrt(x * x + y * y);
        // The apex has every u at once; continuity along the loop must decide.
        if (rho < 1e-12 * (Math.Abs(Radius) + Math.Abs(z) + 1e-300)) return (double.NaN, z);
        double u = Math.Atan2(y, x);
        return (u < 0 ? u + 2 * Math.PI : u, z);
    }
}
