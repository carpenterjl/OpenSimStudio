using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// The element library of the scalar solvers beyond TET4: the 10-node quadratic
/// tetrahedron and its 6-node surface triangle, both straight-edged (subparametric), so the
/// barycentric gradients are constant per element and every integral below is exact.
/// <para>
/// Node order follows <see cref="FeMesh.GetElementNodes"/>: the four corners, then the
/// mid-edge nodes 01, 02, 03, 12, 13, 23. A surface triangle (A, B, C) is followed by its
/// mid-edge nodes AB, BC, CA. Corner N = L(2L − 1), mid-edge N = 4LᵢLⱼ.
/// </para>
/// <para>
/// The reference integrals are not typed in: they are the shape functions multiplied out
/// as polynomials in the barycentric coordinates and integrated monomial by monomial,
/// ∫ L₁^a L₂^b L₃^c dA = 2A·a!b!c!/(a+b+c+2)! on a triangle and the 3! and +3 analogue on
/// a tetrahedron.
/// </para>
/// </summary>
internal static class ScalarElements
{
    /// <summary>The corner pairs of the six TET10 mid-edge nodes, in node order.</summary>
    public static readonly (int I, int J)[] TetEdges = { (0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3) };

    /// <summary>The corner pairs of the three T6 mid-edge nodes: AB, BC, CA.</summary>
    public static readonly (int I, int J)[] TriEdges = { (0, 1), (1, 2), (2, 0) };

    /// <summary>∫Nᵢ dV / V over a TET10: what a uniform source puts on each node.</summary>
    public static readonly double[] Tet10Load = Loads(4, TetEdges);

    /// <summary>∫Nᵢ dA / A over a T6: what a uniform surface flux puts on each node.</summary>
    public static readonly double[] Tri6Load = Loads(3, TriEdges);

    /// <summary>∫NᵢNⱼ dA / A over a T6: the surface mass of a Robin term.</summary>
    public static readonly double[,] Tri6Mass = Mass(3, TriEdges);

    /// <summary>The ten TET10 shape functions at barycentric point L.</summary>
    public static void Tet10Values(ReadOnlySpan<double> l, Span<double> n)
    {
        for (int i = 0; i < 4; i++) n[i] = l[i] * (2 * l[i] - 1);
        for (int k = 0; k < 6; k++) n[4 + k] = 4 * l[TetEdges[k].I] * l[TetEdges[k].J];
    }

    /// <summary>The ten TET10 shape-function gradients at barycentric point L, from the
    /// element's constant barycentric gradients: corner (4L − 1)∇L, mid-edge
    /// 4(Lᵢ∇Lⱼ + Lⱼ∇Lᵢ).</summary>
    public static void Tet10Gradients(Vector3D[] barycentric, ReadOnlySpan<double> l, Span<Vector3D> g)
    {
        for (int i = 0; i < 4; i++) g[i] = barycentric[i] * (4 * l[i] - 1);
        for (int k = 0; k < 6; k++)
        {
            var (i, j) = TetEdges[k];
            g[4 + k] = (barycentric[j] * l[i] + barycentric[i] * l[j]) * 4;
        }
    }

    /// <summary>The six T6 shape functions at barycentric point L.</summary>
    public static void Tri6Values(ReadOnlySpan<double> l, Span<double> n)
    {
        for (int i = 0; i < 3; i++) n[i] = l[i] * (2 * l[i] - 1);
        for (int k = 0; k < 3; k++) n[3 + k] = 4 * l[TriEdges[k].I] * l[TriEdges[k].J];
    }

    /// <summary>
    /// A triangle rule exact to degree 4 with positive weights (summing to 1; they multiply
    /// the area): three-point Gauss–Legendre in both directions of the square collapsed onto
    /// the triangle, L = (u, v(1 − u), (1 − u)(1 − v)) with the Jacobian 2(1 − u). Degree k
    /// in L is degree k + 1 in u, inside the rule's 5.
    /// </summary>
    public static readonly (double[] L, double W)[] TriangleRule = BuildTriangleRule();

    /// <summary>The 4-point tetrahedron rule exact to degree 2 (equal weights).</summary>
    public static readonly (double[] L, double W)[] TetDegree2 =
        Tet10Assembler.GaussPoints.Select(p => (p, 0.25)).ToArray();

    /// <summary>The 14-point tetrahedron rule exact to degree 5, all weights positive.</summary>
    public static (double[] L, double W)[] TetDegree5 => Tet10Assembler.MassQuadrature;

    private static (double[] L, double W)[] BuildTriangleRule()
    {
        double s = Math.Sqrt(0.6);
        var nodes = new[] { (1 - s) / 2, 0.5, (1 + s) / 2 };
        var weights = new[] { 5.0 / 18, 8.0 / 18, 5.0 / 18 };
        var rule = new List<(double[], double)>(9);
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
            {
                double u = nodes[a], v = nodes[b];
                rule.Add((new[] { u, v * (1 - u), (1 - u) * (1 - v) }, 2 * weights[a] * weights[b] * (1 - u)));
            }
        return rule.ToArray();
    }

    // ---- exact reference integrals ---------------------------------------------------

    /// <summary>A polynomial in the barycentric coordinates: exponent tuple → coefficient.</summary>
    private sealed class Poly : Dictionary<string, (int[] Exponents, double Coefficient)>
    {
        public void Add(int[] exponents, double coefficient)
        {
            string key = string.Join(",", exponents);
            this[key] = TryGetValue(key, out var term) ? (exponents, term.Coefficient + coefficient) : (exponents, coefficient);
        }

        public Poly Times(Poly other)
        {
            var product = new Poly();
            foreach (var (a, ca) in Values)
                foreach (var (b, cb) in other.Values)
                    product.Add(a.Zip(b, (x, y) => x + y).ToArray(), ca * cb);
            return product;
        }

        /// <summary>∫ over the simplex of dimension n − 1, divided by its measure.</summary>
        public double Integrate()
        {
            int dimension = Values.First().Exponents.Length - 1;
            double total = 0;
            foreach (var (exponents, c) in Values)
            {
                double numerator = Factorial(dimension);
                foreach (int a in exponents) numerator *= Factorial(a);
                total += c * numerator / Factorial(exponents.Sum() + dimension);
            }
            return total;
        }
    }

    private static double Factorial(int n)
    {
        double f = 1;
        for (int i = 2; i <= n; i++) f *= i;
        return f;
    }

    private static Poly[] Shapes(int corners, (int I, int J)[] edges)
    {
        var shapes = new Poly[corners + edges.Length];
        for (int i = 0; i < corners; i++)
        {
            var square = new int[corners];
            square[i] = 2;
            var linear = new int[corners];
            linear[i] = 1;
            shapes[i] = new Poly();
            shapes[i].Add(square, 2);
            shapes[i].Add(linear, -1);
        }
        for (int k = 0; k < edges.Length; k++)
        {
            var product = new int[corners];
            product[edges[k].I] = 1;
            product[edges[k].J] = 1;
            shapes[corners + k] = new Poly();
            shapes[corners + k].Add(product, 4);
        }
        return shapes;
    }

    private static double[] Loads(int corners, (int I, int J)[] edges) =>
        Shapes(corners, edges).Select(s => s.Integrate()).ToArray();

    private static double[,] Mass(int corners, (int I, int J)[] edges)
    {
        var shapes = Shapes(corners, edges);
        var mass = new double[shapes.Length, shapes.Length];
        for (int i = 0; i < shapes.Length; i++)
            for (int j = 0; j < shapes.Length; j++)
                mass[i, j] = shapes[i].Times(shapes[j]).Integrate();
        return mass;
    }
}
