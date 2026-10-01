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
/// The Ansys beam reproduction again, this time on the MAPPED mesh — the same 200 x 60 x
/// 20 mm beam, fixed on its two opposite bottom edges, but meshed on a regular lattice at
/// the reference's own edge divisions instead of by the Delaunay mesher.
///
/// <see cref="BeamBendingBenchmarks"/> remains the Delaunay gate and its band records what
/// that mesher achieves at suite-affordable element sizes. This fixture answers the other
/// question: given the reference's OWN mesh density, how close is the physics? Comparing at
/// equal density is the only comparison that means anything here, because the support is
/// fixed along a line and the deflection keeps creeping up with refinement.
/// </summary>
public class StructuredBeamBenchmark
{
    private readonly ITestOutputHelper _output;

    public StructuredBeamBenchmark(ITestOutputHelper output) => _output = output;

    private const double Length = 0.200, Height = 0.060, Thickness = 0.020;
    private const double Load500 = 5e5;
    private const double AnsysDeflection = 7.6947e-4;

    /// <summary>
    /// The reference's own mesh density: 5,000 SOLID186 elements as 100 x 25 x 2 edge
    /// divisions (2.0 x 2.4 x 10 mm cells). Ours are tetrahedra rather than hexahedra, so
    /// this matches the reference's DISCRETIZATION, not its element formulation.
    /// </summary>
    private static readonly LatticeDivisions AnsysDivisions = new(100, 25, 2);

    private static Material Steel() => new()
    {
        Name = "Structural steel", YoungsModulus = 2.0e11, PoissonRatio = 0.30, Density = 7850
    };

    private static FeMesh MeshBeam(LatticeDivisions divisions) =>
        new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Length, Height, Thickness),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Divisions = divisions,
                ElementOrder = ElementOrder.Quadratic   // bending: TET10 is not optional
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

    private static (double Deflection, double MeanVonMises, int Nodes) Solve(LatticeDivisions divisions)
    {
        var mesh = MeshBeam(divisions);
        var output = new LinearStaticSolver().Solve(Setup(mesh, Steel(), Load500));
        var displacement = (NodalVectorField)output.Fields.First(f => f.Name == "Displacement");
        var vonMises = output.Fields.First(f => f.Name == "Stress (von Mises)");
        return (displacement.Values.Max(v => v.Length),
                FieldStatistics.Compute(vonMises, mesh).Mean,
                mesh.NodeCount);
    }

    /// <summary>
    /// The reference-density run, solved once for the whole fixture. xUnit builds a fresh
    /// instance per test and this is a 50,000-node quadratic solve, so sharing it is the
    /// difference between one minute of suite time and two.
    /// </summary>
    private static readonly Lazy<(double Deflection, double MeanVonMises, int Nodes)> ReferenceRun =
        new(() => Solve(AnsysDivisions), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The support really is the mathematical line the reference fixes, not a band of nodes
    /// near it. This is the whole mechanism the benchmark below measures, so it is asserted
    /// separately and BITWISE — on a lattice the coordinates are exact, and any future
    /// change that reintroduced a perturbation would fail here first, with a clear cause,
    /// rather than as a few percent of deflection.
    /// </summary>
    [Fact]
    public void EverySupportNode_SitsExactlyOnTheFixedLine()
    {
        var mesh = MeshBeam(AnsysDivisions);

        var left = mesh.Edges.EdgeById(mesh.Edges.EdgesBetween(new[] { 2, 0 })[0])!;
        var right = mesh.Edges.EdgeById(mesh.Edges.EdgesBetween(new[] { 2, 1 })[0])!;

        Assert.All(left.NodeIds, n =>
        {
            Assert.Equal(0.0, mesh.Nodes[n].X);
            Assert.Equal(0.0, mesh.Nodes[n].Y);
        });
        Assert.All(right.NodeIds, n =>
        {
            Assert.Equal(Length, mesh.Nodes[n].X);
            Assert.Equal(0.0, mesh.Nodes[n].Y);
        });
        Assert.Equal(AnsysDivisions.Nz + 1, left.NodeIds.Count);
        Assert.Equal(AnsysDivisions.Nz + 1, right.NodeIds.Count);
    }

    /// <summary>
    /// The headline: steel at 500 kN on the reference's own edge divisions.
    ///
    /// MEASURED, and the band is set from the measurement plus a stated margin rather than
    /// from a hope. The mesh-robust targets are the deflection and the mean stress; the peak
    /// stress and strain remain deliberately ungated here for the same reason as in the
    /// Delaunay benchmark — they are a support singularity at a fixed mathematical line and
    /// do not converge under refinement in any code.
    /// </summary>
    [Fact]
    public void SteelDeflection_OnTheReferenceMeshDensity_MatchesAnsys()
    {
        var run = ReferenceRun.Value;
        double ratio = run.Deflection / AnsysDeflection;

        _output.WriteLine(
            $"{AnsysDivisions.Nx}x{AnsysDivisions.Ny}x{AnsysDivisions.Nz} lattice, {run.Nodes:N0} nodes: " +
            $"u_max = {run.Deflection:g6} m ({ratio:P1} of Ansys {AnsysDeflection:g6}), " +
            $"mean von Mises = {run.MeanVonMises:g6} Pa");

        // MEASURED: 7.87687e-4 m, 102.4% of the Ansys 7.6947e-4, at the reference's own
        // 100 x 25 x 2 divisions. The band is that measurement plus a stated margin, and it
        // is narrow enough to fail on the Delaunay mesher's 88.9% at a comparable cost.
        Assert.InRange(ratio, 0.95, 1.10);
    }

    /// <summary>
    /// Convergence with cell size, reported rather than asserted as a single number — and
    /// the comparison against the Delaunay mesher, which is the claim this batch rests on.
    /// <para>
    /// Refining PAST the reference density keeps raising the deflection (measured out of
    /// suite: 105.2% at 140 x 40 x 6, 296k nodes). That is the physics of a support fixed
    /// along a mathematical LINE — a curve carries no area in a 3D continuum, so the
    /// solution has a singularity there and the compliance keeps creeping up with
    /// refinement, in this code as in any other. It is exactly why the comparison worth
    /// making is at the reference's OWN mesh density, and why the peak stress and strain
    /// are not compared at all.
    /// </para>
    /// </summary>
    [Fact]
    public void StructuredMesh_LandsCloserToAnsysThanTheJitteredOne()
    {
        foreach (var d in new[] { new LatticeDivisions(25, 8, 2), new LatticeDivisions(50, 13, 2) })
        {
            var coarse = Solve(d);
            _output.WriteLine(
                $"{d.Nx,4}x{d.Ny,3}x{d.Nz}: {coarse.Nodes,7:N0} nodes, u_max = {coarse.Deflection:g6} m " +
                $"({coarse.Deflection / AnsysDeflection:P1} of Ansys)");
        }

        var reference = ReferenceRun.Value;
        _output.WriteLine(
            $"{AnsysDivisions.Nx,4}x{AnsysDivisions.Ny,3}x{AnsysDivisions.Nz}: {reference.Nodes,7:N0} nodes, " +
            $"u_max = {reference.Deflection:g6} m ({reference.Deflection / AnsysDeflection:P1} of Ansys)");
        double structured = reference.Deflection / AnsysDeflection;

        var delaunay = new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Length, Height, Thickness),
            new MeshSettings { TargetEdgeLength = 0.010, ElementOrder = ElementOrder.Quadratic });
        var delaunayOut = new LinearStaticSolver().Solve(Setup(delaunay, Steel(), Load500));
        double delaunayRatio =
            ((NodalVectorField)delaunayOut.Fields.First(f => f.Name == "Displacement"))
            .Values.Max(v => v.Length) / AnsysDeflection;

        _output.WriteLine($"structured {structured:P2} vs Delaunay(h=10mm) {delaunayRatio:P2} of Ansys");
        Assert.True(Math.Abs(structured - 1) < Math.Abs(delaunayRatio - 1),
            $"Structured {structured:P2} is no closer to Ansys than Delaunay {delaunayRatio:P2}.");
    }
}
