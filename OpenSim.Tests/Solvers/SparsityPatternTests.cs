using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// The elasticity assemblers derive their non-zero positions once and fill a values array,
/// instead of rediscovering them with a hash map and a sort per row on every assembly. This
/// gates the claim: the matrices are BITWISE the ones the builder produced.
///
/// The builder is not going anywhere — it is the right tool when the pattern genuinely is not
/// known in advance — so it serves as the oracle here rather than being kept alive for the
/// test's sake.
/// </summary>
public class SparsityPatternTests
{
    private static readonly Material Steel = new()
    {
        Name = "Structural steel",
        YoungsModulus = 200e9,
        PoissonRatio = 0.30,
        Density = 7850
    };

    // ------------------------------------------------------------------ the pattern itself

    [Fact]
    public void ABlockCouplesEveryDofToEveryOther_WithAscendingColumns()
    {
        var pattern = SparsityPattern.FromBlocks(5, 5, new[]
        {
            new[] { 4, 1, 0 },
            new[] { 2, 3 }
        });

        Assert.Equal(5, pattern.RowCount);
        Assert.Equal(3 * 3 + 2 * 2, pattern.NonZeroCount);

        for (int row = 0; row < pattern.RowCount; row++)
            for (int k = pattern.RowPointers[row]; k < pattern.RowPointers[row + 1] - 1; k++)
                Assert.True(pattern.ColumnIndices[k] < pattern.ColumnIndices[k + 1],
                    $"row {row} has unsorted or duplicated columns");

        // Row 0 belongs to the first block only.
        Assert.Equal(new[] { 0, 1, 4 },
            pattern.ColumnIndices[pattern.RowPointers[0]..pattern.RowPointers[1]]);
        // Row 2 belongs to the second.
        Assert.Equal(new[] { 2, 3 },
            pattern.ColumnIndices[pattern.RowPointers[2]..pattern.RowPointers[3]]);
    }

    [Fact]
    public void OverlappingBlocks_UnionTheirCouplings()
    {
        var pattern = SparsityPattern.FromBlocks(3, 3, new[]
        {
            new[] { 0, 1 },
            new[] { 1, 2 }
        });

        // Row 1 sees both blocks; rows 0 and 2 see one each.
        Assert.Equal(new[] { 0, 1, 2 },
            pattern.ColumnIndices[pattern.RowPointers[1]..pattern.RowPointers[2]]);
        Assert.Equal(2 + 3 + 2, pattern.NonZeroCount);
    }

    /// <summary>
    /// Scattering outside the pattern is a typed failure, not a silent grow. A pattern that
    /// disagreed with the assembly would otherwise drop entries and produce a matrix that is
    /// merely close to the right one.
    /// </summary>
    [Fact]
    public void ScatteringOutsideThePattern_IsRefusedByPosition()
    {
        var pattern = SparsityPattern.FromBlocks(3, 3, new[] { new[] { 0, 1 } });
        var values = pattern.CreateValues();

        pattern.Add(values, 0, 1, 1.0);          // in the pattern

        var ex = Assert.Throws<ArgumentException>(() => pattern.Add(values, 0, 2, 1.0));
        Assert.Contains("(0, 2)", ex.Message);
    }

    [Fact]
    public void AMismatchedValuesArray_IsRefused()
    {
        var pattern = SparsityPattern.FromBlocks(2, 2, new[] { new[] { 0, 1 } });
        Assert.Throws<ArgumentException>(() => pattern.ToMatrix(new double[3]));
    }

    // ------------------------------------------------------------------ the assemblers

    /// <summary>The oracle: assembly through the general-purpose builder, which discovers the
    /// pattern as it goes.</summary>
    private static CsrMatrix BuilderStiffness(FeMesh mesh, Material material)
    {
        var assembler = new Tet4Assembler(mesh, material);
        var builder = new SparseMatrixBuilder(mesh.NodeCount * 3, mesh.NodeCount * 3);
        var gradients = new Vector3D[4];

        double lambda = material.YoungsModulus * material.PoissonRatio
            / ((1 + material.PoissonRatio) * (1 - 2 * material.PoissonRatio));
        double mu = material.YoungsModulus / (2 * (1 + material.PoissonRatio));

        for (int el = 0; el < mesh.ElementCount; el++)
        {
            double volume = mesh.ElementVolume(el);
            var g = Tet4ShapeGradients.Compute(mesh, el);
            var e = mesh.Elements[el];
            Span<int> nodes = stackalloc int[] { e.N0, e.N1, e.N2, e.N3 };

            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    var gi = g[i];
                    var gj = g[j];
                    double dot = Vector3D.Dot(gi, gj);
                    for (int a = 0; a < 3; a++)
                        for (int b = 0; b < 3; b++)
                        {
                            double value = volume * (lambda * gi[a] * gj[b] + mu * gj[a] * gi[b]);
                            if (a == b) value += volume * mu * dot;
                            builder.Add(nodes[i] * 3 + a, nodes[j] * 3 + b, value);
                        }
                }
        }
        Assert.NotNull(assembler);
        return builder.Build();
    }

    [Fact]
    public void PatternedStiffness_IsBitwiseTheBuilderAssembly()
    {
        var mesh = new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(0.05, 0.03, 0.02),
            new MeshSettings { TargetEdgeLength = 0.008 });

        var expected = BuilderStiffness(mesh, Steel);
        var actual = new Tet4Assembler(mesh, Steel).AssembleStiffness();

        Assert.Equal(expected.RowCount, actual.RowCount);
        Assert.Equal(expected.NonZeroCount, actual.NonZeroCount);
        Assert.Equal(expected.RowPointers, actual.RowPointers);
        Assert.Equal(expected.ColumnIndices, actual.ColumnIndices);
        for (int k = 0; k < expected.Values.Length; k++)
            Assert.Equal(expected.Values[k], actual.Values[k]);
    }

    /// <summary>
    /// Stiffness and mass are assembled over the same connectivity, so they must come out with
    /// the SAME pattern — which is the whole reason deriving it once pays, and the property a
    /// modal solve leans on when it reduces both with one free-DOF map.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void StiffnessAndMass_ShareOnePattern(ElementOrder order)
    {
        var mesh = new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(0.05, 0.03, 0.02),
            new MeshSettings { TargetEdgeLength = 0.010, ElementOrder = order });

        var assembler = new AssemblerProbe(mesh, Steel, order);
        var stiffness = assembler.AssembleStiffness();
        var mass = assembler.AssembleMass();

        Assert.Equal(stiffness.RowPointers, mass.RowPointers);
        Assert.Equal(stiffness.ColumnIndices, mass.ColumnIndices);
        // The arrays are shared, not merely equal — one pattern, two values arrays.
        Assert.Same(stiffness.RowPointers, mass.RowPointers);
        Assert.Same(stiffness.ColumnIndices, mass.ColumnIndices);
    }

    /// <summary>The hexahedral assembler goes the same way.</summary>
    [Fact]
    public void HexStiffnessAndMass_ShareOnePattern()
    {
        var mesh = new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(0.05, 0.03, 0.02),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = ElementShape.Hexahedral,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(4, 3, 2)
            });

        var assembler = new Hex20Assembler(mesh, Steel);
        var stiffness = assembler.AssembleStiffness();
        var mass = assembler.AssembleMass();

        Assert.Same(stiffness.RowPointers, mass.RowPointers);
        Assert.Same(stiffness.ColumnIndices, mass.ColumnIndices);
    }
}
