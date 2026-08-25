using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// A/B gates for the changes that removed repeated computation from the solvers and the
/// mesher. Every one of them claims the SAME arithmetic in the SAME order with a redundant
/// evaluation elided, so each is gated as a bitwise identity against the path it replaced —
/// not as a tolerance. A shift here is a real behaviour change, never a rounding excuse.
/// </summary>
public class RedundantComputationTests
{
    private static readonly Material Steel = new()
    {
        Name = "Structural steel",
        YoungsModulus = 200e9,
        PoissonRatio = 0.30,
        Density = 7850
    };

    private static FeMesh MeshBox(double h, ElementOrder order) =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(0.10, 0.04, 0.02),
            new MeshSettings { TargetEdgeLength = h, ElementOrder = order });

    /// <summary>A deterministic displacement field — the recovery path does not care whether
    /// it solves anything, only that both stress routes read the same numbers.</summary>
    private static double[] SyntheticDisplacements(FeMesh mesh)
    {
        var u = new double[mesh.NodeCount * 3];
        for (int i = 0; i < mesh.NodeCount; i++)
        {
            var p = mesh.Nodes[i];
            u[i * 3] = 1e-4 * p.X + 3e-5 * p.Y - 2e-5 * p.Z;
            u[i * 3 + 1] = -5e-5 * p.X + 7e-5 * p.Y + 1e-5 * p.Z;
            u[i * 3 + 2] = 2e-5 * p.X - 4e-5 * p.Y + 9e-5 * p.Z;
        }
        return u;
    }

    private static void AssertSame(SymmetricTensor expected, SymmetricTensor actual)
    {
        Assert.Equal(expected.XX, actual.XX);
        Assert.Equal(expected.YY, actual.YY);
        Assert.Equal(expected.ZZ, actual.ZZ);
        Assert.Equal(expected.XY, actual.XY);
        Assert.Equal(expected.YZ, actual.YZ);
        Assert.Equal(expected.ZX, actual.ZX);
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void StressFromStrain_IsBitwiseTheTwoCallRecoveryItReplaced(ElementOrder order)
    {
        var mesh = MeshBox(0.012, order);
        var assembler = new AssemblerProbe(mesh, Steel, order);
        var u = SyntheticDisplacements(mesh);

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            // The old path: stress and strain each derived from the displacements.
            var oldStress = assembler.ElementStress(e, u);
            var oldStrain = assembler.ElementStrain(e, u);
            // The new path: strain once, stress from it.
            var newStrain = assembler.ElementStrain(e, u);
            var newStress = assembler.StressFromStrain(newStrain);

            AssertSame(oldStrain, newStrain);
            AssertSame(oldStress, newStress);
        }
    }

    /// <summary>
    /// The preconditioner overload must be the self-building one to the last bit: same
    /// solution, same iteration count, same residual. Anything else would mean the CG saw a
    /// different preconditioner, which is the only way this change could alter a result.
    /// </summary>
    [Fact]
    public void PassedPreconditioner_SolvesBitwiseIdenticallyToTheSelfBuiltOne()
    {
        var mesh = MeshBox(0.010, ElementOrder.Linear);
        var stiffness = new Tet4Assembler(mesh, Steel).AssembleStiffness();
        var prescribed = new Dictionary<int, double>();
        foreach (int node in mesh.GetFaceNodes(new[] { 0 }))
            for (int a = 0; a < 3; a++)
                prescribed[node * 3 + a] = 0;
        var reduced = ConstrainedSystemSolver.Reduce(stiffness, prescribed);

        var loads = new double[stiffness.RowCount];
        for (int i = 0; i < loads.Length; i++) loads[i] = ((i % 7) - 3) * 1e3;
        var rhs = reduced.ReduceLoads(loads);

        var cg = new ConjugateGradientSolver { Tolerance = 1e-10 };
        var xSelfBuilt = new double[reduced.FreeCount];
        var selfBuilt = cg.Solve(reduced.Reduced, rhs, xSelfBuilt);

        var invDiag = ConjugateGradientSolver.BuildJacobiPreconditioner(reduced.Reduced);
        var xPassed = new double[reduced.FreeCount];
        var passed = cg.Solve(reduced.Reduced, rhs, xPassed, invDiag);

        Assert.Equal(selfBuilt.Iterations, passed.Iterations);
        Assert.Equal(selfBuilt.Converged, passed.Converged);
        Assert.Equal(selfBuilt.ResidualNorm, passed.ResidualNorm);
        for (int i = 0; i < xSelfBuilt.Length; i++)
            Assert.Equal(xSelfBuilt[i], xPassed[i]);
    }

    [Fact]
    public void PreconditionerOfASingularSystem_FailsNamingTheRow()
    {
        var builder = new SparseMatrixBuilder(3, 3);
        builder.Add(0, 0, 2.0);
        builder.Add(2, 2, 5.0);            // row 1 has no diagonal entry at all
        var matrix = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConjugateGradientSolver.BuildJacobiPreconditioner(matrix));
        Assert.Contains("row 1", ex.Message);
    }

    [Fact]
    public void PreconditionerLengthMismatch_IsRefused()
    {
        var builder = new SparseMatrixBuilder(2, 2);
        builder.Add(0, 0, 1.0);
        builder.Add(1, 1, 1.0);
        var matrix = builder.Build();
        var cg = new ConjugateGradientSolver();

        Assert.Throws<ArgumentException>(() =>
            cg.Solve(matrix, new double[2], new double[2], new double[3]));
    }

    /// <summary>
    /// The two face-use passes in the mesher became one. Which faces end up on the skin, and
    /// the ORDER they are emitted in, must be unchanged: boundary-triangle order is what
    /// assigns the geometric edge ids every stored scope is written against.
    /// </summary>
    [Fact]
    public void SingleFaceUsePass_ProducesTheSameSkinInTheSameOrder()
    {
        var mesh = new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(0.10, 0.04, 0.02),
            new MeshSettings { TargetEdgeLength = 0.011 });

        // Independent oracle: rebuild the skin from the finished mesh the way the retired
        // counting pass did — four faces per element in element order, keep the once-used.
        var expected = ReferenceSkin(mesh);
        Assert.Equal(expected.Count, mesh.BoundaryTriangles.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            // Winding and face tags come from geometry the oracle does not reproduce; what
            // it pins is WHICH faces are boundary, and in what order they are emitted.
            var actual = mesh.BoundaryTriangles[i];
            Assert.Equal(expected[i], Sorted(actual.A, actual.B, actual.C));
        }
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private static List<(int, int, int)> ReferenceSkin(FeMesh mesh)
    {
        var counts = new Dictionary<(int, int, int), int>();
        var order = new List<(int, int, int)>();
        void Touch(int a, int b, int c)
        {
            var key = Sorted(a, b, c);
            if (counts.TryGetValue(key, out int n)) counts[key] = n + 1;
            else { counts[key] = 1; order.Add(key); }
        }
        foreach (var e in mesh.Elements)
        {
            Touch(e.N1, e.N2, e.N3);
            Touch(e.N0, e.N2, e.N3);
            Touch(e.N0, e.N1, e.N3);
            Touch(e.N0, e.N1, e.N2);
        }
        return order.Where(f => counts[f] == 1).ToList();
    }

    /// <summary>
    /// Bounds and FaceCount are now derived once per mesh instead of on every read. Same
    /// numbers as a fresh computation, and stable across reads.
    /// </summary>
    [Fact]
    public void CachedGeometryProperties_MatchAFreshComputation()
    {
        var geometry = PrimitiveFactory.CreateBox(0.10, 0.04, 0.02);

        var expected = Aabb.FromPoints(geometry.Vertices);
        var bounds = geometry.Bounds;
        Assert.Equal(expected.Min.X, bounds.Min.X);
        Assert.Equal(expected.Min.Y, bounds.Min.Y);
        Assert.Equal(expected.Min.Z, bounds.Min.Z);
        Assert.Equal(expected.Max.X, bounds.Max.X);
        Assert.Equal(expected.Max.Y, bounds.Max.Y);
        Assert.Equal(expected.Max.Z, bounds.Max.Z);

        Assert.Equal(geometry.TriangleFaceIds.Max() + 1, geometry.FaceCount);
        Assert.Equal(bounds.Min.X, geometry.Bounds.Min.X);   // second read is the cached one
    }
}
