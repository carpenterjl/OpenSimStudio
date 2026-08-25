using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// The HEX20 element itself, below the solver: shape functions, quadrature, and the
/// congruence cache.
///
/// These are identities, not tolerances. Partition of unity and the Kronecker property are
/// exact statements about the shape functions; a rigid translation stores no energy in ANY
/// correct stiffness matrix; the quadrature mass of the whole element integrates to exactly
/// rho*V because the rule is exact for the degree of NiNj. The one gate that is a tolerance
/// rather than an identity says so and explains why.
/// </summary>
public class Hex20ElementTests
{
    private static readonly Material Steel = new()
    {
        Name = "Structural steel",
        YoungsModulus = 200e9,
        PoissonRatio = 0.30,
        Density = 7850
    };

    private static FeMesh Lattice(int nx, int ny, int nz,
        double lx = 0.20, double ly = 0.06, double lz = 0.02) =>
        new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(lx, ly, lz),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = ElementShape.Hexahedral,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(nx, ny, nz)
            });

    // ---------------------------------------------------------------- shape functions

    /// <summary>
    /// Serendipity shape functions reproduced here from their definition, independently of
    /// the assembler, so the tests below measure the assembler against the mathematics rather than
    /// against itself.
    /// </summary>
    private static double[] Shape(double xi, double eta, double zeta)
    {
        int[] sx = { -1, 1, 1, -1, -1, 1, 1, -1 };
        int[] sy = { -1, -1, 1, 1, -1, -1, 1, 1 };
        int[] sz = { -1, -1, -1, -1, 1, 1, 1, 1 };
        (int A, int B)[] edges =
        {
            (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6),
            (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7)
        };

        var n = new double[20];
        for (int i = 0; i < 8; i++)
            n[i] = 0.125 * (1 + sx[i] * xi) * (1 + sy[i] * eta) * (1 + sz[i] * zeta)
                         * (sx[i] * xi + sy[i] * eta + sz[i] * zeta - 2);
        for (int e = 0; e < 12; e++)
        {
            var (a, b) = edges[e];
            n[8 + e] = sx[a] != sx[b]
                ? 0.25 * (1 - xi * xi) * (1 + sy[a] * eta) * (1 + sz[a] * zeta)
                : sy[a] != sy[b]
                    ? 0.25 * (1 - eta * eta) * (1 + sx[a] * xi) * (1 + sz[a] * zeta)
                    : 0.25 * (1 - zeta * zeta) * (1 + sx[a] * xi) * (1 + sy[a] * eta);
        }
        return n;
    }

    /// <summary>Reference coordinates of the 20 nodes, canonical order.</summary>
    private static (double Xi, double Eta, double Zeta)[] NodeCoordinates()
    {
        int[] sx = { -1, 1, 1, -1, -1, 1, 1, -1 };
        int[] sy = { -1, -1, 1, 1, -1, -1, 1, 1 };
        int[] sz = { -1, -1, -1, -1, 1, 1, 1, 1 };
        (int A, int B)[] edges =
        {
            (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6),
            (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7)
        };

        var p = new (double, double, double)[20];
        for (int i = 0; i < 8; i++) p[i] = (sx[i], sy[i], sz[i]);
        for (int e = 0; e < 12; e++)
        {
            var (a, b) = edges[e];
            p[8 + e] = (0.5 * (sx[a] + sx[b]), 0.5 * (sy[a] + sy[b]), 0.5 * (sz[a] + sz[b]));
        }
        return p;
    }

    [Fact]
    public void ShapeFunctions_AreAPartitionOfUnity()
    {
        foreach (var (xi, eta, zeta) in Samples())
            Assert.Equal(1.0, Shape(xi, eta, zeta).Sum(), 14);
    }

    [Fact]
    public void ShapeFunctions_AreOneAtTheirOwnNodeAndZeroAtEveryOther()
    {
        var coordinates = NodeCoordinates();
        for (int node = 0; node < 20; node++)
        {
            var n = Shape(coordinates[node].Xi, coordinates[node].Eta, coordinates[node].Zeta);
            for (int i = 0; i < 20; i++)
                Assert.Equal(i == node ? 1.0 : 0.0, n[i], 14);
        }
    }

    /// <summary>
    /// A serendipity hexahedron reproduces any LINEAR field exactly — the completeness
    /// property the patch test rests on.
    /// </summary>
    [Fact]
    public void ShapeFunctions_ReproduceALinearFieldExactly()
    {
        var coordinates = NodeCoordinates();
        foreach (var (xi, eta, zeta) in Samples())
        {
            var n = Shape(xi, eta, zeta);
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < 20; i++)
            {
                x += n[i] * coordinates[i].Xi;
                y += n[i] * coordinates[i].Eta;
                z += n[i] * coordinates[i].Zeta;
            }
            Assert.Equal(xi, x, 13);
            Assert.Equal(eta, y, 13);
            Assert.Equal(zeta, z, 13);
        }
    }

    private static IEnumerable<(double, double, double)> Samples()
    {
        double[] t = { -1.0, -0.6, -0.2, 0.0, 0.37, 0.81, 1.0 };
        foreach (var a in t)
            foreach (var b in t)
                foreach (var c in t)
                    yield return (a, b, c);
    }

    // ---------------------------------------------------------------- element matrices

    /// <summary>
    /// A rigid translation stores no strain energy, so every row of a correct element
    /// stiffness sums to zero over each axis. This catches a wrong Jacobian, a wrong gradient
    /// mapping and a mis-scattered block all at once.
    /// </summary>
    [Fact]
    public void StiffnessRows_SumToZeroUnderRigidTranslation()
    {
        var mesh = Lattice(2, 2, 2);
        var k = new Hex20Assembler(mesh, Steel).AssembleStiffness();

        double scale = 0;
        for (int r = 0; r < k.RowCount; r++) scale = Math.Max(scale, RowAbsSum(k, r));

        for (int axis = 0; axis < 3; axis++)
        {
            var u = new double[k.RowCount];
            for (int n = 0; n < mesh.NodeCount; n++) u[n * 3 + axis] = 1.0;
            var f = new double[k.RowCount];
            k.Multiply(u, f);
            foreach (double value in f)
                Assert.True(Math.Abs(value) < 1e-9 * scale,
                    $"rigid translation along axis {axis} produced force {value:g4}");
        }
    }

    private static double RowAbsSum(CsrMatrix m, int row)
    {
        double sum = 0;
        for (int i = m.RowPointers[row]; i < m.RowPointers[row + 1]; i++) sum += Math.Abs(m.Values[i]);
        return sum;
    }

    /// <summary>
    /// The consistent mass integrates NiNj exactly (degree 4 per direction against a
    /// degree-5 rule), so summing the whole matrix gives exactly rho times the volume — three
    /// times over, once per axis.
    /// </summary>
    [Fact]
    public void TotalMass_IsExactlyDensityTimesVolume()
    {
        var mesh = Lattice(3, 2, 2);
        var m = new Hex20Assembler(mesh, Steel).AssembleMass();

        double total = 0;
        foreach (double v in m.Values) total += v;

        double expected = 3 * Steel.Density * mesh.TotalVolume();   // one copy per axis
        Assert.Equal(expected, total, 6);
        Assert.True(Math.Abs(total - expected) / expected < 1e-12,
            $"quadrature mass {total:g12} against rho*V {expected:g12}");
    }

    /// <summary>
    /// The mass matrix must be positive definite — the property that made the all-positive
    /// quadrature weights a requirement rather than a preference, because an indefinite mass
    /// is fatal to the eigensolver.
    /// </summary>
    [Fact]
    public void MassMatrix_IsPositiveDefinite()
    {
        var mesh = Lattice(2, 2, 1);
        var m = new Hex20Assembler(mesh, Steel).AssembleMass();

        var random = new Random(20250824);
        for (int trial = 0; trial < 20; trial++)
        {
            var v = new double[m.RowCount];
            for (int i = 0; i < v.Length; i++) v[i] = random.NextDouble() * 2 - 1;
            var mv = new double[m.RowCount];
            m.Multiply(v, mv);

            double quadratic = 0;
            for (int i = 0; i < v.Length; i++) quadratic += v[i] * mv[i];
            Assert.True(quadratic > 0, $"v^T M v = {quadratic:g4} is not positive");
        }
    }

    [Fact]
    public void StiffnessAndMass_AreSymmetric()
    {
        var mesh = Lattice(2, 2, 1);
        var assembler = new Hex20Assembler(mesh, Steel);
        AssertSymmetric(assembler.AssembleStiffness());
        AssertSymmetric(assembler.AssembleMass());
    }

    private static void AssertSymmetric(CsrMatrix m)
    {
        double scale = 0;
        foreach (double v in m.Values) scale = Math.Max(scale, Math.Abs(v));

        double At(int row, int column)
        {
            for (int i = m.RowPointers[row]; i < m.RowPointers[row + 1]; i++)
                if (m.ColumnIndices[i] == column) return m.Values[i];
            return 0;
        }

        for (int r = 0; r < m.RowCount; r++)
            for (int i = m.RowPointers[r]; i < m.RowPointers[r + 1]; i++)
            {
                int c = m.ColumnIndices[i];
                Assert.True(Math.Abs(m.Values[i] - At(c, r)) < 1e-9 * scale,
                    $"entry ({r},{c}) is not symmetric");
            }
    }

    // ---------------------------------------------------------------- the congruence cache

    /// <summary>
    /// THE gate the congruence cache rests on: stamping a previously computed element must
    /// produce bitwise the same global matrices as computing every element from scratch. The
    /// cache key is bitwise equality of the element's node offsets, so a hit means the general
    /// path would have produced these very doubles — and this test is what holds that claim.
    /// </summary>
    [Fact]
    public void StampedAssembly_IsBitwiseTheGeneralPath()
    {
        var mesh = Lattice(3, 4, 5);

        var stamped = new Hex20Assembler(mesh, Steel);
        var general = new Hex20Assembler(mesh, Steel) { ForceGeneralPath = true };

        AssertBitwiseEqual(general.AssembleStiffness(), stamped.AssembleStiffness());
        AssertBitwiseEqual(general.AssembleMass(), stamped.AssembleMass());

        // And so must the recovered strains, which read the cached inverse Jacobians.
        var u = new double[mesh.NodeCount * 3];
        for (int i = 0; i < mesh.NodeCount; i++)
        {
            var p = mesh.Nodes[i];
            u[i * 3] = 1e-4 * p.X + 2e-5 * p.Y;
            u[i * 3 + 1] = -3e-5 * p.X + 5e-5 * p.Z;
            u[i * 3 + 2] = 7e-5 * p.Y - 1e-5 * p.Z;
        }
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var a = general.ElementStrain(e, u);
            var b = stamped.ElementStrain(e, u);
            Assert.Equal(a.XX, b.XX);
            Assert.Equal(a.YY, b.YY);
            Assert.Equal(a.ZZ, b.ZZ);
            Assert.Equal(a.XY, b.XY);
            Assert.Equal(a.YZ, b.YZ);
            Assert.Equal(a.ZX, b.ZX);
        }
    }

    private static void AssertBitwiseEqual(CsrMatrix expected, CsrMatrix actual)
    {
        Assert.Equal(expected.RowCount, actual.RowCount);
        Assert.Equal(expected.NonZeroCount, actual.NonZeroCount);
        Assert.Equal(expected.RowPointers, actual.RowPointers);
        Assert.Equal(expected.ColumnIndices, actual.ColumnIndices);
        for (int i = 0; i < expected.Values.Length; i++)
            Assert.Equal(expected.Values[i], actual.Values[i]);
    }

    /// <summary>
    /// The general path is not merely a test fixture — it is what a non-congruent element
    /// takes. Exercised here on a genuinely distorted hexahedron, where the Jacobian varies
    /// point to point and no cache entry can apply.
    /// </summary>
    [Fact]
    public void ADistortedElement_TakesTheGeneralPathAndStillStoresNoEnergyUnderTranslation()
    {
        var corners = new List<Vector3D>
        {
            new(0.000, 0.000, 0.000), new(0.021, -0.001, 0.002),
            new(0.019, 0.032, -0.003), new(-0.002, 0.028, 0.001),
            new(0.003, 0.002, 0.047), new(0.024, 0.001, 0.051),
            new(0.020, 0.030, 0.049), new(-0.001, 0.031, 0.052)
        };
        var hexes = new List<Hex8> { new(0, 1, 2, 3, 4, 5, 6, 7) };
        var quads = new List<BoundaryQuad>
        {
            new(0, 3, 2, 1, 0), new(4, 5, 6, 7, 1), new(0, 1, 5, 4, 2),
            new(1, 2, 6, 5, 3), new(2, 3, 7, 6, 4), new(3, 0, 4, 7, 5)
        };
        var triangles = new List<BoundaryTriangle>();
        foreach (var q in quads)
        {
            triangles.Add(new BoundaryTriangle(q.A, q.B, q.C, q.FaceId));
            triangles.Add(new BoundaryTriangle(q.A, q.C, q.D, q.FaceId));
        }
        var mesh = QuadraticMeshBuilder.UpgradeHex(corners, hexes, triangles, quads);

        var k = new Hex20Assembler(mesh, Steel).AssembleStiffness();
        double scale = 0;
        for (int r = 0; r < k.RowCount; r++) scale = Math.Max(scale, RowAbsSum(k, r));

        for (int axis = 0; axis < 3; axis++)
        {
            var u = new double[k.RowCount];
            for (int n = 0; n < mesh.NodeCount; n++) u[n * 3 + axis] = 1.0;
            var f = new double[k.RowCount];
            k.Multiply(u, f);
            foreach (double value in f)
                Assert.True(Math.Abs(value) < 1e-9 * scale, $"distorted element, axis {axis}: {value:g4}");
        }

        AssertSymmetric(k);
    }

    [Fact]
    public void AnInvertedElement_IsRefusedByName()
    {
        // Swap the top face onto the bottom: the element folds through itself.
        var corners = new List<Vector3D>
        {
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
            new(0, 0, -1), new(1, 0, -1), new(1, 1, -1), new(0, 1, -1)
        };
        var hexes = new List<Hex8> { new(0, 1, 2, 3, 4, 5, 6, 7) };
        var quads = new List<BoundaryQuad>
        {
            new(0, 3, 2, 1, 0), new(4, 5, 6, 7, 1), new(0, 1, 5, 4, 2),
            new(1, 2, 6, 5, 3), new(2, 3, 7, 6, 4), new(3, 0, 4, 7, 5)
        };
        var triangles = new List<BoundaryTriangle>();
        foreach (var q in quads)
        {
            triangles.Add(new BoundaryTriangle(q.A, q.B, q.C, q.FaceId));
            triangles.Add(new BoundaryTriangle(q.A, q.C, q.D, q.FaceId));
        }
        var mesh = QuadraticMeshBuilder.UpgradeHex(corners, hexes, triangles, quads);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new Hex20Assembler(mesh, Steel).AssembleStiffness());
        Assert.Contains("Jacobian", ex.Message);
    }

    [Fact]
    public void ATetrahedralMesh_IsRefused()
    {
        var mesh = new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(0.02, 0.02, 0.02),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(2, 2, 2)
            });

        var ex = Assert.Throws<InvalidOperationException>(() => new Hex20Assembler(mesh, Steel));
        Assert.Contains("HEX20", ex.Message);
    }
}
