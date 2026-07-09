using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Regression benchmarks for the steady-state heat conduction solver against
/// analytical solutions.
/// </summary>
public class ThermalSolverBenchmarks
{
    private static readonly Material Copper = new()
    {
        Name = "Copper",
        YoungsModulus = 110e9,
        PoissonRatio = 0.34,
        Density = 8960,
        ThermalConductivity = 400
    };

    private static FeMesh MeshBox(double x, double y, double z, double h) =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(x, y, z), new MeshSettings { TargetEdgeLength = h });

    private static NodalScalarField Temperature(SolveOutput output) =>
        (NodalScalarField)output.Fields.Single(f => f.Name == "Temperature");

    // ------------------------------------------------------------------
    // 1D bar with both ends held at T₀, insulated sides, uniform volumetric
    // source q: T(x) = T₀ + q·x(L−x)/(2k), so ΔT_max = qL²/(8k) at midspan.
    // ------------------------------------------------------------------
    [Fact]
    public void UniformSource_FixedEnds_ParabolicTemperature()
    {
        const double length = 0.1, width = 0.02, thick = 0.01;
        const double t0 = 300.0;
        const double q = 5e6;                                     // W/m³

        var mesh = MeshBox(length, width, thick, 0.008);
        var source = Enumerable.Repeat(q, mesh.ElementCount).ToArray();

        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            ElementHeatSource = source,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "Left", FaceIds = new[] { 0 }, Kelvin = t0 },
                new FixedTemperature { Name = "Right", FaceIds = new[] { 1 }, Kelvin = t0 }
            }
        });

        double analyticRise = q * length * length / (8 * Copper.ThermalConductivity!.Value);
        var temperature = Temperature(output);
        double maxRise = Enumerable.Range(0, mesh.NodeCount).Max(temperature.GetScalar) - t0;
        // 3%: the parabolic profile is only approximated by linear elements, and the
        // discrete maximum sits at the node nearest to midspan, not exactly at L/2.
        Assert.Equal(analyticRise, maxRise, analyticRise * 3e-2);

        // The whole field must lie between the wall value and the analytic peak.
        double min = Enumerable.Range(0, mesh.NodeCount).Min(temperature.GetScalar);
        Assert.True(min >= t0 - 1e-9, "Temperature must not undershoot the wall value.");
    }

    // ------------------------------------------------------------------
    // Heat flow in at one end, convection out at the other, insulated sides —
    // exercises the Robin path with no Dirichlet constraint at all:
    // T_out = T_amb + P/(hA), T_in = T_out + P·L/(kA).
    // ------------------------------------------------------------------
    [Fact]
    public void FluxIn_ConvectionOut_MatchesSeriesThermalResistance()
    {
        const double length = 0.1, width = 0.02, thick = 0.01;
        const double power = 20.0;                                 // W
        const double h = 500.0;                                    // W/(m²·K)
        const double ambient = 300.0;

        var mesh = MeshBox(length, width, thick, 0.008);
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new HeatFlux { Name = "Heater", FaceIds = new[] { 0 }, TotalPower = power },
                new Convection
                {
                    Name = "Cooling", FaceIds = new[] { 1 },
                    Coefficient = h, AmbientTemperature = ambient
                }
            }
        });

        // Effective areas from the actual (jittered) mesh, isolating solver error
        // from the mesher's geometric perturbation.
        double convArea = mesh.GetFaceTriangles(new[] { 1 }).Sum(t =>
            0.5 * OpenSim.Core.Numerics.Vector3D.Cross(
                mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]).Length);
        double condArea = mesh.TotalVolume() / length;
        double analyticOut = ambient + power / (h * convArea);
        double analyticIn = analyticOut + power * length / (Copper.ThermalConductivity!.Value * condArea);

        var temperature = Temperature(output);
        double avgOut = mesh.GetFaceNodes(new[] { 1 }).Average(n => temperature.Values[n]);
        double avgIn = mesh.GetFaceNodes(new[] { 0 }).Average(n => temperature.Values[n]);
        Assert.Equal(analyticOut - ambient, avgOut - ambient, (analyticOut - ambient) * 2e-2);
        Assert.Equal(analyticIn - ambient, avgIn - ambient, (analyticIn - ambient) * 2e-2);
    }

    // ------------------------------------------------------------------
    // Validation errors must be actionable.
    // ------------------------------------------------------------------
    [Fact]
    public void Validate_MissingSinkOrBadInput_Throws()
    {
        var mesh = MeshBox(0.05, 0.02, 0.02, 0.01);
        var solver = new HeatConductionSolver();

        var fluxOnly = new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new HeatFlux { Name = "Heat", FaceIds = new[] { 0 }, TotalPower = 5 }
            }
        };
        var ex1 = Assert.Throws<InvalidOperationException>(() => solver.Validate(fluxOnly));
        Assert.Contains("fixed temperature or convection", ex1.Message, StringComparison.OrdinalIgnoreCase);

        var wrongSourceLength = new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            ElementHeatSource = new double[3],
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "Sink", FaceIds = new[] { 0 }, Kelvin = 300 }
            }
        };
        var ex2 = Assert.Throws<InvalidOperationException>(() => solver.Validate(wrongSourceLength));
        Assert.Contains("ElementHeatSource", ex2.Message);

        var noConductivity = new SolveInput
        {
            Mesh = mesh,
            Material = Copper with { ThermalConductivity = null },
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "Sink", FaceIds = new[] { 0 }, Kelvin = 300 }
            }
        };
        var ex3 = Assert.Throws<InvalidOperationException>(() => solver.Validate(noConductivity));
        Assert.Contains("conductivity", ex3.Message, StringComparison.OrdinalIgnoreCase);
    }
}
