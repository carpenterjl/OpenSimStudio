using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>
/// Analytic static potential integrals over a flat triangle (Wilton–Rao et al. 1984):
/// I0 = ∫_T dS′/R and the vector Iρ = ∫_T (r′ − ρ) dS′/R, where ρ is the observation
/// point's projection onto the triangle plane and R = |r − r′|. Every linear source
/// moment λ′/R is affine in r′, so these two cover the full RWG static kernel exactly.
///
/// The closed forms are edge sums of ln((R⁺+l⁺)/(R⁻+l⁻)) and arctangent (solid-angle)
/// terms. Numerical guards, each with a documented limit:
///  • Observation ON an edge's line (R⁰ → 0, the in-plane case every coplanar patch
///    pair hits): that edge's ln term carries coefficients t⁰ → 0 and (R⁰)² → 0, and
///    its true contribution vanishes like R⁰²·ln R⁰ — the term is SKIPPED below a
///    relative threshold rather than evaluated as 0·∞.
///  • ln cancellation for edges far on the negative axis: (R+l)(R−l) = (R⁰)² gives the
///    algebraically identical flipped form ln((R⁻−l⁻)/(R⁺−l⁺)), chosen when l⁺+l⁻ < 0.
///  • h = 0 (in-plane observation, the dominant configuration here): the β arctangent
///    terms carry the factor |h| and are skipped exactly.
/// Oracle-tested against a subdivision brute force at observation = vertex, edge
/// midpoint, centroid, in-plane outside, and off-plane.
/// </summary>
public static class TrianglePotentials
{
    /// <summary>Relative threshold below which the observation counts as ON an edge
    /// line and that edge's logarithm is skipped (see the class remarks).</summary>
    private const double OnLineRelative = 1e-12;

    /// <summary>Computes I0 = ∫dS′/R, Iρ = ∫(r′−ρ)dS′/R (in-plane vector), and the
    /// projection ρ of the observation onto the triangle plane. Re-anchor via
    /// ∫r′ dS′/R = Iρ + ρ·I0.</summary>
    public static (double I0, Vector3D IRho, Vector3D Projection) Integrals(
        Vector3D a, Vector3D b, Vector3D c, Vector3D observation)
    {
        var normal = Vector3D.Cross(b - a, c - a);
        double doubleArea = normal.Length;
        if (doubleArea <= 0)
            throw new ArgumentException("The triangle is degenerate (zero area).");
        var n = normal / doubleArea;

        double h = Vector3D.Dot(observation - a, n);
        var rho = observation - n * h;
        double absH = Math.Abs(h);

        double i0 = 0;
        var iRho = new Vector3D(0, 0, 0);
        Span<(Vector3D V1, Vector3D V2)> edges = stackalloc (Vector3D, Vector3D)[]
        {
            (a, b), (b, c), (c, a)
        };

        foreach (var (v1, v2) in edges)
        {
            var edge = v2 - v1;
            double edgeLength = edge.Length;
            var s = edge / edgeLength;
            var m = Vector3D.Cross(s, n);   // outward in-plane edge normal (CCW triangle)

            double lMinus = Vector3D.Dot(v1 - rho, s);
            double lPlus = Vector3D.Dot(v2 - rho, s);
            var perpendicular = (v1 - rho) - s * lMinus;   // ρ → edge line, in plane
            double t0 = Vector3D.Dot(perpendicular, m);     // signed distance to the edge
            double r0Squared = perpendicular.LengthSquared + h * h;
            double rMinus = (v1 - observation).Length;
            double rPlus = (v2 - observation).Length;

            // ln((R⁺+l⁺)/(R⁻+l⁻)) — flipped form when both arc lengths sit on the
            // negative side (avoids R+l cancellation); skipped on the edge line.
            double onLine = OnLineRelative * edgeLength;
            double logTerm = 0;
            if (r0Squared > onLine * onLine)
            {
                logTerm = lPlus + lMinus >= 0
                    ? Math.Log((rPlus + lPlus) / (rMinus + lMinus))
                    : Math.Log((rMinus - lMinus) / (rPlus - lPlus));
            }

            i0 += t0 * logTerm;
            if (absH > 0)
            {
                double betaPlus = Math.Atan(t0 * lPlus / (r0Squared + absH * rPlus));
                double betaMinus = Math.Atan(t0 * lMinus / (r0Squared + absH * rMinus));
                i0 -= absH * (betaPlus - betaMinus);
            }

            iRho += m * (0.5 * (r0Squared * logTerm + lPlus * rPlus - lMinus * rMinus));
        }

        return (i0, iRho, rho);
    }
}
