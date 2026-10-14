using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// The coefficient of a convection condition on each of its triangles: the condition's
/// own everywhere, or the mean over each triangle of the local law its
/// <see cref="ConvectionProfile"/> describes. One place, so the conduction matrix and the
/// ambient loads of every thermal solver read the same numbers.
/// </summary>
public static class ConvectionTerms
{
    /// <summary>Sub-triangles per edge of the quadrature over a triangle (n² points).</summary>
    private const int Subdivisions = 6;

    /// <summary>Throws with an actionable message when the condition cannot be evaluated.</summary>
    public static void Validate(Convection convection)
    {
        if (!(convection.Coefficient > 0))
            throw new InvalidOperationException(
                $"Convection '{convection.Name}': the heat transfer coefficient must be positive.");
        if (convection.Profile is not { } profile) return;
        string name = $"Convection '{convection.Name}'";
        if (!(profile.FlowDirection.Length > 0))
            throw new InvalidOperationException($"{name}: the profile's flow direction has no length.");
        if (profile.Exponent is { } exponent && !(exponent > -1 && exponent <= 0))
            throw new InvalidOperationException(
                $"{name}: the boundary-layer exponent must lie in (−1, 0] (−0.5 laminar, about −0.2 turbulent); it is {exponent:g3}.");
        if (profile.Distances is not null || profile.Factors is not null)
        {
            if (profile.Distances is not { Count: > 0 } distances || profile.Factors is not { } factors || factors.Count != distances.Count)
                throw new InvalidOperationException($"{name}: the factor table needs as many factors as distances, and at least one.");
            for (int i = 1; i < distances.Count; i++)
                if (!(distances[i] > distances[i - 1]))
                    throw new InvalidOperationException($"{name}: the factor table's distances must rise.");
            if (factors.Any(f => !(f >= 0) || double.IsInfinity(f)))
                throw new InvalidOperationException($"{name}: the table's factors must be finite and not negative.");
        }
        foreach (var wake in profile.Wakes)
            if (!(wake.HalfWidth > 0) || !(wake.Length > 0) || !(wake.Factor >= 0) || double.IsInfinity(wake.Factor))
                throw new InvalidOperationException(
                    $"{name}: a wake needs a positive half-width and length and a factor that is not negative.");
    }

    /// <summary>Each triangle of the condition's faces with its coefficient [W/(m²·K)].</summary>
    public static IReadOnlyList<(BoundaryTriangle Triangle, double Coefficient)> Triangles(FeMesh mesh, Convection convection)
    {
        var triangles = mesh.GetFaceTriangles(convection.FaceIds);
        var result = new (BoundaryTriangle, double)[triangles.Count];
        if (convection.Profile is not { } profile)
        {
            for (int i = 0; i < triangles.Count; i++) result[i] = (triangles[i], convection.Coefficient);
            return result;
        }

        var flow = profile.FlowDirection / profile.FlowDirection.Length;
        double run = 0;
        foreach (var t in triangles)
            foreach (int node in new[] { t.A, t.B, t.C })
                run = Math.Max(run, Vector3D.Dot(mesh.Nodes[node] - profile.LeadingEdge, flow));
        double floor = run > 0 ? run / 1000 : 1e-9;

        var areas = new double[triangles.Count];
        var development = new double[triangles.Count];
        var local = new double[triangles.Count];
        for (int i = 0; i < triangles.Count; i++)
        {
            var t = triangles[i];
            Vector3D a = mesh.Nodes[t.A], ab = mesh.Nodes[t.B] - a, ac = mesh.Nodes[t.C] - a;
            areas[i] = 0.5 * Vector3D.Cross(ab, ac).Length;
            // The development's mean over the triangle exactly; the table and wakes as a
            // development-weighted mean over the sub-triangles' centroids.
            development[i] = profile.Exponent is { } exponent
                ? MeanPower(Vector3D.Dot(a - profile.LeadingEdge, flow), Vector3D.Dot(a + ab - profile.LeadingEdge, flow),
                    Vector3D.Dot(a + ac - profile.LeadingEdge, flow), exponent, floor)
                : 1.0;
            double weighted = 0, weights = 0;
            foreach (var (u, v) in SubCentroids())
            {
                var p = a + ab * u + ac * v;
                double x = Vector3D.Dot(p - profile.LeadingEdge, flow);
                double g = profile.Exponent is { } e ? Math.Pow(Math.Max(x, floor), e) : 1.0;
                weighted += g * Table(profile, x) * Wakes(profile, p, flow);
                weights += g;
            }
            local[i] = development[i] * weighted / weights;
        }

        // The development is scaled so its area-mean is 1: the condition's coefficient is
        // the faces' mean of the developing layer; the table and wakes then act on it.
        double scale = 1;
        if (profile.Exponent is not null)
        {
            double area = 0, developed = 0;
            for (int i = 0; i < triangles.Count; i++) { area += areas[i]; developed += areas[i] * development[i]; }
            scale = area / developed;
        }
        for (int i = 0; i < triangles.Count; i++)
            result[i] = (triangles[i], convection.Coefficient * scale * local[i]);
        return result;
    }

    /// <summary>One line for the log: the coefficient's spread when it varies.</summary>
    public static string Describe(FeMesh mesh, Convection convection)
    {
        if (convection.Profile is null) return $"h = {convection.Coefficient:g4} W/(m²·K)";
        var terms = Triangles(mesh, convection);
        double area = 0, sum = 0, low = double.PositiveInfinity, high = 0;
        foreach (var (t, h) in terms)
        {
            double a = ScalarSolverHelpers.TriangleArea(mesh, t);
            area += a;
            sum += a * h;
            low = Math.Min(low, h);
            high = Math.Max(high, h);
        }
        var parts = new List<string>();
        if (convection.Profile.Exponent is { } exponent) parts.Add($"boundary layer h ∝ x^{exponent:g3}");
        if (convection.Profile.Distances is { Count: > 0 } d) parts.Add($"a {d.Count}-point factor table");
        if (convection.Profile.Wakes.Count > 0) parts.Add($"{convection.Profile.Wakes.Count} wake(s)");
        return $"h varies ({string.Join(", ", parts)}): {low:g4} … {high:g4} W/(m²·K), area-mean {sum / area:g4}";
    }

    /// <summary>
    /// The mean of xⁿ over a triangle whose corners lie at x₁, x₂, x₃ along the flow
    /// (x is linear on it): twice the second divided difference of G(x) = x^(n+2)/((n+1)(n+2)),
    /// G'' = xⁿ — exact, including the integrable singularity at a corner on the leading
    /// edge. Corners upstream of the edge count as on it; a triangle lying along the edge
    /// takes the value at the floor.
    /// </summary>
    internal static double MeanPower(double x1, double x2, double x3, double n, double floor)
    {
        Span<double> x = stackalloc double[3] { Math.Max(x1, 0), Math.Max(x2, 0), Math.Max(x3, 0) };
        x.Sort();
        double span = x[2] - x[0];
        if (span <= 1e-9 * Math.Max(x[2], floor)) return Math.Pow(Math.Max(x[1], floor), n);
        double tolerance = 1e-9 * span;
        double G(double v) => Math.Pow(v, n + 2) / ((n + 1) * (n + 2));
        double G1(double v) => Math.Pow(v, n + 1) / (n + 1);
        double First(double p, double q) => q - p <= tolerance ? G1(0.5 * (p + q)) : (G(q) - G(p)) / (q - p);
        return 2 * (First(x[1], x[2]) - First(x[0], x[1])) / span;
    }

    private static double Table(ConvectionProfile profile, double x)
    {
        if (profile.Distances is not { Count: > 0 } distances || profile.Factors is not { } factors) return 1;
        if (x <= distances[0]) return factors[0];
        if (x >= distances[^1]) return factors[^1];
        int hi = 1;
        while (distances[hi] < x) hi++;
        double w = (x - distances[hi - 1]) / (distances[hi] - distances[hi - 1]);
        return factors[hi - 1] + w * (factors[hi] - factors[hi - 1]);
    }

    private static double Wakes(ConvectionProfile profile, Vector3D p, Vector3D flow)
    {
        double factor = 1;
        foreach (var wake in profile.Wakes)
        {
            var r = p - wake.Position;
            double along = Vector3D.Dot(r, flow);
            if (along < 0 || along > wake.Length) continue;
            if ((r - flow * along).Length > wake.HalfWidth) continue;
            factor *= wake.Factor + (1 - wake.Factor) * along / wake.Length;
        }
        return factor;
    }

    /// <summary>The centroids of the n² equal sub-triangles, as (u, v) on the edges AB, AC.</summary>
    private static IEnumerable<(double U, double V)> SubCentroids()
    {
        const int n = Subdivisions;
        for (int i = 0; i < n; i++)
            for (int j = 0; i + j < n; j++)
            {
                yield return ((i + 1.0 / 3) / n, (j + 1.0 / 3) / n);
                if (i + j < n - 1) yield return ((i + 2.0 / 3) / n, (j + 2.0 / 3) / n);
            }
    }
}
