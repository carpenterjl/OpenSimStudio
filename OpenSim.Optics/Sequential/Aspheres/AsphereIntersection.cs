using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Aspheres;

/// <summary>
/// The one intersection routine every sag surface shares (docs/Optics-Conventions.md §17).
/// <para>
/// Along the ray p(t) = (x + tL, y + tM, tN) from its point on the vertex plane, the surface is
/// met where g(t) = tN − z(x + tL, y + tM) = 0, with g′(t) = N − L·∂z/∂x − M·∂z/∂y.
/// </para>
/// <list type="number">
/// <item>Start at the base conic's root (<see cref="ConicIntersection.Intersect"/>), or the
/// quadratic's other root for the alternate forms. When the base conic is missed the start is
/// the vertex plane, t = 0.</item>
/// <item>Damped Newton: the step −g/g′ is halved (up to 60 times) until |g| does not grow and
/// the sag is defined at the new point, which keeps every iterate inside the domain of the
/// conic's square root. At most 64 steps; converged when the step is below a few ulps of the
/// coordinates.</item>
/// <item>If Newton stalls (g′ = 0, no acceptable damped step, or the cap), bisection: the
/// bracket is searched outward from the start by doubling steps in both directions, then
/// halved until it is a few ulps wide.</item>
/// </list>
/// Any failure (no start, no bracket, the sag undefined inside the bracket) returns false and
/// the trace flags the ray <see cref="RayStatus.MissedSurface"/>.
/// </summary>
public static class AsphereIntersection
{
    private const int NewtonCap = 64;
    private const int HalvingCap = 60;
    private const int BracketCap = 80;
    private const int BisectionCap = 200;
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;

    /// <summary>The intersection of the ray through (x, y, 0) with unit direction
    /// <paramref name="direction"/> and <paramref name="shape"/>, with the signed distance from
    /// (x, y, 0); the contract of <see cref="SurfaceShape.Intersect"/>.</summary>
    public static bool Intersect(SagSurfaceShape shape, double x, double y, Vector3D direction,
        SequentialSurface surface, out Vector3D point, out double distance)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(surface);
        point = default;
        distance = double.NaN;
        double L = direction.X, M = direction.Y, N = direction.Z;

        double t;
        if (shape.UsesAlternateRoot)
        {
            if (!AlternateConicRoot(x, y, direction, surface.Curvature, surface.Conic, out t)) return false;
        }
        else if (!ConicIntersection.Intersect(x, y, direction, surface.Curvature, surface.Conic, out _, out t))
            t = 0;

        if (!Evaluate(shape, surface, x, y, L, M, N, t, out double g, out double gp))
        {
            if (shape.UsesAlternateRoot || t == 0) return false;
            t = 0;
            if (!Evaluate(shape, surface, x, y, L, M, N, t, out g, out gp)) return false;
        }
        double start = t, gStart = g;

        bool converged = g == 0;
        for (int iteration = 0; iteration < NewtonCap && !converged; iteration++)
        {
            if (!(gp != 0) || !double.IsFinite(gp)) break;
            double step = -g / gp;
            double tolerance = Tolerance(x, y, t);
            bool accepted = false;
            double lambda = 1;
            for (int halving = 0; halving < HalvingCap; halving++, lambda *= 0.5)
            {
                double trial = t + lambda * step;
                if (Evaluate(shape, surface, x, y, L, M, N, trial, out double gt, out double gpt)
                    && Math.Abs(gt) <= Math.Abs(g))
                {
                    double moved = Math.Abs(trial - t);
                    t = trial;
                    g = gt;
                    gp = gpt;
                    accepted = true;
                    if (g == 0 || moved <= tolerance) converged = true;
                    break;
                }
            }
            if (!accepted)
            {
                // At the rounding floor no step lowers |g|; a step that small is convergence.
                converged = Math.Abs(step) <= 64 * tolerance;
                break;
            }
        }

        if (!converged && !Bisect(shape, surface, x, y, L, M, N, start, gStart, out t)) return false;

        if (!shape.TrySag(x + t * L, y + t * M, surface, out _, out _, out _) || !double.IsFinite(t)) return false;
        point = new Vector3D(x + t * L, y + t * M, t * N);
        distance = t;
        return true;
    }

    /// <summary>
    /// The other root of the base conic's quadratic A·s² + 2B·s + C = 0
    /// (<see cref="ConicIntersection"/>): the roots' product is C/A, so with the vertex root
    /// s₁ = −C/q, q = B − sign(N)·√(B² − AC), the other is s₂ = −q/A. False for a plane, for a
    /// line with A = 0 (one root), when the line misses the conic, or when s₂ lies beyond the
    /// vertex sheet (1 − c(1 + k)z ≤ 0), where the sag is not defined.
    /// </summary>
    public static bool AlternateConicRoot(double x, double y, Vector3D direction, double curvature, double conic,
        out double distance)
    {
        distance = double.NaN;
        double c = curvature;
        if (c == 0) return false;
        double L = direction.X, M = direction.Y, N = direction.Z;
        double kp = 1 + conic;
        double a = c * (L * L + M * M + kp * N * N);
        double b = c * (x * L + y * M) - N;
        double cc = c * (x * x + y * y);
        double disc = b * b - a * cc;
        if (!(disc >= 0) || a == 0) return false;
        double q = b - Math.CopySign(Math.Sqrt(disc), N);
        double s = -q / a;
        if (!double.IsFinite(s) || !(1 - c * kp * (s * N) > 0)) return false;
        distance = s;
        return true;
    }

    private static double Tolerance(double x, double y, double t) =>
        8 * Eps * Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), Math.Max(Math.Abs(t), double.Epsilon));

    private static bool Evaluate(SagSurfaceShape shape, SequentialSurface surface, double x, double y,
        double L, double M, double N, double t, out double g, out double gp)
    {
        if (!double.IsFinite(t) || !shape.TrySag(x + t * L, y + t * M, surface, out double z, out double zx, out double zy))
        {
            g = gp = double.NaN;
            return false;
        }
        g = t * N - z;
        gp = N - L * zx - M * zy;
        return double.IsFinite(g);
    }

    private static bool Bisect(SagSurfaceShape shape, SequentialSurface surface, double x, double y,
        double L, double M, double N, double start, double gStart, out double root)
    {
        root = double.NaN;
        double h = Math.Max(1e-9 * Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), Math.Abs(start)), 1e-15);
        double lo = double.NaN, hi = double.NaN, gLo = 0;
        for (int i = 0; i < BracketCap && double.IsNaN(lo); i++, h *= 2)
        {
            foreach (double candidate in new[] { start + h, start - h })
            {
                if (!Evaluate(shape, surface, x, y, L, M, N, candidate, out double gc, out _)) continue;
                if (gc == 0)
                {
                    root = candidate;
                    return true;
                }
                if (Math.Sign(gc) != Math.Sign(gStart))
                {
                    lo = start;
                    gLo = gStart;
                    hi = candidate;
                    break;
                }
            }
        }
        if (double.IsNaN(lo)) return false;
        for (int i = 0; i < BisectionCap; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Math.Abs(hi - lo) <= Tolerance(x, y, mid) || mid == lo || mid == hi)
            {
                root = mid;
                return true;
            }
            if (!Evaluate(shape, surface, x, y, L, M, N, mid, out double gm, out _)) return false;
            if (gm == 0)
            {
                root = mid;
                return true;
            }
            if (Math.Sign(gm) == Math.Sign(gLo))
            {
                lo = mid;
                gLo = gm;
            }
            else hi = mid;
        }
        root = 0.5 * (lo + hi);
        return true;
    }
}
