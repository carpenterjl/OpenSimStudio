using OpenSim.Core.Numerics;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// SURFACE_OF_REVOLUTION: the profile curve C(v) revolved about an axis; u is the
/// rotation angle (period 2π), v the profile parameter. Inversion splits into the
/// meridian half-plane problem — find v matching the point's (axial, radial) pair via
/// seeded 1D Newton on the squared meridian distance — then u is the signed angle
/// between the profile's and the point's radial directions. A non-converging profile
/// solve is a loud failure, never a wrong UV.
/// </summary>
public sealed record StepRevolutionSurface(int Id, StepCurve Profile, Vector3D AxisPoint, Vector3D AxisDirection)
    : StepSurface
{
    public override double? PeriodU => 2 * Math.PI;

    /// <summary>A periodic profile (circular arc crossing its branch cut) makes v periodic
    /// too — the UV mapper must unwrap v by loop continuity like any other period.</summary>
    public override double? PeriodV => Profile.Period;

    public override Vector3D Point(double u, double v) => Rotate(Profile.Point(v), u);

    public override Vector3D PartialU(double u, double v)
    {
        var s = Point(u, v);
        return Vector3D.Cross(AxisDirection, s - AxisPoint);
    }

    public override Vector3D PartialV(double u, double v) => RotateVector(Profile.Derivative(v), u);

    public override (double U, double V) InvertRaw(Vector3D p)
    {
        var d = p - AxisPoint;
        double axial = Vector3D.Dot(d, AxisDirection);
        var radial = d - AxisDirection * axial;
        double rho = radial.Length;

        double v = SolveProfileParameter(axial, rho, p);
        var c = Profile.Point(v) - AxisPoint;
        var cRadial = c - AxisDirection * Vector3D.Dot(c, AxisDirection);
        if (rho < 1e-12 * (1 + Math.Abs(axial)) || cRadial.Length < 1e-12 * (1 + Math.Abs(axial)))
            return (double.NaN, v); // on the axis: a parameterization pole

        double cos = Math.Clamp(Vector3D.Dot(cRadial, radial) / (cRadial.Length * rho), -1, 1);
        double sin = Vector3D.Dot(Vector3D.Cross(cRadial, radial), AxisDirection) / (cRadial.Length * rho);
        double u = Math.Atan2(sin, cos);
        return (u < 0 ? u + 2 * Math.PI : u, v);
    }

    /// <summary>Profile parameter whose meridian coordinates (axial, ρ) match the point's.</summary>
    private double SolveProfileParameter(double axial, double rho, Vector3D p)
    {
        (double Z, double Rho) Meridian(double v)
        {
            var c = Profile.Point(v) - AxisPoint;
            double z = Vector3D.Dot(c, AxisDirection);
            return (z, (c - AxisDirection * z).Length);
        }
        double F(double v)
        {
            var (z, r) = Meridian(v);
            return (z - axial) * (z - axial) + (r - rho) * (r - rho);
        }

        // Deterministic candidate scan over the profile's natural domain, then damped
        // Newton (numeric derivative of the analytic meridian curve).
        var (t0, t1) = ProfileDomain(p);
        int samples = 64;
        double best = t0, bestF = double.MaxValue;
        for (int i = 0; i <= samples; i++)
        {
            double t = t0 + (t1 - t0) * i / samples;
            double f = F(t);
            if (f < bestF)
            {
                bestF = f;
                best = t;
            }
        }
        double v = best, h = (t1 - t0) * 1e-6;
        for (int it = 0; it < 32 && h > 0; it++)
        {
            double f1 = (F(v + h) - F(v - h)) / (2 * h);
            double f2 = (F(v + h) - 2 * F(v) + F(v - h)) / (h * h);
            if (f2 <= 0) break;
            double step = Math.Clamp(f1 / f2, -(t1 - t0) / 8, (t1 - t0) / 8);
            v -= step;
            if (Math.Abs(step) < 1e-14 * (t1 - t0)) break;
        }

        double scale = Math.Abs(axial) + rho + 1e-300;
        if (Math.Sqrt(F(v)) > 1e-6 * scale)
            throw new StepGeometryException(FormattableString.Invariant(
                $"surface #{Id}: revolution profile solve did not converge for point ({p.X}, {p.Y}, {p.Z})"));
        return v;
    }

    /// <summary>The profile's natural parameter range for candidate scanning.</summary>
    private (double Start, double End) ProfileDomain(Vector3D p) => Profile switch
    {
        StepCircle or StepEllipse => (0, 2 * Math.PI),
        StepBSplineCurve bs => (bs.DomainStart, bs.DomainEnd),
        // A line profile: bracket around the direct projection of the query point.
        StepLine line => (line.ParameterOf(p) - 2, line.ParameterOf(p) + 2),
        _ => throw new StepUnsupportedEntityException(Id, Profile.GetType().Name,
            "profile curve of a surface of revolution")
    };

    private Vector3D Rotate(Vector3D point, double angle) => AxisPoint + RotateVector(point - AxisPoint, angle);

    /// <summary>Rodrigues rotation about the (unit) axis direction.</summary>
    private Vector3D RotateVector(Vector3D w, double angle)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var k = AxisDirection;
        return w * cos + Vector3D.Cross(k, w) * sin + k * (Vector3D.Dot(k, w) * (1 - cos));
    }
}
