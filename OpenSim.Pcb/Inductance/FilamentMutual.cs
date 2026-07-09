using OpenSim.Core.Numerics;

namespace OpenSim.Pcb.Inductance;

/// <summary>
/// Exact mutual inductance between two straight current filaments in arbitrary 3D
/// position — the closed-form solution of the Neumann double integral
/// M = (µ₀/4π)·cosε·∬ ds dt / r (Grover 1946, ch. 7; the same construction FastHenry
/// uses). Replaces the parallel-equal-bar-only approximation for chain composition:
/// collinear, staggered, oblique, and vertical (via-barrel) segment pairs are all exact
/// at the filament level. Finite rectangular cross-sections enter through the
/// geometric-mean-distance substitution on the parallel branch.
/// </summary>
public static class FilamentMutual
{
    private const double Mu0Over4Pi = 1e-7;                  // µ₀/4π [H/m]

    /// <summary>|cosε| below this is treated as perpendicular. This is exact physics, not
    /// an approximation: cosε multiplies the whole Neumann integral, so M ≡ 0 at 90°.</summary>
    private const double PerpendicularTolerance = 1e-12;

    /// <summary>1 − |cosε| below this routes to the parallel branch: the skew formula's
    /// µ/ν intermediates divide by 4l²m²sin²ε and lose ~8 digits at sin²ε ≈ 2e-9, while
    /// the parallel form is exact for parallel input. A sub-threshold tilt is flattened
    /// with O(ε·l/d) relative error (ε ≤ 4.5e-5 rad here) — real traces are either exactly
    /// parallel or degrees apart, so this only ever absorbs FP-noise tilts.</summary>
    private const double ParallelTolerance = 1e-9;

    /// <summary>
    /// Signed mutual inductance [H] between filaments A1→A2 and B1→B2. Positive when
    /// currents traversing A1→A2 and B1→B2 aid each other, negative when they oppose —
    /// the sign comes from the endpoints, so chain composers need no direction-cosine
    /// scaling of their own. <paramref name="parallelGmdDistance"/> (&gt; 0) replaces the
    /// geometric perpendicular distance on the parallel side-by-side branch to account
    /// for finite cross-sections; 0 means pure filaments. It is ignored for collinear
    /// pairs (a cross-section GMD is a perpendicular-offset correction — wrong physics
    /// end-to-end, and Grover shows the collinear finite-section correction is negligible).
    /// </summary>
    public static double Between(Vector3D a1, Vector3D a2, Vector3D b1, Vector3D b2,
        double parallelGmdDistance = 0)
    {
        var da = a2 - a1;
        var db = b2 - b1;
        double l = da.Length, m = db.Length;
        if (l <= 0 || m <= 0)
            throw new ArgumentException("Filament endpoints must be distinct (zero-length filament).");

        double cos = Math.Clamp(Vector3D.Dot(da, db) / (l * m), -1.0, 1.0);
        if (Math.Abs(cos) < PerpendicularTolerance)
            return 0.0;
        if (1 - Math.Abs(cos) < ParallelTolerance)
            return ParallelBranch(a1, b1, b2, da / l, l, m, Math.Sign(cos), parallelGmdDistance);
        return SkewBranch(a1, a2, b1, b2, l, m, cos);
    }

    /// <summary>
    /// Perpendicular distance between the (parallel) carrier lines of the two filaments —
    /// what a composer feeds to <see cref="PartialInductance.GeometricMeanDistance"/> to
    /// build the finite-section correction for side-by-side pairs.
    /// </summary>
    public static double PerpendicularSeparation(Vector3D a1, Vector3D a2, Vector3D b1)
    {
        var u = (a2 - a1).Normalized();
        var w = b1 - a1;
        return (w - Vector3D.Dot(w, u) * u).Length;
    }

    // ------------------------------------------------------------------
    // Parallel branch (includes anti-parallel, offset, and collinear pairs).
    //
    // With A spanning [0, l] along û and B projected to [s_a, s_b] at perpendicular
    // distance d, the Neumann integral has the even antiderivative
    // Φ(u) = u·asinh(u/ρ) − √(u² + ρ²), giving
    //   I = Φ(s_b) + Φ(l − s_a) − Φ(s_a) − Φ(l − s_b).
    // Full overlap (s_a = 0, s_b = l) reduces identically to Grover's equal-parallel
    // formula in PartialInductance.MutualInductanceParallel. As d → 0 the same
    // combination converges to the collinear form Φ₀(u) = |u|·ln|u| − |u| — the ln ρ
    // divergences cancel exactly when the segments do not overlap.
    // ------------------------------------------------------------------
    private static double ParallelBranch(Vector3D a1, Vector3D b1, Vector3D b2,
        Vector3D unit, double l, double m, int sign, double gmd)
    {
        double s1 = Vector3D.Dot(b1 - a1, unit);
        double s2 = Vector3D.Dot(b2 - a1, unit);
        double sa = Math.Min(s1, s2), sb = Math.Max(s1, s2);
        var offset = (b1 - a1) - s1 * unit;
        double d = offset.Length;

        double lengthScale = Math.Max(l, m);
        if (d <= 1e-9 * lengthScale)
        {
            // Collinear. True filaments that OVERLAP along the line have a divergent
            // Neumann integral — that geometry cannot come out of a valid trace chain,
            // so fail loudly instead of returning a huge number.
            double overlap = Math.Min(l, sb) - Math.Max(0, sa);
            if (overlap > 1e-9 * lengthScale)
                throw new InvalidOperationException(
                    "Collinear overlapping filaments have a divergent mutual inductance " +
                    "(the same copper counted twice) — the chain geometry is degenerate.");
            return sign * Mu0Over4Pi
                   * (Phi0(sb) + Phi0(l - sa) - Phi0(sa) - Phi0(l - sb));
        }

        double rho = gmd > 0 ? gmd : d;
        return sign * Mu0Over4Pi
               * (Phi(sb, rho) + Phi(l - sa, rho) - Phi(sa, rho) - Phi(l - sb, rho));
    }

    private static double Phi(double u, double rho) =>
        u * Math.Asinh(u / rho) - Math.Sqrt(u * u + rho * rho);

    private static double Phi0(double u)
    {
        double a = Math.Abs(u);
        return a <= 0 ? 0 : a * Math.Log(a) - a;
    }

    // ------------------------------------------------------------------
    // Skew branch — Grover 1946 eqs. (60)–(64). R1..R4 are the endpoint distances;
    // µ and ν locate the feet of the common perpendicular along each filament, d is its
    // length, and Ω is the solid-angle correction (zero for coplanar filaments).
    //
    // Stability notes: Grover's α² = R4² − R3² + R2² − R1² is IDENTICALLY 2lm·cosε
    // (expand the squares), so α² and the denominator D = 4l²m² − α⁴ = 4l²m²·sin²ε are
    // computed from the direction cosine directly — the R-difference form cancels
    // catastrophically for well-separated segments.
    // ------------------------------------------------------------------
    private static double SkewBranch(Vector3D a1, Vector3D a2, Vector3D b1, Vector3D b2,
        double l, double m, double cos)
    {
        double r1 = (a2 - b2).Length, r2 = (a2 - b1).Length;
        double r3 = (a1 - b1).Length, r4 = (a1 - b2).Length;
        double sin2 = 1 - cos * cos;
        double sin = Math.Sqrt(sin2);
        double alpha2 = 2 * l * m * cos;
        double denominator = 4 * l * l * m * m * sin2;

        double mu = l * (2 * m * m * (r2 * r2 - r3 * r3 - l * l)
                         + alpha2 * (r4 * r4 - r3 * r3 - m * m)) / denominator;
        double nu = m * (2 * l * l * (r4 * r4 - r3 * r3 - m * m)
                         + alpha2 * (r2 * r2 - r3 * r3 - l * l)) / denominator;
        double d = Math.Sqrt(Math.Max(0, r3 * r3 - mu * mu - nu * nu + 2 * mu * nu * cos));

        double terms = (mu + l) * Atanh(m / (r1 + r2))
                       + (nu + m) * Atanh(l / (r1 + r4))
                       - mu * Atanh(m / (r3 + r4))
                       - nu * Atanh(l / (r2 + r3));

        // The solid-angle term vanishes with d (|Ω| ≤ 2π), so coplanar pairs skip it.
        double omegaTerm = 0;
        if (d > 1e-12 * (l + m))
        {
            double omega =
                Math.Atan((d * d * cos + (mu + l) * (nu + m) * sin2) / (d * r1 * sin))
                - Math.Atan((d * d * cos + (mu + l) * nu * sin2) / (d * r2 * sin))
                + Math.Atan((d * d * cos + mu * nu * sin2) / (d * r3 * sin))
                - Math.Atan((d * d * cos + mu * (nu + m) * sin2) / (d * r4 * sin));
            omegaTerm = omega * d / sin;
        }

        return Mu0Over4Pi * cos * (2 * terms - omegaTerm);
    }

    /// <summary>
    /// atanh with the argument clamped just below 1. Touching filament endpoints (chain
    /// corners, R = 0 on one distance) drive an argument to exactly 1, but Grover's
    /// matching coefficient (µ + l, ν, …) vanishes there in exact arithmetic — e.g. for
    /// b1 = a2 the feet sit at the corner, µ = −l, ν = 0, and the formula reduces to his
    /// published meeting-point special case. The clamp only bounds the harmless
    /// ~0 · atanh(1⁻) float product; genuinely singular geometry (collinear overlap) is
    /// owned and rejected by the parallel branch.
    /// </summary>
    private static double Atanh(double x)
    {
        x = Math.Min(x, 1 - 1e-15);
        return 0.5 * Math.Log((1 + x) / (1 - x));
    }
}
