using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using OpenSim.Solvers.Environment;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// The lagged-coefficient loop no longer rebuilds and re-reduces the whole matrix once per
/// iterate; it rewrites the entries the surface film actually touches. This gates the claim
/// that makes that safe: the rewritten system is BITWISE the one the rebuild produced.
///
/// The old path is kept here as the oracle rather than deleted, because "the same arithmetic
/// in the same order" is a claim about two implementations and needs both of them present to
/// be checked.
/// </summary>
public class FilmUpdateEquivalenceTests
{
    private static readonly Material Copper = new()
    {
        Name = "Copper",
        YoungsModulus = 110e9,
        PoissonRatio = 0.34,
        Density = 8960,
        SpecificHeat = 385,
        ThermalConductivity = 400,
        Emissivity = 0.15
    };

    private static FeMesh Block() =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(0.05, 0.03, 0.02),
            new MeshSettings { TargetEdgeLength = 0.008 });

    /// <summary>A film with a different coefficient on every triangle, some of them NaN
    /// (unwetted) and some zero, so every branch of the update is exercised.</summary>
    private static SurfaceFilmModel Film(FeMesh mesh, double scale)
    {
        var h = new double[mesh.BoundaryTriangles.Count];
        for (int t = 0; t < h.Length; t++)
            h[t] = t % 7 == 0 ? double.NaN
                 : t % 11 == 0 ? 0.0
                 : scale * (3 + (t % 13));
        return new SurfaceFilmModel
        {
            TriangleFilmCoefficient = h,
            ReferenceTemperature = 300.0,
            Origin = "test"
        };
    }

    /// <summary>The retired path: copy every non-zero into a new builder, stamp the film on,
    /// reduce the result.</summary>
    private static ConstrainedSystemSolver.ReducedSystem RebuiltSystem(
        CsrMatrix conduction, FeMesh mesh, SurfaceFilmModel film,
        IReadOnlyDictionary<int, double> prescribed)
    {
        var builder = new SparseMatrixBuilder(conduction.RowCount, conduction.ColumnCount);
        for (int row = 0; row < conduction.RowCount; row++)
            for (int k = conduction.RowPointers[row]; k < conduction.RowPointers[row + 1]; k++)
                builder.Add(row, conduction.ColumnIndices[k], conduction.Values[k]);
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            double h = film.TriangleFilmCoefficient[t];
            if (double.IsNaN(h) || h == 0) continue;
            ScalarDiffusionAssembler.AddRobinSurface(builder, mesh, mesh.BoundaryTriangles[t], h);
        }
        return ConstrainedSystemSolver.Reduce(builder.Build(), prescribed, allowUnconstrained: true);
    }

    private static CsrMatrix Conduction(FeMesh mesh) =>
        new ScalarDiffusionAssembler(mesh, _ => Copper.ThermalConductivity!.Value)
            .AssembleStiffness();

    [Fact]
    public void TheUpdatedSystem_IsBitwiseTheRebuiltOne_WithNoPrescribedNodes()
    {
        var mesh = Block();
        var conduction = Conduction(mesh);
        var prescribed = new Dictionary<int, double>();

        var updatable = FilmUpdatableSystem.Prepare(conduction, mesh, prescribed);

        // Several different films through the SAME prepared system: this is the loop the
        // real solver runs, and a stale value left behind by an earlier iterate would show
        // up on the second one.
        foreach (double scale in new[] { 1.0, 25.0, 0.5 })
        {
            var film = Film(mesh, scale);
            updatable.Apply(film);
            AssertBitwise(RebuiltSystem(conduction, mesh, film, prescribed), updatable.System);
        }
    }

    /// <summary>
    /// With prescribed nodes the film also feeds the right-hand-side correction, because a
    /// stamp whose COLUMN is prescribed leaves the matrix and becomes a load term. That path
    /// has to match too, and a fixed temperature is the common case that exercises it.
    /// </summary>
    [Fact]
    public void TheUpdatedSystem_IsBitwiseTheRebuiltOne_WithPrescribedNodes()
    {
        var mesh = Block();
        var conduction = Conduction(mesh);

        var prescribed = new Dictionary<int, double>();
        foreach (int node in mesh.GetFaceNodes(new[] { 0 }))
            prescribed[node] = 350.0 + node % 5;      // non-uniform, so the correction is not zero

        var updatable = FilmUpdatableSystem.Prepare(conduction, mesh, prescribed);

        foreach (double scale in new[] { 2.0, 40.0 })
        {
            var film = Film(mesh, scale);
            updatable.Apply(film);

            var rebuilt = RebuiltSystem(conduction, mesh, film, prescribed);
            AssertBitwise(rebuilt, updatable.System);

            // And the correction the loads pass through must agree, which is the half of the
            // system the matrix comparison cannot see.
            var loads = new double[mesh.NodeCount];
            for (int i = 0; i < loads.Length; i++) loads[i] = (i % 9) - 4;

            var expected = rebuilt.ReduceLoads(loads);
            var actual = updatable.System.ReduceLoads(loads);
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], actual[i]);
        }
    }

    /// <summary>
    /// A film of all-NaN coefficients is the "nothing is wetted" case, and must leave the
    /// system exactly the film-free one — not merely close to it.
    /// </summary>
    [Fact]
    public void AnEmptyFilm_LeavesTheFilmFreeSystem()
    {
        var mesh = Block();
        var conduction = Conduction(mesh);
        var prescribed = new Dictionary<int, double>();

        var h = new double[mesh.BoundaryTriangles.Count];
        Array.Fill(h, double.NaN);
        var empty = new SurfaceFilmModel
        {
            TriangleFilmCoefficient = h,
            ReferenceTemperature = 300.0,
            Origin = "none"
        };

        var updatable = FilmUpdatableSystem.Prepare(conduction, mesh, prescribed);
        updatable.Apply(empty);

        AssertBitwise(RebuiltSystem(conduction, mesh, empty, prescribed), updatable.System);
    }

    private static void AssertBitwise(ConstrainedSystemSolver.ReducedSystem expected,
        ConstrainedSystemSolver.ReducedSystem actual)
    {
        Assert.Equal(expected.FreeCount, actual.FreeCount);
        Assert.Equal(expected.Reduced.RowPointers, actual.Reduced.RowPointers);
        Assert.Equal(expected.Reduced.ColumnIndices, actual.Reduced.ColumnIndices);
        Assert.Equal(expected.Reduced.Values.Length, actual.Reduced.Values.Length);
        for (int i = 0; i < expected.Reduced.Values.Length; i++)
            Assert.Equal(expected.Reduced.Values[i], actual.Reduced.Values[i]);
    }
}
