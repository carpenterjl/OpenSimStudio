using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step.Tessellate;

namespace OpenSim.Geometry.Step.Schema;

/// <summary>
/// A (possibly rational) B-spline surface with explicit clamped knot vectors. Control
/// points are stored row-major: index = iu·CountV + iv, matching the STEP control-grid
/// list-of-lists layout (outer list = u direction). Inversion has no closed form here —
/// the UV mapper drives <see cref="InvertNear"/> (Gauss-Newton) with a continuity seed,
/// and <see cref="InvertRaw"/> deliberately throws.
/// </summary>
public sealed record StepBSplineSurface(
    int Id,
    int DegreeU,
    int DegreeV,
    int CountU,
    int CountV,
    IReadOnlyList<Vector3D> ControlPoints,
    double[] KnotsU,
    double[] KnotsV,
    IReadOnlyList<double> Weights) : StepSurface
{
    public double DomainStartU => KnotsU[DegreeU];
    public double DomainEndU => KnotsU[CountU];
    public double DomainStartV => KnotsV[DegreeV];
    public double DomainEndV => KnotsV[CountV];

    public override Vector3D Point(double u, double v)
    {
        var (a, w, _, _, _, _) = Evaluate(u, v, withDerivatives: false);
        return a / w;
    }

    public override Vector3D PartialU(double u, double v)
    {
        var (a, w, au, wu, _, _) = Evaluate(u, v, withDerivatives: true);
        var s = a / w;
        return (au - s * wu) / w;
    }

    public override Vector3D PartialV(double u, double v)
    {
        var (a, w, _, _, av, wv) = Evaluate(u, v, withDerivatives: true);
        var s = a / w;
        return (av - s * wv) / w;
    }

    public override (double U, double V) InvertRaw(Vector3D p) =>
        throw new StepGeometryException(
            $"#{Id}: B-spline surface inversion requires a Newton seed — use InvertNear");

    /// <summary>
    /// Gauss-Newton closest-point from the given seed. Returns null on non-convergence
    /// (the caller escalates: grid-refine fallback, then a loud failure). The step solves
    /// the 2×2 normal equations of J = [Su Sv] and is damped to one knot span per
    /// iteration so a bad seed cannot fling the iterate across the patch.
    /// </summary>
    public (double U, double V)? InvertNear(Vector3D p, double seedU, double seedV, double acceptTolMeters)
    {
        double u = Math.Clamp(seedU, DomainStartU, DomainEndU);
        double v = Math.Clamp(seedV, DomainStartV, DomainEndV);
        double spanU = MaxSpan(KnotsU, DegreeU, CountU);
        double spanV = MaxSpan(KnotsV, DegreeV, CountV);
        double domU = DomainEndU - DomainStartU, domV = DomainEndV - DomainStartV;

        for (int it = 0; it < 48; it++)
        {
            var r = Point(u, v) - p;
            var su = PartialU(u, v);
            var sv = PartialV(u, v);

            double a11 = su.LengthSquared, a12 = Vector3D.Dot(su, sv), a22 = sv.LengthSquared;
            double b1 = -Vector3D.Dot(r, su), b2 = -Vector3D.Dot(r, sv);
            double det = a11 * a22 - a12 * a12;
            if (det <= 1e-30 * a11 * a22 + double.Epsilon) return null; // degenerate tangent plane

            double du = (b1 * a22 - b2 * a12) / det;
            double dv = (b2 * a11 - b1 * a12) / det;
            du = Math.Clamp(du, -spanU, spanU);
            dv = Math.Clamp(dv, -spanV, spanV);
            u = Math.Clamp(u + du, DomainStartU, DomainEndU);
            v = Math.Clamp(v + dv, DomainStartV, DomainEndV);

            if (Math.Abs(du) < 1e-12 * domU && Math.Abs(dv) < 1e-12 * domV)
                break;
        }

        return (Point(u, v) - p).Length <= acceptTolMeters ? (u, v) : null;
    }

    private static double MaxSpan(double[] knots, int degree, int count)
    {
        double max = 0;
        for (int i = degree; i < count; i++) max = Math.Max(max, knots[i + 1] - knots[i]);
        return max;
    }

    private (Vector3D A, double W, Vector3D AU, double WU, Vector3D AV, double WV) Evaluate(
        double u, double v, bool withDerivatives)
    {
        u = Math.Clamp(u, DomainStartU, DomainEndU);
        v = Math.Clamp(v, DomainStartV, DomainEndV);
        int spanU = Nurbs.FindSpan(KnotsU, DegreeU, CountU, u);
        int spanV = Nurbs.FindSpan(KnotsV, DegreeV, CountV, v);
        var nu = new double[DegreeU + 1];
        var nv = new double[DegreeV + 1];
        var dnu = new double[DegreeU + 1];
        var dnv = new double[DegreeV + 1];
        if (withDerivatives)
        {
            Nurbs.BasisWithDerivative(KnotsU, DegreeU, spanU, u, nu, dnu);
            Nurbs.BasisWithDerivative(KnotsV, DegreeV, spanV, v, nv, dnv);
        }
        else
        {
            Nurbs.Basis(KnotsU, DegreeU, spanU, u, nu);
            Nurbs.Basis(KnotsV, DegreeV, spanV, v, nv);
        }

        Vector3D a = Vector3D.Zero, au = Vector3D.Zero, av = Vector3D.Zero;
        double w = 0, wu = 0, wv = 0;
        for (int ju = 0; ju <= DegreeU; ju++)
        {
            int iu = spanU - DegreeU + ju;
            for (int jv = 0; jv <= DegreeV; jv++)
            {
                int iv = spanV - DegreeV + jv;
                int index = iu * CountV + iv;
                double wi = Weights[index];
                var pw = ControlPoints[index] * wi;

                double bb = nu[ju] * nv[jv];
                a += pw * bb;
                w += wi * bb;
                if (withDerivatives)
                {
                    double bu = dnu[ju] * nv[jv];
                    double bv = nu[ju] * dnv[jv];
                    au += pw * bu;
                    wu += wi * bu;
                    av += pw * bv;
                    wv += wi * bv;
                }
            }
        }
        return (a, w, au, wu, av, wv);
    }
}
