namespace OpenSim.Geometry.Step.Tessellate;

using OpenSim.Geometry.Step;

/// <summary>
/// First-party B-spline basis evaluation (Piegl &amp; Tiller algorithms) shared by the
/// STEP B-spline curve and surface evaluators. Only clamped knot vectors reach this code
/// (B_SPLINE_*_WITH_KNOTS carries explicit knots; unclamped named forms are rejected at
/// schema resolution), so the domain is [knot[degree], knot[n+1]].
/// </summary>
public static class Nurbs
{
    /// <summary>
    /// Expands the STEP (multiplicities, knots) pair into a flat knot vector, validating
    /// the fundamental count identity #flatKnots = #control + degree + 1. Errors name the
    /// owning instance so a broken exporter is diagnosable from the log.
    /// </summary>
    public static double[] ExpandKnots(
        IReadOnlyList<long> multiplicities, IReadOnlyList<double> knots,
        int controlCount, int degree, int id)
    {
        if (multiplicities.Count != knots.Count)
            throw new StepImportException(
                $"#{id}: {multiplicities.Count} knot multiplicities but {knots.Count} knot values");
        long total = 0;
        foreach (long m in multiplicities)
        {
            if (m < 1) throw new StepImportException($"#{id}: knot multiplicity {m} is not positive");
            total += m;
        }
        if (total != controlCount + degree + 1)
            throw new StepImportException(
                $"#{id}: knot vector has {total} entries but {controlCount} control points with " +
                $"degree {degree} require {controlCount + degree + 1}");

        var flat = new double[total];
        int k = 0;
        double previous = double.NegativeInfinity;
        for (int i = 0; i < knots.Count; i++)
        {
            if (knots[i] <= previous)
                throw new StepImportException($"#{id}: knot values must be strictly increasing");
            previous = knots[i];
            for (long m = 0; m < multiplicities[i]; m++) flat[k++] = knots[i];
        }
        return flat;
    }

    /// <summary>Knot span index containing t (clamped vector; Piegl &amp; Tiller A2.1).</summary>
    public static int FindSpan(double[] knots, int degree, int controlCount, double t)
    {
        int n = controlCount - 1;
        if (t >= knots[n + 1]) return n;
        if (t <= knots[degree]) return degree;
        int low = degree, high = n + 1;
        int mid = (low + high) / 2;
        while (t < knots[mid] || t >= knots[mid + 1])
        {
            if (t < knots[mid]) high = mid;
            else low = mid;
            mid = (low + high) / 2;
        }
        return mid;
    }

    /// <summary>Nonzero basis functions N[span-degree..span] at t (Piegl &amp; Tiller A2.2). N has length degree+1.</summary>
    public static void Basis(double[] knots, int degree, int span, double t, double[] n)
    {
        Span<double> left = stackalloc double[degree + 1];
        Span<double> right = stackalloc double[degree + 1];
        n[0] = 1.0;
        for (int j = 1; j <= degree; j++)
        {
            left[j] = t - knots[span + 1 - j];
            right[j] = knots[span + j] - t;
            double saved = 0.0;
            for (int r = 0; r < j; r++)
            {
                double temp = n[r] / (right[r + 1] + left[j - r]);
                n[r] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }
            n[j] = saved;
        }
    }

    /// <summary>
    /// Basis functions and their first derivatives at t. The derivative uses the standard
    /// degree-reduction identity N'ᵢ,p = p·(Nᵢ,p₋₁/(uᵢ₊p−uᵢ) − Nᵢ₊₁,p₋₁/(uᵢ₊p₊₁−uᵢ₊₁)),
    /// with zero-denominator terms dropping out (the clamped-end convention).
    /// </summary>
    public static void BasisWithDerivative(double[] knots, int degree, int span, double t,
        double[] n, double[] dn)
    {
        Basis(knots, degree, span, t, n);
        if (degree == 0)
        {
            dn[0] = 0;
            return;
        }

        // Basis functions one degree down: Nlow[j] = N_{span-degree+1+j, degree-1}, j = 0..degree-1.
        var low = new double[degree];
        Basis(knots, degree - 1, span, t, low);

        for (int j = 0; j <= degree; j++)
        {
            int i = span - degree + j; // global index of N_{i,degree}
            double a = 0, b = 0;
            if (j > 0)
            {
                double denom = knots[i + degree] - knots[i];
                if (denom > 0) a = low[j - 1] / denom;
            }
            if (j < degree)
            {
                double denom = knots[i + degree + 1] - knots[i + 1];
                if (denom > 0) b = low[j] / denom;
            }
            dn[j] = degree * (a - b);
        }
    }
}
