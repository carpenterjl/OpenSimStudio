using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// D6 (evaluation 2026-09-09, worked 2026-10-25): stress was recovered as one element mean and
/// then averaged at the nodes as a scalar, so a bending surface read (1 − 1/n) of its stress with
/// n elements through the thickness — half of it on the two-element meshes the benchmarks use.
/// Now the element's own field is evaluated AT each node, the tensors are averaged over the
/// elements sharing the node, and the invariants are taken of the averaged tensors. The gate is
/// the one the evaluation asked for: a cantilever's surface stress against M·c/I.
/// </summary>
public class StressRecoveryTests
{
    private readonly ITestOutputHelper _out;
    public StressRecoveryTests(ITestOutputHelper output) => _out = output;

    private const double L = 0.200, H = 0.060, T = 0.020, F = 1.0e4;

    private static Material Steel() => new()
    {
        Name = "Structural steel", YoungsModulus = 2.0e11, PoissonRatio = 0.30, Density = 7850
    };

    /// <summary>A cantilever on the structured lattice: fixed at the x = 0 end face (face 0), the
    /// tip force on the x = L end face (face 1), two elements through the height.</summary>
    private static (FeMesh Mesh, SolveOutput Output) Cantilever(ElementShape shape, int alongLength)
    {
        var mesh = new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(L, H, T),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = shape,
                Divisions = new LatticeDivisions(alongLength, 2, 2),
                ElementOrder = ElementOrder.Quadratic
            });
        var output = new LinearStaticSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Steel(),
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Root", FaceIds = new[] { 0 } },
                new ForceLoad { Name = "Tip", FaceIds = new[] { 1 }, TotalForce = new Vector3D(0, -F, 0) }
            }
        });
        return (mesh, output);
    }

    [Theory]
    [InlineData(ElementShape.Hexahedral, 0.02)]
    [InlineData(ElementShape.Tetrahedral, 0.02)]
    public void SurfaceStress_AtMidSpan_IsMcOverI(ElementShape shape, double band)
    {
        var (mesh, output) = Cantilever(shape, 40);
        var vonMises = (NodalScalarField)output.Fields.First(f => f.Name == "Stress (von Mises)");

        // At x = L/2 the bending moment is F·L/2; the surface fibre stress is M·c/I with c = H/2,
        // I = T·H³/12. Von Mises there is |σ_xx| to within the small transverse terms.
        double moment = F * L / 2, inertia = T * H * H * H / 12, exact = moment * (H / 2) / inertia;
        var surface = Enumerable.Range(0, mesh.NodeCount).Where(n =>
        {
            var p = mesh.Nodes[n];
            return Math.Abs(p.X - L / 2) < 1e-9 && (Math.Abs(p.Y) < 1e-9 || Math.Abs(p.Y - H) < 1e-9);
        }).ToList();
        Assert.True(surface.Count >= 6, $"the mid-span section has {surface.Count} surface nodes");
        double mean = surface.Average(n => vonMises.Values[n]);
        _out.WriteLine($"{shape}: mid-span surface von Mises {mean / 1e6:F3} MPa against M·c/I {exact / 1e6:F3} MPa ({(mean / exact - 1) * 100:+0.00} %) over {surface.Count} nodes");
        Assert.InRange(mean / exact, 1 - band, 1 + band);

        // The recovered nodal tensor carries the same surface value in its XX component (the
        // results panel's "Normal XX" and "Max principal" read it), where the element mean on the
        // same section sits at ±H/4 and reads half — the deficit the nodal recovery removes.
        var nodal = (TensorField)output.Fields.First(f => f.Name == "Stress tensor");
        Assert.Equal(FieldLocation.Node, nodal.Location);
        double nodalXx = surface.Average(n => Math.Abs(nodal.GetTensor(n).XX));
        Assert.InRange(nodalXx / exact, 1 - band, 1 + band);
        var tensor = (TensorField)output.Fields.First(f => f.Name == "Stress tensor (element mean)");
        Assert.Equal(FieldLocation.Element, tensor.Location);
        var top = Enumerable.Range(0, mesh.ElementCount).Where(e =>
        {
            var nodes = mesh.GetElementNodes(e);
            double cx = nodes.Average(n => mesh.Nodes[n].X), cy = nodes.Average(n => mesh.Nodes[n].Y);
            return cy > H / 2 && Math.Abs(cx - L / 2) < L / 40;
        }).ToList();
        double elementMean = top.Average(e => Math.Abs(tensor.Values[e].XX));
        _out.WriteLine($"{shape}: element-mean |σ_xx| beside mid-span {elementMean / 1e6:F3} MPa ({elementMean / exact:F3} of M·c/I)");
        Assert.InRange(elementMean / exact, 0.35, 0.65);
    }

    [Theory]
    [InlineData(ElementShape.Tetrahedral)]
    [InlineData(ElementShape.Hexahedral)]
    public void NodalStrains_OfAQuadraticField_AreExactAtEveryNode(ElementShape shape)
    {
        // u_x = a·x·y (quadratic, which TET10 and HEX20 carry exactly): ε_xx = a·y, ε_xy = a·x/2,
        // every other component zero. Each node's strain must be the field's value AT THAT node —
        // which pins the natural or barycentric coordinates of every node, their order against
        // GetElementNodes, and the Jacobian at each one; a uniform field could not tell them apart.
        const double a = 2e-3;
        var mesh = new StructuredLatticeMeshGenerator().Generate(PrimitiveFactory.CreateBox(0.02, 0.01, 0.01),
            new MeshSettings { Method = MeshMethod.StructuredLattice, Shape = shape, Divisions = new LatticeDivisions(4, 2, 2), ElementOrder = ElementOrder.Quadratic });
        var u = new double[mesh.NodeCount * 3];
        for (int n = 0; n < mesh.NodeCount; n++) u[n * 3] = a * mesh.Nodes[n].X * mesh.Nodes[n].Y;
        var assembler = StructuralSurfaceLoads.AssemblerFor(mesh, Steel());
        var at = new SymmetricTensor[20];
        int checkedNodes = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.GetElementNodes(e);
            assembler.NodalStrains(e, u, at);
            for (int k = 0; k < nodes.Length; k++)
            {
                var p = mesh.Nodes[nodes[k]];
                Assert.Equal(a * p.Y, at[k].XX, 12);
                Assert.Equal(a * p.X / 2, at[k].XY, 12);
                Assert.Equal(0.0, at[k].YY, 12);
                Assert.Equal(0.0, at[k].ZZ, 12);
                Assert.Equal(0.0, at[k].YZ, 12);
                Assert.Equal(0.0, at[k].ZX, 12);
                checkedNodes++;
            }
        }
        Assert.True(checkedNodes >= 160, $"{checkedNodes} node evaluations");
    }

    [Fact]
    public void NodalStrains_OfAUniformStrainField_AreTheElementStrain_OnEveryElement()
    {
        // A homogeneous stretch: every node of every element must read the same strain the
        // element mean reads, on TET4, TET10 and HEX20 alike (the recovery adds nothing to a
        // constant field; it only stops averaging a gradient away).
        foreach (var (shape, order) in new[] { (ElementShape.Tetrahedral, ElementOrder.Linear), (ElementShape.Tetrahedral, ElementOrder.Quadratic), (ElementShape.Hexahedral, ElementOrder.Quadratic) })
        {
            var mesh = new StructuredLatticeMeshGenerator().Generate(PrimitiveFactory.CreateBox(0.02, 0.01, 0.01),
                new MeshSettings { Method = MeshMethod.StructuredLattice, Shape = shape, Divisions = new LatticeDivisions(4, 2, 2), ElementOrder = order });
            var u = new double[mesh.NodeCount * 3];
            for (int n = 0; n < mesh.NodeCount; n++)
            {
                var p = mesh.Nodes[n];
                u[n * 3] = 1e-3 * p.X; u[n * 3 + 1] = -3e-4 * p.Y; u[n * 3 + 2] = -3e-4 * p.Z + 2e-4 * p.X;
            }
            var assembler = StructuralSurfaceLoads.AssemblerFor(mesh, Steel());
            var at = new SymmetricTensor[20];
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                var mean = assembler.ElementStrain(e, u);
                int count = mesh.GetElementNodes(e).Length;
                assembler.NodalStrains(e, u, at);
                for (int k = 0; k < count; k++)
                {
                    Assert.Equal(1e-3, at[k].XX, 10);
                    Assert.Equal(-3e-4, at[k].YY, 10);
                    Assert.Equal(1e-4, at[k].ZX, 10);                     // ½(∂u_z/∂x + ∂u_x/∂z) = ½·2e-4
                    Assert.Equal(mean.XX, at[k].XX, 12);
                }
            }
        }
    }
}
