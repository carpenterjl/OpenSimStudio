using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step.Tessellate;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// A (possibly rational) B-spline curve with an explicit clamped knot vector. Rational
/// evaluation runs in homogeneous coordinates; derivatives use the quotient rule
/// C' = (A' − C·w')/w with A the weighted numerator.
/// </summary>
public sealed record StepBSplineCurve(
    int Id,
    int Degree,
    IReadOnlyList<Vector3D> ControlPoints,
    double[] Knots,
    IReadOnlyList<double> Weights) : StepCurve
{
    public double DomainStart => Knots[Degree];
    public double DomainEnd => Knots[ControlPoints.Count];

    public override Vector3D Point(double t)
    {
        var (a, w, _, _) = Evaluate(t, withDerivative: false);
        return a / w;
    }

    public override Vector3D Derivative(double t)
    {
        var (a, w, da, dw) = Evaluate(t, withDerivative: true);
        var c = a / w;
        return (da - c * dw) / w;
    }

    public override double ParameterOf(Vector3D p, double? hint = null)
    {
        // Dense deterministic seed grid over the knot domain, then Newton on the squared
        // distance. Endpoint parameters of edges are what this recovers, so the point IS
        // on the curve; failure to converge means broken geometry and is reported loudly.
        int spans = ControlPoints.Count - Degree;
        int samples = Math.Max(16, 8 * spans);
        double t0 = DomainStart, t1 = DomainEnd;
        double best = t0, bestD = double.MaxValue;
        for (int i = 0; i <= samples; i++)
        {
            double t = t0 + (t1 - t0) * i / samples;
            if (hint is not null) { /* geometric candidates dominate; hint unused for curves */ }
            double d = Vector3D.DistanceSquared(Point(t), p);
            if (d < bestD) { bestD = d; best = t; }
        }

        double u = best;
        for (int it = 0; it < 32; it++)
        {
            var c = Point(u) - p;
            var d1 = Derivative(u);
            double g = Vector3D.Dot(c, d1);              // ½ d/du |C-p|²
            double h = d1.LengthSquared + SecondDot(u, c); // its derivative (Gauss-Newton keeps d1²)
            if (h <= 0) break;
            double step = g / h;
            u = Math.Clamp(u - step, t0, t1);
            if (Math.Abs(step) < 1e-14 * (t1 - t0)) break;
        }
        return u;
    }

    /// <summary>c·C″ via central differencing of the analytic first derivative — second
    /// derivatives are only a Newton accelerant here, never geometry.</summary>
    private double SecondDot(double u, Vector3D c)
    {
        double h = 1e-7 * (DomainEnd - DomainStart);
        var d2 = (Derivative(Math.Min(u + h, DomainEnd)) - Derivative(Math.Max(u - h, DomainStart)))
                 / (Math.Min(u + h, DomainEnd) - Math.Max(u - h, DomainStart));
        return Vector3D.Dot(c, d2);
    }

    private (Vector3D A, double W, Vector3D DA, double DW) Evaluate(double t, bool withDerivative)
    {
        t = Math.Clamp(t, DomainStart, DomainEnd);
        int span = Nurbs.FindSpan(Knots, Degree, ControlPoints.Count, t);
        var n = new double[Degree + 1];
        var dn = new double[Degree + 1];
        if (withDerivative) Nurbs.BasisWithDerivative(Knots, Degree, span, t, n, dn);
        else Nurbs.Basis(Knots, Degree, span, t, n);

        Vector3D a = Vector3D.Zero, da = Vector3D.Zero;
        double w = 0, dw = 0;
        for (int j = 0; j <= Degree; j++)
        {
            int i = span - Degree + j;
            double wi = Weights[i];
            a += ControlPoints[i] * (n[j] * wi);
            w += n[j] * wi;
            if (withDerivative)
            {
                da += ControlPoints[i] * (dn[j] * wi);
                dw += dn[j] * wi;
            }
        }
        return (a, w, da, dw);
    }
}
