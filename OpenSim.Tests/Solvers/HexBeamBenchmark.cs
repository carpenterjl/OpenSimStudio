using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// The like-for-like comparison the HEX20 element exists for.
///
/// The reference is an Ansys 2023 R1 static structural study of a 200 x 60 x 20 mm steel beam
/// fixed along the two edges where its bottom face meets each end, loaded with 500 kN on the
/// top face, meshed as 5,000 SOLID186 elements — 100 x 25 x 2 edge divisions. SOLID186 IS a
/// 20-node hexahedron, so running our own HEX20 at the reference's own divisions compares the
/// same element formulation on the same discretization; the existing tetrahedral benchmark
/// beside this one compares only the discretization.
///
/// What is deliberately NOT compared: the peak stress and peak strain. The support is fixed
/// along a mathematical LINE, a curve carries no area in a three-dimensional continuum, so the
/// solution is singular there and those peaks do not converge under refinement in any code.
/// Matching them would mean matching Ansys mesh for mesh instead of matching the physics.
/// </summary>
public class HexBeamBenchmark
{
    private readonly ITestOutputHelper _output;

    public HexBeamBenchmark(ITestOutputHelper output) => _output = output;

    private const double Length = 0.200, Height = 0.060, Thickness = 0.020;
    private const double Load500 = 5e5;
    private const double AnsysDeflection = 7.6947e-4;

    /// <summary>The reference's own mesh density — and here, its element type too.</summary>
    private static readonly LatticeDivisions AnsysDivisions = new(100, 25, 2);

    private static Material Steel() => new()
    {
        Name = "Structural steel", YoungsModulus = 2.0e11, PoissonRatio = 0.30, Density = 7850
    };

    private static FeMesh MeshBeam(LatticeDivisions divisions, ElementShape shape) =>
        new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Length, Height, Thickness),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = shape,
                Divisions = divisions,
                ElementOrder = ElementOrder.Quadratic
            });

    /// <summary>The reference scope: fixed on the two edges where the bottom face meets each
    /// end face, loaded by a total force on the top face.</summary>
    private static SolveInput Setup(FeMesh mesh, Material material, double load)
    {
        var left = mesh.Edges.EdgesBetween(new[] { 2, 0 });
        var right = mesh.Edges.EdgesBetween(new[] { 2, 1 });
        Assert.True(left.Count == 1 && right.Count == 1,
            $"Expected one support edge per end, found {left.Count} and {right.Count}.");

        return new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport
                {
                    Name = "Supports", FaceIds = Array.Empty<int>(),
                    EdgeIds = new[] { left[0], right[0] }
                },
                new ForceLoad
                {
                    Name = "Load", FaceIds = new[] { 3 }, TotalForce = new Vector3D(0, -load, 0)
                }
            }
        };
    }

    private static (double Deflection, double MeanVonMises, int Nodes, int Elements) Solve(
        LatticeDivisions divisions, ElementShape shape, double load = Load500)
    {
        var mesh = MeshBeam(divisions, shape);
        var output = new LinearStaticSolver().Solve(Setup(mesh, Steel(), load));
        var displacement = (NodalVectorField)output.Fields.First(f => f.Name == "Displacement");
        var vonMises = output.Fields.First(f => f.Name == "Stress (von Mises)");
        return (displacement.Values.Max(v => v.Length),
                FieldStatistics.Compute(vonMises, mesh).Mean,
                mesh.NodeCount, mesh.ElementCount);
    }

    /// <summary>Solved once for the whole fixture: xUnit builds a fresh instance per test and
    /// this is a 5,000-element quadratic solve.</summary>
    private static readonly Lazy<(double Deflection, double MeanVonMises, int Nodes, int Elements)>
        ReferenceRun = new(() => Solve(AnsysDivisions, ElementShape.Hexahedral),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The support is the mathematical line the reference fixes, not a band of nodes near it —
    /// asserted BITWISE, including the mid-edge nodes, because the lattice assigns extreme
    /// coordinates verbatim. Any future change that reintroduced a perturbation fails here
    /// first, with a clear cause, rather than as a few percent of deflection.
    /// </summary>
    [Fact]
    public void EverySupportNode_SitsExactlyOnTheFixedLine()
    {
        var mesh = MeshBeam(new LatticeDivisions(20, 5, 2), ElementShape.Hexahedral);
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);

        foreach (var (faces, x) in new[] { (new[] { 2, 0 }, 0.0), (new[] { 2, 1 }, Length) })
        {
            int edgeId = Assert.Single(mesh.Edges.EdgesBetween(faces));
            var edge = mesh.Edges.EdgeById(edgeId)!;

            var nodes = new HashSet<int>(edge.NodeIds);
            foreach (var s in edge.Segments)
                nodes.Add(edgeMid[s.A < s.B ? (s.A, s.B) : (s.B, s.A)]);

            Assert.Equal(2 * 2 + 1, nodes.Count);          // corners and mids across the thickness
            foreach (int n in nodes)
            {
                Assert.Equal(x, mesh.Nodes[n].X);
                Assert.Equal(0.0, mesh.Nodes[n].Y);
            }
        }
    }

    /// <summary>
    /// THE benchmark: our HEX20 against the reference's SOLID186, at the reference's own
    /// 100 x 25 x 2 divisions.
    ///
    /// MEASURED at 7.60663e-4 m, which is 98.86% of the Ansys 7.6947e-4 m — against 102.37%
    /// for TET10 on the same divisions. The band below is set around that measurement with a
    /// stated margin, not chosen in advance.
    ///
    /// We come out very slightly STIFFER, and in the expected direction: Ansys defaults
    /// SOLID186 to 2x2x2 REDUCED integration with hourglass stabilization, which is a little
    /// more compliant than the full 3x3x3 rule used here. Full integration is a deliberate
    /// choice — the stabilization that makes reduced integration safe is machinery this solver
    /// does not carry, and the spurious zero-energy modes it suppresses would otherwise
    /// surface as eigenvalues in a modal solve.
    /// </summary>
    [Fact]
    public void SteelDeflection_OnTheReferenceMeshAndElement_MatchesAnsys()
    {
        var (deflection, _, nodes, elements) = ReferenceRun.Value;
        double ratio = deflection / AnsysDeflection;

        _output.WriteLine($"HEX20 {AnsysDivisions.Nx}x{AnsysDivisions.Ny}x{AnsysDivisions.Nz}: " +
                          $"{elements:N0} elements, {nodes:N0} nodes, u_max = {deflection:g6} m " +
                          $"against Ansys {AnsysDeflection:g6} m -> {ratio:P2}");

        Assert.Equal(AnsysDivisions.Nx * AnsysDivisions.Ny * AnsysDivisions.Nz, elements);
        Assert.InRange(ratio, 0.97, 1.01);      // measured 0.9886, +-~2%
    }

    /// <summary>
    /// The claim this batch rests on: at the SAME divisions, matching the reference's element
    /// type lands closer to its answer than the tetrahedral mesh does. Both are correct
    /// physics — this measures which discretization reproduces the reference, not which is
    /// right.
    /// </summary>
    [Fact]
    public void MatchingTheReferenceElementType_LandsCloserThanTetrahedra()
    {
        var hex = ReferenceRun.Value.Deflection / AnsysDeflection;
        var tet = Solve(AnsysDivisions, ElementShape.Tetrahedral).Deflection / AnsysDeflection;

        _output.WriteLine($"HEX20 {hex:P2} vs TET10 {tet:P2} of the reference deflection");

        Assert.True(Math.Abs(hex - 1) < Math.Abs(tet - 1),
            $"HEX20 landed at {hex:P2} and TET10 at {tet:P2}; matching the reference element " +
            "type was expected to land closer to the reference answer.");
    }

    /// <summary>
    /// Linearity, which is a property of the formulation rather than of the reference: double
    /// the load, double the response, exactly.
    /// </summary>
    [Fact]
    public void DoublingTheLoad_DoublesTheDeflectionExactly()
    {
        var divisions = new LatticeDivisions(20, 6, 2);
        double single = Solve(divisions, ElementShape.Hexahedral, Load500).Deflection;
        double doubled = Solve(divisions, ElementShape.Hexahedral, 2 * Load500).Deflection;

        Assert.Equal(2.0, doubled / single, 6);
    }

    /// <summary>
    /// Refining PAST the reference density keeps raising the deflection, because a support
    /// fixed along a curve is singular in a three-dimensional continuum — a curve carries no
    /// area, so compliance creeps up with refinement in any code. This is precisely why the
    /// gate above is pinned at the reference's own divisions and why the peak stress is not
    /// compared at all; the trend is asserted here so that reasoning stays measured rather
    /// than remembered.
    /// </summary>
    [Fact]
    public void RefiningPastTheReferenceDensity_KeepsRaisingTheDeflection()
    {
        double coarse = Solve(new LatticeDivisions(25, 6, 2), ElementShape.Hexahedral).Deflection;
        double medium = Solve(new LatticeDivisions(50, 12, 3), ElementShape.Hexahedral).Deflection;

        _output.WriteLine($"25x6x2 {coarse:g6} m -> 50x12x3 {medium:g6} m " +
                          $"({medium / coarse:P2} of the coarse value)");

        Assert.True(medium > coarse,
            $"refinement gave {medium:g6} m against {coarse:g6} m: the singular support should " +
            "make compliance increase, not decrease.");
    }
}
