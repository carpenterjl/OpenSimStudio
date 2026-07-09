using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Regression benchmarks for the DC conduction solver against analytical solutions.
/// </summary>
public class ElectricalSolverBenchmarks
{
    private static readonly Material Copper = new()
    {
        Name = "Copper",
        YoungsModulus = 110e9,
        PoissonRatio = 0.34,
        Density = 8960,
        ElectricalConductivity = 5.96e7
    };

    private static FeMesh MeshBox(double x, double y, double z, double h) =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(x, y, z), new MeshSettings { TargetEdgeLength = h });

    private static double TotalDissipatedPower(FeMesh mesh, SolveOutput output)
    {
        var power = (ElementScalarField)output.Fields.Single(
            f => f.Name == ElectricalConductionSolver.ElementPowerFieldName);
        return Enumerable.Range(0, mesh.ElementCount).Sum(e => power.Values[e] * mesh.ElementVolume(e));
    }

    // ------------------------------------------------------------------
    // Patch test: with φ = b·x + c·y + d·z prescribed on every boundary node,
    // a consistent TET4 diffusion assembly reproduces the constant gradient
    // exactly at arbitrary interior nodes.
    // ------------------------------------------------------------------
    [Fact]
    public void PatchTest_LinearPotentialReproducedExactly()
    {
        var mesh = MeshBox(1, 1, 1, 0.35);
        var assembler = new ScalarDiffusionAssembler(mesh, _ => 1.0);
        var stiffness = assembler.AssembleStiffness();

        var expected = new Vector3D(2.0, -0.7, 1.3);
        var boundaryNodes = mesh.GetFaceNodes(mesh.BoundaryTriangles.Select(t => t.FaceId).Distinct());
        Assert.True(mesh.NodeCount > boundaryNodes.Count, "Patch test needs interior nodes.");

        var prescribed = new Dictionary<int, double>();
        foreach (int n in boundaryNodes)
        {
            var p = mesh.Nodes[n];
            prescribed[n] = expected.X * p.X + expected.Y * p.Y + expected.Z * p.Z;
        }

        var result = ConstrainedSystemSolver.Solve(stiffness, new double[assembler.DofCount], prescribed,
            tolerance: 1e-13);

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var gradient = assembler.ElementGradient(e, result.Displacements);
            Assert.Equal(expected.X, gradient.X, 9);
            Assert.Equal(expected.Y, gradient.Y, 9);
            Assert.Equal(expected.Z, gradient.Z, 9);
        }
    }

    // ------------------------------------------------------------------
    // 1D bar: potentials on the two end faces give the exact linear solution
    // R = L/(σA), verified through the dissipated power P = ΔV²/R.
    // ------------------------------------------------------------------
    [Fact]
    public void Bar_ResistanceMatchesAnalytic()
    {
        const double length = 0.1, width = 0.02, thick = 0.01;
        const double volts = 0.001;

        var mesh = MeshBox(length, width, thick, 0.008);
        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Ground", FaceIds = new[] { 0 }, Volts = 0 },      // x-min
                new VoltagePotential { Name = "Supply", FaceIds = new[] { 1 }, Volts = volts }   // x-max
            }
        });

        // The mesher intentionally jitters the surface, so the meshed bar's effective
        // cross-section differs from the nominal box by ~2%. Normalizing by the actual
        // mesh volume (A_eff = V/L) isolates the solver error from the mesher's.
        double area = mesh.TotalVolume() / length;
        double analyticR = length / (Copper.ElectricalConductivity!.Value * area);
        double solvedR = volts * volts / TotalDissipatedPower(mesh, output);
        // 2% tolerance: conforming P1 overestimates conductance (energy minimization
        // over a subspace), ~1% here. The pre-refinement mesh masked this by
        // under-filling low-current edges/corners; the refined mesh does not.
        Assert.Equal(analyticR, solvedR, analyticR * 2e-2);

        // Uniform current density J = σ·ΔV/L everywhere.
        double analyticJ = Copper.ElectricalConductivity.Value * volts / length;
        var current = (NodalVectorField)output.Fields.Single(f => f.Name == "Current density");
        double avgJ = Enumerable.Range(0, current.Count).Average(current.GetScalar);
        Assert.Equal(analyticJ, avgJ, analyticJ * 1e-2);
    }

    // ------------------------------------------------------------------
    // Current conservation: injecting a total current through one face against
    // a grounded far face must reproduce ΔV = I·R at the injection face.
    // ------------------------------------------------------------------
    [Fact]
    public void CurrentInjection_ReproducesOhmsLaw()
    {
        const double length = 0.1, width = 0.02, thick = 0.01;
        const double amps = 10.0;

        var mesh = MeshBox(length, width, thick, 0.008);
        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Ground", FaceIds = new[] { 0 }, Volts = 0 },
                new CurrentFlow { Name = "Injection", FaceIds = new[] { 1 }, TotalCurrent = amps }
            }
        });

        // Effective cross-section from the actual (jittered) mesh volume, as above.
        double analyticV = amps * length / (Copper.ElectricalConductivity!.Value * (mesh.TotalVolume() / length));
        var potential = (NodalScalarField)output.Fields.Single(f => f.Name == "Electric potential");
        double avgTip = mesh.GetFaceNodes(new[] { 1 }).Average(n => potential.Values[n]);
        Assert.Equal(analyticV, avgTip, analyticV * 1e-2);

        // Energy bookkeeping: dissipated power must equal I·V for the solved field.
        Assert.Equal(avgTip * amps, TotalDissipatedPower(mesh, output), avgTip * amps * 1e-2);

        // A current-driven test must still report resistance in the summary
        // (R = P/I², exact for a two-terminal DC network) so the UI's
        // current-excitation mode reads the same keys as the voltage mode.
        double analyticR = analyticV / amps;
        Assert.NotNull(output.Summary);
        Assert.Equal(analyticR, output.Summary!["Resistance (Ω)"], analyticR * 1e-2);
        Assert.Equal(amps, output.Summary["Current (A)"], amps * 1e-9);
    }

    // ------------------------------------------------------------------
    // Two conductivities in series via region materials: R = R₁ + R₂.
    // Elements are classified by centroid, so the interface is jagged at the
    // element scale — the tolerance covers that discretization of the split plane.
    // ------------------------------------------------------------------
    [Fact]
    public void TwoRegionSeriesBar_MatchesSeriesResistance()
    {
        const double length = 0.1, width = 0.02, thick = 0.01;
        const double volts = 0.001;
        var halfConductive = Copper with { Name = "Half copper", ElectricalConductivity = Copper.ElectricalConductivity / 2 };

        var baseMesh = MeshBox(length, width, thick, 0.008);
        var regions = new int[baseMesh.ElementCount];
        for (int e = 0; e < baseMesh.ElementCount; e++)
        {
            var el = baseMesh.Elements[e];
            double cx = (baseMesh.Nodes[el.N0].X + baseMesh.Nodes[el.N1].X
                         + baseMesh.Nodes[el.N2].X + baseMesh.Nodes[el.N3].X) / 4.0;
            regions[e] = cx < length / 2 ? 0 : 1;
        }
        var mesh = new FeMesh(baseMesh.Nodes, baseMesh.Elements, baseMesh.BoundaryTriangles, regions);
        Assert.Contains(0, regions);
        Assert.Contains(1, regions);

        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            RegionMaterials = new Dictionary<int, Material> { [0] = Copper, [1] = halfConductive },
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Ground", FaceIds = new[] { 0 }, Volts = 0 },
                new VoltagePotential { Name = "Supply", FaceIds = new[] { 1 }, Volts = volts }
            }
        });

        double area = mesh.TotalVolume() / length;
        double analyticR = length / 2 / (Copper.ElectricalConductivity!.Value * area)
                           + length / 2 / (halfConductive.ElectricalConductivity!.Value * area);
        double solvedR = volts * volts / TotalDissipatedPower(mesh, output);
        Assert.Equal(analyticR, solvedR, analyticR * 3e-2);
    }

    // ------------------------------------------------------------------
    // Validation errors must be actionable.
    // ------------------------------------------------------------------
    [Fact]
    public void Validate_MissingVoltageOrWrongBcType_Throws()
    {
        var mesh = MeshBox(0.05, 0.02, 0.02, 0.01);
        var solver = new ElectricalConductionSolver();

        var noVoltage = new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new CurrentFlow { Name = "I", FaceIds = new[] { 1 }, TotalCurrent = 1 }
            }
        };
        var ex1 = Assert.Throws<InvalidOperationException>(() => solver.Validate(noVoltage));
        Assert.Contains("voltage", ex1.Message, StringComparison.OrdinalIgnoreCase);

        var structuralBc = new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "V", FaceIds = new[] { 0 }, Volts = 0 },
                new FixedSupport { Name = "Fix", FaceIds = new[] { 1 } }
            }
        };
        var ex2 = Assert.Throws<InvalidOperationException>(() => solver.Validate(structuralBc));
        Assert.Contains("does not apply", ex2.Message, StringComparison.OrdinalIgnoreCase);

        var noConductivity = new SolveInput
        {
            Mesh = mesh,
            Material = Copper with { ElectricalConductivity = null },
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "V", FaceIds = new[] { 0 }, Volts = 0 }
            }
        };
        var ex3 = Assert.Throws<InvalidOperationException>(() => solver.Validate(noConductivity));
        Assert.Contains("conductivity", ex3.Message, StringComparison.OrdinalIgnoreCase);
    }
}
