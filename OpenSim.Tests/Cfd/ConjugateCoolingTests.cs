using OpenSim.Cfd;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using OpenSim.Tests.Solvers;
using Xunit.Abstractions;

namespace OpenSim.Tests.Cfd;

/// <summary>
/// The conjugate CFD solve as the cooling of the electro-thermal loop (FU-25). The coupling
/// adds nothing of its own to the flow: with a resistivity that does not follow temperature
/// the loop's answer is the conjugate study's for the same heat, exactly; with one that does,
/// the loss ratio is 1 + α·(mean conductor rise) as on any cooling.
/// </summary>
public class ConjugateCoolingTests
{
    private readonly ITestOutputHelper _output;
    public ConjugateCoolingTests(ITestOutputHelper output) => _output = output;

    private const double Alpha = 0.004, T0 = 293.15, Amps = 30;

    /// <summary>A 0.2 m resistive block (σ = 1000 S/m, so 30 A make 4.5 W) in the conjugate
    /// tests' 0.01 m/s channel.</summary>
    private static Material Block(double? alpha) => StructuredBoxMesh.Conductor("resistive block", 200) with
    {
        ElectricalConductivity = 1000, ResistivityTemperatureCoefficient = alpha, ResistivityReferenceTemperature = T0
    };

    private static EnvironmentSettings Channel => new()
    {
        Medium = MediumKind.MovingFluid,
        AmbientTemperature = T0,
        FlowVelocity = new Vector3D(0.01, 0, 0),
        CustomFluid = FluidProperties.Constant("test air",
            density: 1.2, dynamicViscosity: 1.8e-5, thermalConductivity: 0.026,
            specificHeat: 1005, thermalExpansion: 3.4e-3),
        IncludeRadiation = false,
        Gravity = new Vector3D(0, 0, 0)
    };

    private static CfdSettings Grid => CfdSettings.ForExternalFlow(new Vector3D(0.01, 0, 0)) with
    {
        DomainBox = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1.5, 0.5, 0.5)),
        CellSize = 0.5 / 12,
        SteadyTolerance = 1e-6,
        MaxSteps = 30000
    };

    private static ElectroThermalInput Input(FeMesh mesh, double? alpha) => new()
    {
        Mesh = mesh,
        Material = Block(alpha),
        Terminals = new[]
        {
            new ConductionTerminal { Name = "in", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, SourceVolts = 1.0 },
            new ConductionTerminal { Name = "out", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, LoadCurrent = Amps },
        },
        Environment = Channel,
        Cooling = ConjugateCooling.Steady(Grid),
        StartTemperature = T0,
        // The flow solve settles to its own tolerance, which moves the wall temperatures by
        // a few millikelvin from one solve to the next: the loop stops well above that.
        Tolerance = 0.05,
        MaxIterations = 12
    };

    private static FeMesh Mesh => StructuredBoxMesh.Build(0.4, 0.6, 0.15, 0.35, 0.15, 0.35, 2, 2, 2);

    [Fact]
    public void WithoutFeedback_TheLoopIsTheConjugateStudy()
    {
        var mesh = Mesh;
        var coupled = ElectroThermalStudy.Solve(Input(mesh, alpha: null));
        var heat = coupled.Electrical.ElementPowerDensity.ToArray();
        var direct = ConjugateHeatStudy.Run(new SolveInput
        {
            Mesh = mesh, Material = Block(null), BoundaryConditions = Array.Empty<BoundaryCondition>(),
            ElementHeatSource = heat
        }, new[] { 0 }, Channel, Grid).Thermal;
        var expected = ((NodalScalarField)direct.Fields.First(f => f.Name == "Temperature")).Values;
        for (int n = 0; n < expected.Count; n++) Assert.Equal(expected[n], coupled.Temperature[n], 1e-9);
        _output.WriteLine($"{coupled.Electrical.TotalPower:g4} W; peak {coupled.Temperature.Max() - T0:f3} K above ambient");
    }

    [Fact]
    public void WithFeedback_TheLossFollowsTheConductorTemperature()
    {
        var mesh = Mesh;
        var result = ElectroThermalStudy.Solve(Input(mesh, Alpha));
        double weighted = 0, weight = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double w = result.ColdElectrical.ElementPowerDensity[e] * mesh.ElementVolume(e);
            weighted += w * (result.ElementTemperature[e] - T0);
            weight += w;
        }
        double ratio = result.Electrical.TotalPower / result.ColdElectrical.TotalPower;
        _output.WriteLine($"loss {result.ColdElectrical.TotalPower:g4} → {result.Electrical.TotalPower:g4} W, ratio {ratio:f5} " +
                          $"against 1 + α·mean rise {1 + Alpha * weighted / weight:f5}; peak {result.Temperature.Max() - T0:f3} K " +
                          $"(one-way {result.OneWayTemperature.Max() - T0:f3} K), {result.Iterations} passes");
        Assert.Equal(1 + Alpha * weighted / weight, ratio, 2e-3);
        Assert.True(result.Temperature.Max() > result.OneWayTemperature.Max());
    }
}
