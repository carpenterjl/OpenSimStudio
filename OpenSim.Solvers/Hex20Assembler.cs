using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Assembly of the global stiffness system for linear isotropic elasticity over 20-node
/// serendipity hexahedra (SOLID186-class), the element family the reference beam study was
/// run with. DOF numbering is (node·3 + axis), identical to the tetrahedra — mid-edge nodes
/// are ordinary nodes.
/// <para>
/// Unlike <see cref="Tet10Assembler"/> this is genuinely ISOPARAMETRIC: a hexahedron's
/// Jacobian varies over the element, so there is no constant-gradient shortcut and J is
/// inverted at every quadrature point. What makes that affordable on a mapped mesh is the
/// congruence cache described on <see cref="ElementKey"/>.
/// </para>
/// </summary>
public sealed class Hex20Assembler : IElasticityAssembler
{
    // 3x3x3 Gauss (27 points), used for BOTH stiffness and mass.
    //
    // Not the 2x2x2 reduced rule a commercial code defaults to: reduced integration on a
    // 20-node hex admits spurious zero-energy (hourglass) modes, and the stabilization that
    // suppresses them is machinery this solver does not carry. On a modal solve those modes
    // are not a cosmetic artifact — they are eigenvalues, and they would appear among the
    // reported frequencies.
    //
    // The full rule is also EXACT here rather than merely safe. On an affine element the
    // stiffness integrand is degree 4 at most per reference direction and NiNj is degree 4,
    // while the three-point rule is exact to degree 5 — so no quadrature error enters the
    // patch test. And every weight is POSITIVE, which makes the quadrature mass a Gram
    // matrix: SPD on any positive-volume element, the same argument that chose the 14-point
    // Keast rule for TET10 over a compact degree-4 rule with a negative weight.
    private const double GaussNode = 0.7745966692414834;   // sqrt(3/5)
    private const double WeightCentre = 8.0 / 9.0;
    private const double WeightOuter = 5.0 / 9.0;

    /// <summary>Reference corner coordinates, in the canonical <see cref="Hex8"/> order.</summary>
    private static readonly int[] Sx = { -1, 1, 1, -1, -1, 1, 1, -1 };
    private static readonly int[] Sy = { -1, -1, 1, 1, -1, -1, 1, 1 };
    private static readonly int[] Sz = { -1, -1, -1, -1, 1, 1, 1, 1 };

    /// <summary>The twelve edges, in the field order of <see cref="Hex20Mid"/>.</summary>
    private static readonly (int A, int B)[] Edges =
    {
        (0, 1), (1, 2), (2, 3), (3, 0),
        (4, 5), (5, 6), (6, 7), (7, 4),
        (0, 4), (1, 5), (2, 6), (3, 7)
    };

    /// <summary>One quadrature point: reference coordinates, weight, and the shape function
    /// values and reference gradients there — computed ONCE for the process, since they do
    /// not depend on any element's geometry.</summary>
    private sealed record QuadraturePoint(double Weight, double[] N, Vector3D[] DN);

    private static readonly QuadraturePoint[] Quadrature = BuildQuadrature();

    private static QuadraturePoint[] BuildQuadrature()
    {
        double[] nodes = { -GaussNode, 0.0, GaussNode };
        double[] weights = { WeightOuter, WeightCentre, WeightOuter };

        var points = new List<QuadraturePoint>(27);
        for (int k = 0; k < 3; k++)
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++)
                {
                    var n = new double[20];
                    var dn = new Vector3D[20];
                    ShapeFunctions(nodes[i], nodes[j], nodes[k], n, dn);
                    points.Add(new QuadraturePoint(weights[i] * weights[j] * weights[k], n, dn));
                }
        return points.ToArray();
    }

    /// <summary>
    /// The 20 serendipity shape functions and their REFERENCE gradients at (xi, eta, zeta),
    /// ordered like <see cref="FeMesh.GetElementNodes"/>: 8 corners, then the 12 edge mids.
    /// Corner: N = (1/8)(1+xi*xi_i)(1+eta*eta_i)(1+zeta*zeta_i)(xi*xi_i + eta*eta_i + zeta*zeta_i - 2).
    /// Edge mid, for the direction the edge runs in: N = (1/4)(1 - t^2) times the two
    /// remaining (1 + s*s_i) factors.
    /// </summary>
    private static void ShapeFunctions(double xi, double eta, double zeta, double[] n, Vector3D[] dn)
    {
        for (int i = 0; i < 8; i++)
        {
            double a = 1 + Sx[i] * xi, b = 1 + Sy[i] * eta, c = 1 + Sz[i] * zeta;
            double sum = Sx[i] * xi + Sy[i] * eta + Sz[i] * zeta - 2;
            n[i] = 0.125 * a * b * c * sum;
            dn[i] = new Vector3D(
                0.125 * Sx[i] * b * c * (sum + a),
                0.125 * Sy[i] * a * c * (sum + b),
                0.125 * Sz[i] * a * b * (sum + c));
        }

        for (int e = 0; e < 12; e++)
        {
            var (p, q) = Edges[e];
            int i = 8 + e;
            // The edge runs along the axis whose corner signs differ between its endpoints.
            if (Sx[p] != Sx[q])
            {
                double b = 1 + Sy[p] * eta, c = 1 + Sz[p] * zeta;
                n[i] = 0.25 * (1 - xi * xi) * b * c;
                dn[i] = new Vector3D(
                    0.25 * (-2 * xi) * b * c,
                    0.25 * (1 - xi * xi) * Sy[p] * c,
                    0.25 * (1 - xi * xi) * b * Sz[p]);
            }
            else if (Sy[p] != Sy[q])
            {
                double a = 1 + Sx[p] * xi, c = 1 + Sz[p] * zeta;
                n[i] = 0.25 * (1 - eta * eta) * a * c;
                dn[i] = new Vector3D(
                    0.25 * (1 - eta * eta) * Sx[p] * c,
                    0.25 * (-2 * eta) * a * c,
                    0.25 * (1 - eta * eta) * a * Sz[p]);
            }
            else
            {
                double a = 1 + Sx[p] * xi, b = 1 + Sy[p] * eta;
                n[i] = 0.25 * (1 - zeta * zeta) * a * b;
                dn[i] = new Vector3D(
                    0.25 * (1 - zeta * zeta) * Sx[p] * b,
                    0.25 * (1 - zeta * zeta) * a * Sy[p],
                    0.25 * (-2 * zeta) * a * b);
            }
        }
    }

    /// <summary>
    /// The identity of an element's SHAPE, for reuse of its computed matrices.
    /// <para>
    /// A mapped lattice is thousands of congruent bricks: every one of them has the same
    /// Jacobian at every quadrature point, so every one of them has the same 60x60 stiffness
    /// and mass. Computing them once and stamping is not an approximation — the key is the
    /// element's own corner offsets compared BIT FOR BIT (via
    /// <see cref="BitConverter.DoubleToInt64Bits(double)"/>), so two elements share a key only
    /// when the general path would produce identical doubles for them anyway. Elements that do
    /// not match a translated copy of an earlier one simply get their own entry.
    /// </para>
    /// </summary>
    private readonly record struct ElementKey(long A, long B, long C);

    private sealed record ElementMatrices(double[] Stiffness, double[] Mass, Matrix3[] InverseJ,
        double[] DetJ);

    /// <summary>A 3x3 matrix, only ever used here as an inverse Jacobian.</summary>
    private readonly struct Matrix3
    {
        public readonly Vector3D Row0, Row1, Row2;
        public Matrix3(Vector3D r0, Vector3D r1, Vector3D r2) { Row0 = r0; Row1 = r1; Row2 = r2; }

        /// <summary>Maps a reference gradient to a physical one: g = J^-T * gRef, which with
        /// the rows stored as above is the transpose product written out.</summary>
        public Vector3D TransposeTimes(Vector3D v) => new(
            Row0.X * v.X + Row1.X * v.Y + Row2.X * v.Z,
            Row0.Y * v.X + Row1.Y * v.Y + Row2.Y * v.Z,
            Row0.Z * v.X + Row1.Z * v.Y + Row2.Z * v.Z);
    }

    private readonly FeMesh _mesh;
    private readonly double _lambda;
    private readonly double _mu;
    private readonly double _density;
    private readonly Dictionary<ElementKey, ElementMatrices> _byShape = new();

    /// <summary>
    /// Test hook: skip the congruence cache and compute every element from scratch. The two
    /// paths must agree BITWISE, which is what the cache's correctness rests on.
    /// </summary>
    internal bool ForceGeneralPath { get; init; }

    /// <summary>
    /// How many distinct element shapes the cache ended up holding — the measurement that
    /// says whether it is doing anything. On a mapped lattice this should be a handful
    /// against thousands of elements.
    /// </summary>
    internal int DistinctShapeCount => _byShape.Count;

    public Hex20Assembler(FeMesh mesh, Material material)
    {
        if (!mesh.IsHex)
            throw new InvalidOperationException("Hex20Assembler requires a hexahedral (HEX20) mesh.");
        material.ValidateMechanical();
        _mesh = mesh;
        double e = material.YoungsModulus;
        double nu = material.PoissonRatio;
        _lambda = e * nu / ((1 + nu) * (1 - 2 * nu));
        _mu = e / (2 * (1 + nu));
        _density = material.Density;
    }

    public int DofCount => _mesh.NodeCount * 3;

    /// <summary>
    /// The element's matrices, from the cache when an earlier element had bitwise the same
    /// shape. The key is built from the corner offsets relative to node 0 and the mid-node
    /// offsets, so a translated copy hits and anything else misses.
    /// </summary>
    private ElementMatrices MatricesFor(int element)
    {
        var nodes = _mesh.GetElementNodes(element);
        if (ForceGeneralPath) return Compute(nodes);

        var key = ShapeKey(nodes);
        if (key is { } k && _byShape.TryGetValue(k, out var cached)) return cached;

        var computed = Compute(nodes);
        if (key is { } kk) _byShape[kk] = computed;
        return computed;
    }

    /// <summary>
    /// A shape key when the element is a translate of a canonical brick, otherwise null.
    /// The check is deliberately strict — bitwise equality of every node offset against the
    /// axis-aligned pattern the lattice mesher emits — so a hit guarantees the general path
    /// would compute the very same numbers.
    /// </summary>
    private ElementKey? ShapeKey(int[] nodes)
    {
        var origin = _mesh.Nodes[nodes[0]];
        double dx = _mesh.Nodes[nodes[1]].X - origin.X;
        double dy = _mesh.Nodes[nodes[3]].Y - origin.Y;
        double dz = _mesh.Nodes[nodes[4]].Z - origin.Z;

        for (int i = 0; i < 20; i++)
        {
            var p = _mesh.Nodes[nodes[i]] - origin;
            // Reference coordinates of node i, in [-1, 1], mapped onto [0, 1] fractions.
            double fx, fy, fz;
            if (i < 8) { fx = 0.5 * (1 + Sx[i]); fy = 0.5 * (1 + Sy[i]); fz = 0.5 * (1 + Sz[i]); }
            else
            {
                var (a, b) = Edges[i - 8];
                fx = 0.25 * (2 + Sx[a] + Sx[b]);
                fy = 0.25 * (2 + Sy[a] + Sy[b]);
                fz = 0.25 * (2 + Sz[a] + Sz[b]);
            }
            if (p.X != fx * dx || p.Y != fy * dy || p.Z != fz * dz) return null;
        }

        return new ElementKey(
            BitConverter.DoubleToInt64Bits(dx),
            BitConverter.DoubleToInt64Bits(dy),
            BitConverter.DoubleToInt64Bits(dz));
    }

    /// <summary>
    /// The isoparametric element matrices: at each of the 27 points, build J from all 20
    /// nodes, invert it, map the reference gradients, and accumulate the same isotropic 3x3
    /// blocks the tetrahedral assemblers use.
    /// </summary>
    private ElementMatrices Compute(int[] nodes)
    {
        var stiffness = new double[60 * 60];
        var mass = new double[20 * 20];
        var inverse = new Matrix3[Quadrature.Length];
        var detJ = new double[Quadrature.Length];
        var g = new Vector3D[20];

        for (int q = 0; q < Quadrature.Length; q++)
        {
            var point = Quadrature[q];

            // Coordinates RELATIVE to the element's first node. The Jacobian is
            // translation-invariant (the shape gradients sum to zero), so this changes no
            // mathematics — but it makes the arithmetic depend only on the element's SHAPE,
            // not on where it sits. That is what lets two congruent elements share a computed
            // matrix bitwise, and it avoids cancelling large absolute coordinates besides.
            var origin = _mesh.Nodes[nodes[0]];
            Vector3D dXi = default, dEta = default, dZeta = default;
            for (int i = 0; i < 20; i++)
            {
                var p = _mesh.Nodes[nodes[i]] - origin;
                dXi += p * point.DN[i].X;
                dEta += p * point.DN[i].Y;
                dZeta += p * point.DN[i].Z;
            }

            double det = Vector3D.Dot(dXi, Vector3D.Cross(dEta, dZeta));
            if (det <= 0)
                throw new InvalidOperationException(
                    $"A hexahedral element has a non-positive Jacobian determinant ({det:g4}) at " +
                    "one of its integration points: the element is inverted or badly distorted. " +
                    "Reduce the division counts or check the geometry.");

            // J^-1 by the adjugate, rows being the gradients of (xi, eta, zeta) in space.
            double inv = 1.0 / det;
            var invJ = new Matrix3(
                Vector3D.Cross(dEta, dZeta) * inv,
                Vector3D.Cross(dZeta, dXi) * inv,
                Vector3D.Cross(dXi, dEta) * inv);
            inverse[q] = invJ;
            detJ[q] = det;

            for (int i = 0; i < 20; i++)
                g[i] = invJ.TransposeTimes(point.DN[i]);

            double w = point.Weight * det;
            for (int i = 0; i < 20; i++)
            {
                for (int j = 0; j < 20; j++)
                {
                    // K_ij += w*(lambda*g_i (x) g_j + mu*g_j (x) g_i + mu*(g_i . g_j)*I)
                    var gi = g[i];
                    var gj = g[j];
                    double dot = Vector3D.Dot(gi, gj);
                    for (int a = 0; a < 3; a++)
                        for (int b = 0; b < 3; b++)
                        {
                            double value = w * (_lambda * gi[a] * gj[b] + _mu * gj[a] * gi[b]);
                            if (a == b) value += w * _mu * dot;
                            stiffness[(i * 3 + a) * 60 + (j * 3 + b)] += value;
                        }

                    mass[i * 20 + j] += w * _density * point.N[i] * point.N[j];
                }
            }
        }

        return new ElementMatrices(stiffness, mass, inverse, detJ);
    }

    /// <summary>
    /// The non-zero positions of both matrices, derived once. A modal solve assembles the
    /// stiffness AND the mass over the same connectivity, so discovering the pattern twice is
    /// discovering the same answer twice — and on a large mesh that is a hash map and a sort
    /// per row, paid for again.
    /// </summary>
    private SparsityPattern Pattern => _pattern ??= BuildPattern();

    private SparsityPattern? _pattern;

    private SparsityPattern BuildPattern()
    {
        var blocks = new List<int[]>(_mesh.ElementCount);
        for (int el = 0; el < _mesh.ElementCount; el++)
        {
            var nodes = _mesh.GetElementNodes(el);
            var dofs = new int[nodes.Length * 3];
            for (int i = 0; i < nodes.Length; i++)
                for (int a = 0; a < 3; a++)
                    dofs[i * 3 + a] = nodes[i] * 3 + a;
            blocks.Add(dofs);
        }
        return SparsityPattern.FromBlocks(DofCount, DofCount, blocks);
    }

    public CsrMatrix AssembleStiffness(CancellationToken cancellationToken = default)
    {
        var pattern = Pattern;
        var values = pattern.CreateValues();
        for (int el = 0; el < _mesh.ElementCount; el++)
        {
            if ((el & 255) == 0) cancellationToken.ThrowIfCancellationRequested();

            var nodes = _mesh.GetElementNodes(el);
            var ke = MatricesFor(el).Stiffness;
            for (int i = 0; i < 20; i++)
                for (int j = 0; j < 20; j++)
                    for (int a = 0; a < 3; a++)
                        for (int b = 0; b < 3; b++)
                            pattern.Add(values, nodes[i] * 3 + a, nodes[j] * 3 + b,
                                ke[(i * 3 + a) * 60 + (j * 3 + b)]);
        }
        return pattern.ToMatrix(values);
    }

    public CsrMatrix AssembleMass(CancellationToken cancellationToken = default)
    {
        var pattern = Pattern;
        var values = pattern.CreateValues();
        for (int el = 0; el < _mesh.ElementCount; el++)
        {
            if ((el & 255) == 0) cancellationToken.ThrowIfCancellationRequested();

            var nodes = _mesh.GetElementNodes(el);
            var me = MatricesFor(el).Mass;
            for (int i = 0; i < 20; i++)
                for (int j = 0; j < 20; j++)
                {
                    double value = me[i * 20 + j];
                    for (int a = 0; a < 3; a++)
                        pattern.Add(values, nodes[i] * 3 + a, nodes[j] * 3 + a, value);
                }
        }
        return pattern.ToMatrix(values);
    }

    /// <summary>
    /// Element strain: the symmetric displacement gradient, averaged over the 27 quadrature
    /// points with their own weights and |J|.
    /// <para>
    /// Strain varies QUADRATICALLY over a HEX20, so unlike TET10 the average and the centroid
    /// value are different numbers. The volume-weighted average is the defensible element
    /// tensor — it is the mean of the field over the element, which is what the nodal
    /// averaging downstream then treats it as.
    /// </para>
    /// </summary>
    public SymmetricTensor ElementStrain(int element, ReadOnlySpan<double> displacements)
    {
        var nodes = _mesh.GetElementNodes(element);
        var matrices = MatricesFor(element);
        var g = new Vector3D[20];

        double dxx = 0, dyy = 0, dzz = 0, dxy = 0, dyz = 0, dzx = 0, total = 0;
        for (int q = 0; q < Quadrature.Length; q++)
        {
            var point = Quadrature[q];
            var invJ = matrices.InverseJ[q];
            for (int i = 0; i < 20; i++)
                g[i] = invJ.TransposeTimes(point.DN[i]);

            double w = point.Weight * matrices.DetJ[q];
            total += w;

            double exx = 0, eyy = 0, ezz = 0, exy = 0, eyz = 0, ezx = 0;
            for (int i = 0; i < 20; i++)
            {
                double ux = displacements[nodes[i] * 3];
                double uy = displacements[nodes[i] * 3 + 1];
                double uz = displacements[nodes[i] * 3 + 2];
                var gi = g[i];
                exx += ux * gi.X;
                eyy += uy * gi.Y;
                ezz += uz * gi.Z;
                exy += 0.5 * (ux * gi.Y + uy * gi.X);
                eyz += 0.5 * (uy * gi.Z + uz * gi.Y);
                ezx += 0.5 * (uz * gi.X + ux * gi.Z);
            }

            dxx += w * exx; dyy += w * eyy; dzz += w * ezz;
            dxy += w * exy; dyz += w * eyz; dzx += w * ezx;
        }

        double inv = 1.0 / total;
        return new SymmetricTensor(dxx * inv, dyy * inv, dzz * inv, dxy * inv, dyz * inv, dzx * inv);
    }

    public SymmetricTensor ElementStress(int element, ReadOnlySpan<double> displacements) =>
        StressFromStrain(ElementStrain(element, displacements));

    /// <inheritdoc/>
    public SymmetricTensor StressFromStrain(SymmetricTensor eps)
    {
        double trace = eps.XX + eps.YY + eps.ZZ;
        return new SymmetricTensor(
            _lambda * trace + 2 * _mu * eps.XX,
            _lambda * trace + 2 * _mu * eps.YY,
            _lambda * trace + 2 * _mu * eps.ZZ,
            2 * _mu * eps.XY,
            2 * _mu * eps.YZ,
            2 * _mu * eps.ZX);
    }
}
