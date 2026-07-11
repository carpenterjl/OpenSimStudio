namespace OpenSim.Rf.Surface;

/// <summary>
/// Symmetric Gauss quadrature rules on the reference triangle in barycentric
/// coordinates (Dunavant 1985). Only rules with ALL-POSITIVE weights are offered —
/// the same reasoning as the TET10 consistent-mass Keast choice: a negative weight can
/// turn a Gram-like quadrature sum indefinite, and the standard degree-3 (4-point) and
/// degree-7 (13-point) rules carry one, so they are deliberately absent. Weights sum
/// to 1; the caller maps barycentric → physical points and multiplies by the triangle
/// area. Every rule is pinned by a monomial-exactness oracle at 1e-14.
/// </summary>
public static class TriangleQuadrature
{
    /// <summary>The quadrature rule exact for polynomials up to <paramref name="degree"/>.
    /// Available degrees: 1 (1 pt), 2 (3 pt), 4 (6 pt), 5 (7 pt), 6 (12 pt). A request
    /// between available degrees rounds UP (never silently under-integrates).</summary>
    public static (double[] L1, double[] L2, double[] L3, double[] Weights) Rule(int degree)
    {
        if (degree <= 1) return Points(new[] { (1.0 / 3, 1.0 / 3, 1.0) });
        if (degree == 2)
            return Points(Orbit3(1.0 / 6, 1.0 / 3));
        if (degree <= 4)
            return Points(
                Orbit3(0.445948490915965, 0.223381589678011)
                .Concat(Orbit3(0.091576213509771, 0.109951743655322)));
        if (degree == 5)
            return Points(new[] { (1.0 / 3, 1.0 / 3, 0.225) }
                .Concat(Orbit3(0.470142064105115, 0.132394152788506))
                .Concat(Orbit3(0.101286507323456, 0.125939180544827)));
        if (degree == 6)
            return Points(
                Orbit3(0.249286745170910, 0.116786275726379)
                .Concat(Orbit3(0.063089014491502, 0.050844906370207))
                .Concat(Orbit6(0.310352451033785, 0.053145049844816, 0.082851075618374)));
        throw new ArgumentOutOfRangeException(nameof(degree),
            $"No all-positive symmetric rule of degree {degree} is provided (max 6).");
    }

    /// <summary>A 3-point symmetric orbit: (a, a, 1−2a) and its two rotations.</summary>
    private static IEnumerable<(double A, double B, double W)> Orbit3(double a, double w)
    {
        double c = 1 - 2 * a;
        yield return (a, a, w);
        yield return (a, c, w);
        yield return (c, a, w);
    }

    /// <summary>A 6-point orbit: all permutations of (a, b, 1−a−b).</summary>
    private static IEnumerable<(double A, double B, double W)> Orbit6(double a, double b, double w)
    {
        double c = 1 - a - b;
        yield return (a, b, w);
        yield return (b, a, w);
        yield return (a, c, w);
        yield return (c, a, w);
        yield return (b, c, w);
        yield return (c, b, w);
    }

    private static (double[] L1, double[] L2, double[] L3, double[] Weights) Points(
        IEnumerable<(double A, double B, double W)> points)
    {
        var list = points.ToArray();
        var l1 = new double[list.Length];
        var l2 = new double[list.Length];
        var l3 = new double[list.Length];
        var w = new double[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            l1[i] = list[i].A;
            l2[i] = list[i].B;
            l3[i] = 1 - list[i].A - list[i].B;
            w[i] = list[i].W;
        }
        return (l1, l2, l3, w);
    }
}
