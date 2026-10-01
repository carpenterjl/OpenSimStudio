using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// The AC sweep reduces its system ONCE and re-values it per frequency, instead of rebuilding
/// the free-DOF map and both real assemblies at every point. This gates the claim: the reduced
/// system and its right-hand side are BITWISE what the per-point reduction produced.
///
/// <see cref="ComplexConstrainedSystemSolver"/> is retained purely as the oracle here. It has
/// no callers left in the solvers, and deleting it would leave the equivalence claim with
/// nothing to be checked against.
/// </summary>
public class AcSweepReductionTests
{
    private static readonly Material Copper = new()
    {
        Name = "Copper",
        YoungsModulus = 110e9,
        PoissonRatio = 0.34,
        Density = 8960,
        ElectricalConductivity = 5.8e7,
        RelativePermittivity = 1.0
    };

    private static FeMesh Block() =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(0.02, 0.01, 0.005),
            new MeshSettings { TargetEdgeLength = 0.0025 });

    private static (CsrMatrix Conductance, CsrMatrix Capacitance) Assemblies(FeMesh mesh)
    {
        var sigma = new ScalarDiffusionAssembler(mesh, _ => Copper.ElectricalConductivity!.Value);
        var eps = new ScalarDiffusionAssembler(mesh,
            _ => 8.854187817e-12 * Copper.RelativePermittivity!.Value);
        return (sigma.AssembleStiffness(), eps.AssembleStiffness());
    }

    private static Dictionary<int, Complex> Electrodes(FeMesh mesh)
    {
        var prescribed = new Dictionary<int, Complex>();
        foreach (int node in mesh.GetFaceNodes(new[] { 0 })) prescribed[node] = Complex.Zero;
        foreach (int node in mesh.GetFaceNodes(new[] { 1 })) prescribed[node] = new Complex(1.0, 0.25);
        return prescribed;
    }

    /// <summary>
    /// One reduction, several frequencies: the matrix entries and the right-hand side must
    /// match the per-point reduction to the last bit at EVERY point, not just the first — a
    /// value left over from a previous frequency would pass a single-point check.
    /// </summary>
    [Fact]
    public void TheOnceReducedSweep_IsBitwiseThePerPointReduction()
    {
        var mesh = Block();
        var (conductance, capacitance) = Assemblies(mesh);
        var prescribed = Electrodes(mesh);

        var loads = new Complex[mesh.NodeCount];
        for (int i = 0; i < loads.Length; i++)
            loads[i] = new Complex((i % 5) - 2, (i % 3) - 1);

        var reduced = ComplexReducedSystem.Reduce(conductance, capacitance, prescribed);

        foreach (double frequency in new[] { 0.0, 1e3, 2.5e6, 1e9 })
        {
            double omega = 2 * Math.PI * frequency;

            var actualMatrix = reduced.MatrixAt(omega);
            var actualRhs = reduced.LoadsAt(omega, loads);

            var (expectedMatrix, expectedRhs) = PerPointReduction(
                ComplexCsrMatrix.Combine(conductance, capacitance, omega), loads, prescribed);

            Assert.Equal(expectedMatrix.RowCount, actualMatrix.RowCount);
            Assert.Equal(expectedMatrix.RowPointers, actualMatrix.RowPointers);
            Assert.Equal(expectedMatrix.ColumnIndices, actualMatrix.ColumnIndices);
            for (int k = 0; k < expectedMatrix.Values.Length; k++)
            {
                Assert.Equal(expectedMatrix.Values[k].Real, actualMatrix.Values[k].Real);
                Assert.Equal(expectedMatrix.Values[k].Imaginary, actualMatrix.Values[k].Imaginary);
            }

            Assert.Equal(expectedRhs.Length, actualRhs.Length);
            for (int i = 0; i < expectedRhs.Length; i++)
            {
                Assert.Equal(expectedRhs[i].Real, actualRhs[i].Real);
                Assert.Equal(expectedRhs[i].Imaginary, actualRhs[i].Imaginary);
            }
        }
    }

    /// <summary>Restrict and Expand round-trip through the free-DOF map, prescribed phasors
    /// reinstated — the warm start between sweep points depends on it.</summary>
    [Fact]
    public void RestrictAndExpand_RoundTripThroughTheFreeDofMap()
    {
        var mesh = Block();
        var (conductance, capacitance) = Assemblies(mesh);
        var prescribed = Electrodes(mesh);
        var reduced = ComplexReducedSystem.Reduce(conductance, capacitance, prescribed);

        var full = new Complex[mesh.NodeCount];
        for (int i = 0; i < full.Length; i++) full[i] = new Complex(i * 0.5, -i * 0.25);
        foreach (var (node, value) in prescribed) full[node] = value;

        var round = reduced.Expand(reduced.Restrict(full));

        Assert.Equal(full.Length, round.Length);
        for (int i = 0; i < full.Length; i++)
        {
            Assert.Equal(full[i].Real, round[i].Real);
            Assert.Equal(full[i].Imaginary, round[i].Imaginary);
        }

        // A null warm start restricts to zeros rather than throwing — the first sweep point.
        Assert.All(reduced.Restrict(null), v => Assert.Equal(Complex.Zero, v));
    }

    [Fact]
    public void ASystemWithNoPrescribedPotentials_IsRefused()
    {
        var mesh = Block();
        var (conductance, capacitance) = Assemblies(mesh);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ComplexReducedSystem.Reduce(conductance, capacitance,
                new Dictionary<int, Complex>()));
        Assert.Contains("reference", ex.Message);
    }

    /// <summary>
    /// The reduction that the sweep no longer performs per point, kept here so the equivalence
    /// above is checked against a real implementation rather than against itself.
    /// </summary>
    private static (ComplexCsrMatrix Matrix, Complex[] Rhs) PerPointReduction(
        ComplexCsrMatrix matrix, Complex[] loads, IReadOnlyDictionary<int, Complex> prescribed)
    {
        int n = matrix.RowCount;
        var freeIndex = new int[n];
        int freeCount = 0;
        for (int i = 0; i < n; i++)
            freeIndex[i] = prescribed.ContainsKey(i) ? -1 : freeCount++;

        var reBuilder = new SparseMatrixBuilder(freeCount, freeCount);
        var imBuilder = new SparseMatrixBuilder(freeCount, freeCount);
        var rhs = new Complex[freeCount];
        for (int row = 0; row < n; row++)
        {
            int r = freeIndex[row];
            if (r < 0) continue;
            rhs[r] = loads[row];
            for (int k = matrix.RowPointers[row]; k < matrix.RowPointers[row + 1]; k++)
            {
                int col = matrix.ColumnIndices[k];
                int c = freeIndex[col];
                if (c >= 0)
                {
                    reBuilder.Add(r, c, matrix.Values[k].Real);
                    imBuilder.Add(r, c, matrix.Values[k].Imaginary);
                }
                else
                {
                    rhs[r] -= matrix.Values[k] * prescribed[col];
                }
            }
        }
        return (ComplexCsrMatrix.Combine(reBuilder.Build(), imBuilder.Build(), 1.0), rhs);
    }
}
