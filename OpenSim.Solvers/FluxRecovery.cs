using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Superconvergent patch recovery of the flux q = −K·∇u of a scalar solve (Zienkiewicz and
/// Zhu), with the error estimate it gives.
/// <para>
/// The computed flux is constant per TET4 and linear per TET10, discontinuous between
/// elements, and one order less accurate than the field it comes from. Around every corner
/// node, one polynomial of the element's degree (linear on TET4, quadratic on TET10) is
/// fitted by least squares to the flux at the elements' sampling points — the centroid of a
/// TET4, the four Gauss points of a TET10, where the computed flux is most accurate. An
/// interior corner node takes its own patch's value; a node on the boundary, on a material
/// interface or in the middle of an edge takes the mean of every patch that contains it,
/// since a patch fitted around a boundary node only extrapolates to it. The recovered flux
/// is interpolated with the element's own shape functions.
/// </para>
/// <para>
/// Patches never cross a material: the normal flux is continuous at an interface but the
/// tangential flux jumps, so each material has its own value at a shared node, as the
/// nodal averages already keep (<see cref="ScalarSolverHelpers.NodalAverageWithSides"/>).
/// </para>
/// <para>
/// The error estimate is the energy norm of the difference between the recovered and the
/// computed flux, η² = Σ ∫(q* − q)·K⁻¹·(q* − q) dV, which converges to the true error where
/// the recovery is superconvergent (its effectivity tends to 1); it is reported against the
/// flux's own energy, ∫q·K⁻¹·q dV (the dissipated power of a conduction solve), as
/// η/√(‖q‖² + η²).
/// </para>
/// </summary>
public sealed class FluxRecovery
{
    private readonly ScalarDiffusionAssembler _assembler;
    private readonly FeMesh _mesh;
    private readonly double[] _solution;
    private readonly Func<int, ConductivityTensor> _conductivity;
    private readonly int[]? _groups;

    /// <summary>Recovered value per node (nodes of one material) and per (node, material)
    /// at nodes several materials share.</summary>
    private readonly Vector3D[] _value;
    private readonly Dictionary<(int Node, int Group), Vector3D> _sharedValue = new();
    private readonly bool[] _shared;

    private FluxRecovery(ScalarDiffusionAssembler assembler, double[] solution,
        Func<int, ConductivityTensor> conductivity, int[]? groups)
    {
        _assembler = assembler;
        _mesh = assembler.Mesh;
        _solution = solution;
        _conductivity = conductivity;
        _groups = groups;
        _value = new Vector3D[_mesh.NodeCount];
        _shared = new bool[_mesh.NodeCount];
        ElementError = new double[_mesh.ElementCount];
    }

    /// <summary>Nodes that no well-posed patch reached and took the plain average of the
    /// computed flux of their elements instead.</summary>
    public int FallbackNodes { get; private set; }

    /// <summary>The recovered flux per node; at a node several materials share, the side
    /// where it is larger (the others are in <see cref="Sides"/>).</summary>
    public Vector3D[] Nodal { get; private set; } = Array.Empty<Vector3D>();

    public InterfaceSides<Vector3D>? Sides { get; private set; }

    /// <summary>The root-mean-square of |q* − q| over each element, in flux units.</summary>
    public double[] ElementError { get; }

    /// <summary>η² = Σ ∫(q* − q)·K⁻¹·(q* − q) dV.</summary>
    public double ErrorEnergy { get; private set; }

    /// <summary>‖q‖² = Σ ∫q·K⁻¹·q dV of the computed flux.</summary>
    public double FluxEnergy { get; private set; }

    /// <summary>The estimated relative error of the flux in the energy norm, η/√(‖q‖² + η²).</summary>
    public double RelativeError => ErrorEnergy + FluxEnergy > 0 ? Math.Sqrt(ErrorEnergy / (ErrorEnergy + FluxEnergy)) : 0;

    /// <param name="conductivity">The tensor each element was solved with (it can differ
    /// from the assembler's, as in the staged conduction solve).</param>
    /// <param name="groups">One material id per element; null when the mesh is one material.</param>
    /// <param name="groupNames">The material of each id, for the per-side values.</param>
    public static FluxRecovery Recover(ScalarDiffusionAssembler assembler, IReadOnlyList<double> solution,
        Func<int, ConductivityTensor> conductivity, IReadOnlyList<int>? groups = null,
        IReadOnlyList<string>? groupNames = null, CancellationToken cancellationToken = default)
    {
        var recovery = new FluxRecovery(assembler, solution.Take(assembler.Mesh.NodeCount).ToArray(),
            conductivity, groups?.ToArray());
        recovery.Fit(cancellationToken);
        recovery.Display(groupNames);
        recovery.Estimate();
        return recovery;
    }

    private int GroupOf(int element) => _groups?[element] ?? 0;

    private int Degree => _assembler.IsQuadratic ? 2 : 1;

    /// <summary>The computed flux −K·∇u of one element at barycentric point L.</summary>
    public Vector3D ComputedAt(int element, ReadOnlySpan<double> l) =>
        _conductivity(element).Apply(_assembler.ElementGradientAt(element, l, _solution)) * -1;

    /// <summary>The recovered flux of one element at barycentric point L.</summary>
    public Vector3D RecoveredAt(int element, ReadOnlySpan<double> l)
    {
        var nodes = _mesh.GetElementNodes(element);
        int group = GroupOf(element);
        if (!_assembler.IsQuadratic)
        {
            var sum = Vector3D.Zero;
            for (int i = 0; i < 4; i++) sum += ValueAt(nodes[i], group) * l[i];
            return sum;
        }
        Span<double> n = stackalloc double[10];
        ScalarElements.Tet10Values(l, n);
        var total = Vector3D.Zero;
        for (int i = 0; i < 10; i++) total += ValueAt(nodes[i], group) * n[i];
        return total;
    }

    /// <summary>The recovered value at a node, on the side of one material.</summary>
    public Vector3D ValueAt(int node, int group) =>
        _shared[node] ? _sharedValue.GetValueOrDefault((node, group)) : _value[node];

    /// <summary>The barycentric coordinates of a point in one element (outside it they
    /// extrapolate the element's own functions).</summary>
    public double[] Barycentric(int element, Vector3D point)
    {
        var g = _assembler.BarycentricGradients(element);
        var nodes = _mesh.Elements[element];
        var centroid = (_mesh.Nodes[nodes.N0] + _mesh.Nodes[nodes.N1] + _mesh.Nodes[nodes.N2] + _mesh.Nodes[nodes.N3]) / 4;
        var d = point - centroid;
        return new[]
        {
            0.25 + Vector3D.Dot(g[0], d), 0.25 + Vector3D.Dot(g[1], d),
            0.25 + Vector3D.Dot(g[2], d), 0.25 + Vector3D.Dot(g[3], d)
        };
    }

    private Vector3D PointOf(int element, ReadOnlySpan<double> l)
    {
        var e = _mesh.Elements[element];
        return _mesh.Nodes[e.N0] * l[0] + _mesh.Nodes[e.N1] * l[1] + _mesh.Nodes[e.N2] * l[2] + _mesh.Nodes[e.N3] * l[3];
    }

    private static readonly double[][] CentroidOnly = { new[] { 0.25, 0.25, 0.25, 0.25 } };

    /// <summary>Where the computed flux is sampled: the centroid of a TET4, the 4 Gauss points
    /// of a TET10.</summary>
    private double[][] SamplePoints => _assembler.IsQuadratic ? Tet10Assembler.GaussPoints : CentroidOnly;

    /// <summary>A fitted patch: the polynomial's coefficients per flux component, about the
    /// patch's corner node, in coordinates scaled by the patch's reach.</summary>
    private sealed record Patch(Vector3D Centre, double Scale, double[] Coefficients, int Terms);

    private static void Basis(Vector3D local, int degree, Span<double> p)
    {
        p[0] = 1; p[1] = local.X; p[2] = local.Y; p[3] = local.Z;
        if (degree < 2) return;
        p[4] = local.X * local.X; p[5] = local.Y * local.Y; p[6] = local.Z * local.Z;
        p[7] = local.X * local.Y; p[8] = local.X * local.Z; p[9] = local.Y * local.Z;
    }

    private static Vector3D Evaluate(Patch patch, Vector3D point)
    {
        Span<double> p = stackalloc double[10];
        Basis((point - patch.Centre) / patch.Scale, patch.Terms == 4 ? 1 : 2, p);
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < patch.Terms; i++)
        {
            x += p[i] * patch.Coefficients[3 * i];
            y += p[i] * patch.Coefficients[3 * i + 1];
            z += p[i] * patch.Coefficients[3 * i + 2];
        }
        return new Vector3D(x, y, z);
    }

    /// <summary>
    /// Least squares over a patch's samples by Householder QR of the sample matrix (not the
    /// normal equations, whose condition number is the square of it: a ten-term quadratic
    /// patch lost five digits that way). A patch with fewer samples than terms, or whose
    /// samples do not determine the polynomial (a diagonal of R below 10⁻⁸ of the largest —
    /// samples on one plane), is not used.
    /// </summary>
    private Patch? FitPatch(int corner, List<int> elements)
    {
        int degree = Degree;
        int terms = degree == 1 ? 4 : 10;
        var samples = new List<(Vector3D X, Vector3D Q)>();
        foreach (int e in elements)
            foreach (var l in SamplePoints)
                samples.Add((PointOf(e, l), ComputedAt(e, l)));
        int rows = samples.Count;
        if (rows < terms) return null;

        var centre = _mesh.Nodes[corner];
        double scale = samples.Max(s => (s.X - centre).Length);
        if (!(scale > 0)) return null;

        var a = new double[rows, terms];
        var b = new double[rows, 3];
        Span<double> p = stackalloc double[10];
        for (int r = 0; r < rows; r++)
        {
            Basis((samples[r].X - centre) / scale, degree, p);
            for (int j = 0; j < terms; j++) a[r, j] = p[j];
            b[r, 0] = samples[r].Q.X;
            b[r, 1] = samples[r].Q.Y;
            b[r, 2] = samples[r].Q.Z;
        }

        // Householder: column by column, reflect the rest of the column onto its diagonal and
        // apply the same reflection to the right-hand sides.
        var diagonal = new double[terms];
        for (int j = 0; j < terms; j++)
        {
            double norm = 0;
            for (int r = j; r < rows; r++) norm += a[r, j] * a[r, j];
            norm = Math.Sqrt(norm);
            double alpha = a[j, j] > 0 ? -norm : norm;
            diagonal[j] = alpha;
            if (norm == 0) continue;
            a[j, j] -= alpha;
            double vv = 0;
            for (int r = j; r < rows; r++) vv += a[r, j] * a[r, j];
            if (vv == 0) continue;
            for (int k = j + 1; k < terms; k++)
            {
                double dot = 0;
                for (int r = j; r < rows; r++) dot += a[r, j] * a[r, k];
                double f = 2 * dot / vv;
                for (int r = j; r < rows; r++) a[r, k] -= f * a[r, j];
            }
            for (int k = 0; k < 3; k++)
            {
                double dot = 0;
                for (int r = j; r < rows; r++) dot += a[r, j] * b[r, k];
                double f = 2 * dot / vv;
                for (int r = j; r < rows; r++) b[r, k] -= f * a[r, j];
            }
        }
        double largest = diagonal.Max(Math.Abs);
        if (!(largest > 0) || diagonal.Any(d => !(Math.Abs(d) > 1e-8 * largest))) return null;

        var c = new double[terms * 3];
        for (int component = 0; component < 3; component++)
            for (int i = terms - 1; i >= 0; i--)
            {
                double sum = b[i, component];
                for (int k = i + 1; k < terms; k++) sum -= a[i, k] * c[3 * k + component];
                c[3 * i + component] = sum / diagonal[i];
            }
        return new Patch(centre, scale, c, terms);
    }

    private void Fit(CancellationToken cancellationToken)
    {
        int nodeCount = _mesh.NodeCount;
        int elementCount = _mesh.ElementCount;

        // Which nodes several materials share, and the material of every other node.
        var nodeGroup = new int[nodeCount];
        Array.Fill(nodeGroup, int.MinValue);
        for (int e = 0; e < elementCount; e++)
            foreach (int n in _mesh.GetElementNodes(e))
            {
                if (nodeGroup[n] == int.MinValue) nodeGroup[n] = GroupOf(e);
                else if (nodeGroup[n] != GroupOf(e)) _shared[n] = true;
            }

        // The elements around each corner node.
        var around = new List<int>?[nodeCount];
        for (int e = 0; e < elementCount; e++)
        {
            var t = _mesh.Elements[e];
            foreach (int n in new[] { t.N0, t.N1, t.N2, t.N3 })
                (around[n] ??= new List<int>()).Add(e);
        }
        var boundary = new bool[nodeCount];
        foreach (var t in _mesh.BoundaryTriangles)
            boundary[t.A] = boundary[t.B] = boundary[t.C] = true;

        // Every patch's polynomial at every node of its elements, summed per (node, material).
        var sum = new Vector3D[nodeCount];
        var count = new int[nodeCount];
        var sharedSum = new Dictionary<(int, int), (Vector3D Sum, int Count)>();
        var own = new Dictionary<(int, int), Vector3D>();
        var stamp = new int[nodeCount];
        Array.Fill(stamp, -1);
        int patchId = 0;
        for (int corner = 0; corner < nodeCount; corner++)
        {
            if (around[corner] is not { } elements) continue;
            if ((corner & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            foreach (var groupElements in elements.GroupBy(GroupOf))
            {
                int group = groupElements.Key;
                var members = groupElements.ToList();
                var patch = FitPatch(corner, members);
                if (patch is null) continue;
                patchId++;
                foreach (int e in members)
                    foreach (int n in _mesh.GetElementNodes(e))
                    {
                        if (stamp[n] == patchId) continue;
                        stamp[n] = patchId;
                        var value = Evaluate(patch, _mesh.Nodes[n]);
                        if (_shared[n])
                        {
                            var (s, c) = sharedSum.GetValueOrDefault((n, group));
                            sharedSum[(n, group)] = (s + value, c + 1);
                        }
                        else
                        {
                            sum[n] += value;
                            count[n]++;
                        }
                    }
                // An interior corner of one material is the point the patch is centred on.
                if (!boundary[corner] && !_shared[corner])
                    own[(corner, group)] = Evaluate(patch, _mesh.Nodes[corner]);
            }
        }

        for (int n = 0; n < nodeCount; n++)
            if (!_shared[n] && count[n] > 0) _value[n] = sum[n] / count[n];
        foreach (var ((n, group), (s, c)) in sharedSum)
            _sharedValue[(n, group)] = s / c;
        foreach (var ((n, _), value) in own)
            _value[n] = value;

        // What no patch reached: the volume-weighted mean of the computed flux of its
        // elements at the node.
        var fallback = new Dictionary<(int, int), (Vector3D Sum, double Weight)>();
        for (int e = 0; e < elementCount; e++)
        {
            var nodes = _mesh.GetElementNodes(e);
            int group = GroupOf(e);
            for (int i = 0; i < nodes.Length; i++)
            {
                int n = nodes[i];
                bool reached = _shared[n] ? _sharedValue.ContainsKey((n, group)) : count[n] > 0;
                if (reached) continue;
                double w = _mesh.ElementVolume(e);
                var (s, total) = fallback.GetValueOrDefault((n, group));
                fallback[(n, group)] = (s + ComputedAt(e, NodeBarycentric(i)) * w, total + w);
            }
        }
        foreach (var ((n, group), (s, w)) in fallback)
        {
            if (_shared[n]) _sharedValue[(n, group)] = s / w;
            else _value[n] = s / w;
        }
        FallbackNodes = fallback.Select(kv => kv.Key.Item1).Distinct().Count();
    }

    /// <summary>The barycentric coordinates of an element's i-th node.</summary>
    private static double[] NodeBarycentric(int i)
    {
        var l = new double[4];
        if (i < 4) l[i] = 1;
        else
        {
            var (a, b) = ScalarElements.TetEdges[i - 4];
            l[a] = l[b] = 0.5;
        }
        return l;
    }

    /// <summary>The display field: one value per node, the larger side at a shared node.</summary>
    private void Display(IReadOnlyList<string>? groupNames)
    {
        var nodal = (Vector3D[])_value.Clone();
        var perNode = new Dictionary<int, List<(int Group, Vector3D Value)>>();
        foreach (var ((n, group), value) in _sharedValue)
        {
            if (!perNode.TryGetValue(n, out var list)) perNode[n] = list = new List<(int, Vector3D)>();
            list.Add((group, value));
        }
        var ordered = new Dictionary<int, IReadOnlyList<(int Group, Vector3D Value)>>(perNode.Count);
        foreach (var (n, list) in perNode)
        {
            list.Sort((a, b) => a.Group.CompareTo(b.Group));
            nodal[n] = list.MaxBy(s => s.Value.LengthSquared).Value;
            ordered[n] = list;
        }
        Nodal = nodal;
        Sides = groupNames is null || ordered.Count == 0 ? null : new InterfaceSides<Vector3D>(groupNames, ordered);
    }

    /// <summary>
    /// The element errors and the energies, by a rule exact for the integrands: degree 2 on a
    /// TET4 (q* linear, q constant), degree 4 on a TET10 (q* quadratic, q linear).
    /// </summary>
    private void Estimate()
    {
        var rule = _assembler.IsQuadratic ? ScalarElements.TetDegree5 : ScalarElements.TetDegree2;
        double error = 0, energy = 0;
        for (int e = 0; e < _mesh.ElementCount; e++)
        {
            var k = _conductivity(e);
            bool definite = k.IsPositiveDefinite;
            double volume = _mesh.ElementVolume(e);
            double squared = 0;
            foreach (var (l, w) in rule)
            {
                var q = ComputedAt(e, l);
                var d = RecoveredAt(e, l) - q;
                squared += w * d.LengthSquared;
                if (!definite) continue;
                error += w * volume * Vector3D.Dot(d, k.Solve(d));
                energy += w * volume * Vector3D.Dot(q, k.Solve(q));
            }
            ElementError[e] = Math.Sqrt(squared);
        }
        ErrorEnergy = error;
        FluxEnergy = energy;
    }
}
