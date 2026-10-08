using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// The pieces under the board electro-thermal follow-ups (FU-25), each against an exact
/// answer: a conductivity tensor reproduces the linear field it must; the mesh transfer keeps
/// heat and reproduces a linear field; and the coupled transient of an adiabatic bar follows
/// the backward-Euler recursion of its lumped equation C·dθ/dt = P(θ) to the coupling
/// tolerance, and the closed form as the step shrinks.
/// </summary>
public class ElectroThermalCouplingTests
{
    private readonly ITestOutputHelper _output;
    public ElectroThermalCouplingTests(ITestOutputHelper output) => _output = output;

    // ---------------- Conductivity tensor ----------------

    [Fact]
    public void AnOrthotropicBlock_CarriesItsLinearField_WithTheOffDiagonalFlux()
    {
        // T = T₀ + g·x with K rotated 30° in the plane: the flux −K·∇T has a y part, which the
        // side faces must carry. Fix T on the x faces, put the exact flux through the y faces,
        // and the linear field is the solution — linear tets hold it to rounding only if the
        // tensor is assembled as it is.
        const double size = 10e-3, t0 = 300, g = 1000;
        var k = ConductivityTensor.InPlane(5, 1, 2, Math.PI / 6);
        var mesh = StructuredBoxMesh.Build(0, size, 0, size, 0, size, 3, 3, 3);
        double sidePower = g * k.Xy * size * size;           // into the body through y = size
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = StructuredBoxMesh.Conductor("block", 1),
            ElementThermalConductivity = Enumerable.Repeat(k, mesh.ElementCount).ToArray(),
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "x0", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = t0 },
                new FixedTemperature { Name = "x1", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Kelvin = t0 + g * size },
                new HeatFlux { Name = "y1", FaceIds = new[] { StructuredBoxMesh.FaceYMax }, TotalPower = sidePower },
                new HeatFlux { Name = "y0", FaceIds = new[] { StructuredBoxMesh.FaceYMin }, TotalPower = -sidePower },
            }
        });
        var temperature = ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values;
        // To the conjugate-gradient tolerance (2e-8 K measured); an isotropic K would miss by kelvins.
        for (int n = 0; n < mesh.NodeCount; n++)
            Assert.Equal(t0 + g * mesh.Nodes[n].X, temperature[n], 1e-6);
        var flux = ((NodalVectorField)output.Fields.First(f => f.Name == "Heat flux")).Values;
        var expected = k.Apply(new Vector3D(g, 0, 0)) * -1;
        foreach (var q in flux)
            Assert.True((q - expected).Length < 1e-6 * expected.Length, $"flux {q} against {expected}");
    }

    [Fact]
    public void AnIsotropicTensor_IsTheScalarConductivity()
    {
        var mesh = StructuredBoxMesh.Build(0, 1e-2, 0, 2e-2, 0, 5e-3, 3, 2, 2);
        var conditions = new BoundaryCondition[]
        {
            new FixedTemperature { Name = "cold", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = 300 },
            new HeatFlux { Name = "hot", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, TotalPower = 2 },
        };
        SolveOutput Solve(IReadOnlyList<ConductivityTensor>? tensor) => new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = StructuredBoxMesh.Conductor("k", 3.7), BoundaryConditions = conditions,
            ElementThermalConductivity = tensor
        });
        var scalar = ((NodalScalarField)Solve(null).Fields[0]).Values;
        var tensor = ((NodalScalarField)Solve(Enumerable.Repeat(ConductivityTensor.Isotropic(3.7), mesh.ElementCount).ToArray()).Fields[0]).Values;
        for (int n = 0; n < scalar.Count; n++) Assert.Equal(scalar[n], tensor[n], 1e-9);
        Assert.Throws<InvalidOperationException>(() => Solve(Enumerable.Repeat(new ConductivityTensor(1, 1, 1, 2), mesh.ElementCount).ToArray()));
    }

    // ---------------- Mesh transfer ----------------

    [Fact]
    public void TheTransfer_KeepsTheHeat_AndCarriesALinearFieldExactly()
    {
        var fine = StructuredBoxMesh.Build(0, 1, 0, 1, 0, 1, 5, 4, 3);
        var coarse = StructuredBoxMesh.Build(0, 1, 0, 1, 0, 1, 2, 3, 2);
        var transfer = MeshTransfer.Build(fine, coarse);
        Assert.Equal(0, transfer.Outside);

        var density = Enumerable.Range(0, fine.ElementCount).Select(e => 1.0 + e % 7).ToArray();
        double put = Enumerable.Range(0, fine.ElementCount).Sum(e => density[e] * fine.ElementVolume(e));
        var moved = transfer.Heat(density);
        double got = Enumerable.Range(0, coarse.ElementCount).Sum(e => moved[e] * coarse.ElementVolume(e));
        Assert.Equal(put, got, 1e-12 * put);

        double Linear(Vector3D p) => 3 + 2 * p.X - p.Y + 0.5 * p.Z;
        var values = transfer.ElementValues(coarse.Nodes.Select(Linear).ToArray());
        for (int e = 0; e < fine.ElementCount; e++)
        {
            var el = fine.Elements[e];
            var c = (fine.Nodes[el.N0] + fine.Nodes[el.N1] + fine.Nodes[el.N2] + fine.Nodes[el.N3]) * 0.25;
            Assert.Equal(Linear(c), values[e], 1e-12);
        }
    }

    // ---------------- Coupled transient ----------------

    private const double Sigma0 = 5.8e7, Alpha = 0.004, T0 = 300;
    private const double Length = 10e-3, Width = 1e-3, Thickness = 0.5e-3;

    private static readonly Material Copper = new()
    {
        Name = "bar copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
        ThermalConductivity = 400, SpecificHeat = 385, ElectricalConductivity = Sigma0,
        ResistivityTemperatureCoefficient = Alpha, ResistivityReferenceTemperature = T0
    };

    private static double Capacity => Copper.Density * Copper.SpecificHeat!.Value * Length * Width * Thickness;
    private static double Resistance => Length / (Sigma0 * Width * Thickness);

    private ElectroThermalTransientResult Run(IReadOnlyList<ConductionTerminal> terminals, double step, int steps) =>
        ElectroThermalStudy.SolveTransient(new ElectroThermalInput
        {
            Mesh = StructuredBoxMesh.Build(0, Length, 0, Width, 0, Thickness, 4, 1, 1),
            Material = Copper,
            Terminals = terminals,
            StartTemperature = T0,
            Tolerance = 1e-9
        }, new TransientThermalSettings
        {
            InitialTemperature = T0, TimeStep = step, Duration = step * steps, Capacity = ThermalCapacity.Lumped
        });

    [Fact]
    public void AnAdiabaticBar_CurrentDriven_FollowsTheBackwardEulerRecursion_AndTheExponential()
    {
        // C·dθ/dt = I²R₀(1 + αθ): θ(t) = (e^{αP₀t/C} − 1)/α. Backward Euler, step for step:
        // θₙ = (C·θₙ₋₁/Δt + P₀)/(C/Δt − αP₀).
        const double amps = 30;
        double p0 = amps * amps * Resistance, tau = Capacity / (Alpha * p0);
        var terminals = new[]
        {
            new ConductionTerminal { Name = "in", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, SourceVolts = 1.0 },
            new ConductionTerminal { Name = "out", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, LoadCurrent = amps },
        };
        const int steps = 20;
        double dt = tau / steps;
        var result = Run(terminals, dt, steps);

        double theta = 0;
        for (int n = 0; n < steps; n++)
        {
            theta = (Capacity * theta / dt + p0) / (Capacity / dt - Alpha * p0);
            var (time, loss, peak) = result.History[n];
            Assert.Equal((n + 1) * dt, time, 1e-12 * tau);
            Assert.Equal(theta, peak - T0, 1e-6 * theta);
            Assert.Equal(p0 * (1 + Alpha * theta), loss, 1e-6 * loss);
        }
        double exact = (Math.E - 1) / Alpha;
        _output.WriteLine($"θ(τ): backward Euler {theta:f4} K at 20 steps, closed form {exact:f4} K; " +
                          $"{result.ElectricalSolves} electrical solves");

        // As the step shrinks the march approaches the exponential (first order).
        var fine = Run(terminals, tau / 160, 160);
        double coarseError = Math.Abs(theta - exact), fineError = Math.Abs(fine.History[^1].PeakKelvin - T0 - exact);
        _output.WriteLine($"error {coarseError:f4} K at τ/20, {fineError:f4} K at τ/160");
        Assert.InRange(coarseError / fineError, 6, 10);
    }

    [Fact]
    public void AnAdiabaticBar_VoltageDriven_FollowsItsRecursion()
    {
        // C·dθ/dt = V²/(R₀(1 + αθ)): each backward-Euler step solves
        // αθₙ² + (1 − αθₙ₋₁)θₙ − θₙ₋₁ − P₀Δt/C = 0.
        const double volts = 0.02;
        double p0 = volts * volts / Resistance, dt = 0.05 * Capacity / (Alpha * p0);
        var terminals = new[]
        {
            new ConductionTerminal { Name = "high", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, SourceVolts = volts },
            new ConductionTerminal { Name = "low", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, SourceVolts = 0 },
        };
        var result = Run(terminals, dt, 12);
        double theta = 0;
        for (int n = 0; n < 12; n++)
        {
            double b = 1 - Alpha * theta, c = -(theta + p0 * dt / Capacity);
            theta = (-b + Math.Sqrt(b * b - 4 * Alpha * c)) / (2 * Alpha);
            Assert.Equal(theta, result.History[n].PeakKelvin - T0, 1e-6 * theta);
        }
    }
}
