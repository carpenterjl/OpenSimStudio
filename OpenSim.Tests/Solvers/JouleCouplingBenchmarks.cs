using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Regression benchmarks for the coupled Joule heating study against the analytical
/// solution of a uniformly heated bar.
/// </summary>
public class JouleCouplingBenchmarks
{
    private static readonly Material Copper = new()
    {
        Name = "Copper",
        YoungsModulus = 110e9,
        PoissonRatio = 0.34,
        Density = 8960,
        ElectricalConductivity = 5.96e7,
        ThermalConductivity = 400
    };

    private static FeMesh MeshBox(double x, double y, double z, double h) =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(x, y, z), new MeshSettings { TargetEdgeLength = h });

    // ------------------------------------------------------------------
    // A bar with ΔV across its ends carries the uniform field E = ΔV/L, so the
    // Joule source is q = σE² everywhere. With both ends held at T₀ and insulated
    // sides, ΔT_max = qL²/(8k) = σ·ΔV²/(8k) — independent of the bar geometry.
    // ------------------------------------------------------------------
    [Fact]
    public void JouleHeatedBar_MatchesAnalyticTemperatureRise()
    {
        const double length = 0.1, width = 0.02, thick = 0.01;
        const double volts = 0.005;
        const double t0 = 300.0;

        var mesh = MeshBox(length, width, thick, 0.008);
        var output = new JouleHeatingStudy().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Ground", FaceIds = new[] { 0 }, Volts = 0 },
                new VoltagePotential { Name = "Supply", FaceIds = new[] { 1 }, Volts = volts },
                new FixedTemperature { Name = "Left sink", FaceIds = new[] { 0 }, Kelvin = t0 },
                new FixedTemperature { Name = "Right sink", FaceIds = new[] { 1 }, Kelvin = t0 }
            }
        });

        double analyticRise = Copper.ElectricalConductivity!.Value * volts * volts
                              / (8 * Copper.ThermalConductivity!.Value);
        var temperature = (NodalScalarField)output.Fields.Single(f => f.Name == "Temperature");
        double maxRise = Enumerable.Range(0, mesh.NodeCount).Max(temperature.GetScalar) - t0;
        Assert.Equal(analyticRise, maxRise, analyticRise * 3e-2);

        // The merged output must carry both physics' fields.
        Assert.Contains(output.Fields, f => f.Name == "Electric potential");
        Assert.Contains(output.Fields, f => f.Name == "Current density");
        Assert.Contains(output.Fields, f => f.Name == "Heat flux");
    }

    // ------------------------------------------------------------------
    // Power bookkeeping: the total volumetric heat source fed into the thermal
    // solve must equal the electrically dissipated power ΔV²/R = ΔV²·σ·A_eff/L.
    // ------------------------------------------------------------------
    [Fact]
    public void JouleHeatedBar_PowerBookkeepingIsConsistent()
    {
        const double length = 0.1, width = 0.02, thick = 0.01;
        const double volts = 0.005;

        var mesh = MeshBox(length, width, thick, 0.008);
        var output = new JouleHeatingStudy().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Ground", FaceIds = new[] { 0 }, Volts = 0 },
                new VoltagePotential { Name = "Supply", FaceIds = new[] { 1 }, Volts = volts },
                new Convection
                {
                    Name = "Air", FaceIds = new[] { 2, 3, 4, 5 },
                    Coefficient = 25, AmbientTemperature = 300
                }
            }
        });

        var power = (ElementScalarField)output.Fields.Single(
            f => f.Name == ElectricalConductionSolver.ElementPowerFieldName);
        double totalSource = Enumerable.Range(0, mesh.ElementCount)
            .Sum(e => power.Values[e] * mesh.ElementVolume(e));

        double effectiveArea = mesh.TotalVolume() / length;
        double analyticPower = volts * volts * Copper.ElectricalConductivity!.Value * effectiveArea / length;
        // 2%: P1 overestimates conductance (hence power) by ~1%; the refined mesh no
        // longer masks it with a volume deficit. See Bar_ResistanceMatchesAnalytic.
        Assert.Equal(analyticPower, totalSource, analyticPower * 2e-2);
    }

    // ------------------------------------------------------------------
    // Validation must reject BCs that belong to neither physics and require
    // each sub-problem to be well-posed.
    // ------------------------------------------------------------------
    [Fact]
    public void Validate_RejectsForeignBcsAndIllPosedSubproblems()
    {
        var mesh = MeshBox(0.05, 0.02, 0.02, 0.01);
        var study = new JouleHeatingStudy();

        var structural = new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport { Name = "Fix", FaceIds = new[] { 0 } }
            }
        };
        Assert.Throws<InvalidOperationException>(() => study.Validate(structural));

        // Well-posed electrically, but no thermal sink.
        var noSink = new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Ground", FaceIds = new[] { 0 }, Volts = 0 },
                new VoltagePotential { Name = "Supply", FaceIds = new[] { 1 }, Volts = 1 }
            }
        };
        var ex = Assert.Throws<InvalidOperationException>(() => study.Validate(noSink));
        Assert.Contains("temperature or convection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
