using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Regression benchmarks for the transient heat conduction solver (backward Euler)
/// against analytical solutions.
/// </summary>
public class TransientThermalBenchmarks
{
    private static readonly Material Copper = new()
    {
        Name = "Copper",
        YoungsModulus = 110e9,
        PoissonRatio = 0.34,
        Density = 8960,
        ThermalConductivity = 400,
        SpecificHeat = 385
    };

    private static double VolumetricCapacity => Copper.Density * Copper.SpecificHeat!.Value;
    private static double Diffusivity => Copper.ThermalConductivity!.Value / VolumetricCapacity;

    private static FeMesh MeshBox(double x, double y, double z, double h) =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(x, y, z), new MeshSettings { TargetEdgeLength = h });

    private static NodalScalarField Temperature(IReadOnlyList<IResultField> fields) =>
        (NodalScalarField)fields.Single(f => f.Name == "Temperature");

    private static double FaceArea(FeMesh mesh, IReadOnlyList<int> faceIds) =>
        mesh.GetFaceTriangles(faceIds).Sum(t =>
            0.5 * OpenSim.Core.Numerics.Vector3D.Cross(
                mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]).Length);

    // ------------------------------------------------------------------
    // Patch-exact: adiabatic uniform heating. With a spatially uniform field the
    // diffusion term vanishes (K row sums are zero), consistent mass and consistent
    // source loads share the same V/4 nodal weights, and backward Euler integrates the
    // constant-rate ODE exactly — so T(t) = T₀ + q·t/(ρc_p) must hold at EVERY node
    // to solver precision, on any (jittered) mesh.
    // ------------------------------------------------------------------
    [Fact]
    public void AdiabaticUniformHeating_ExactAtEveryNode()
    {
        const double t0 = 300.0;
        const double q = 2e7;                                      // W/m³
        const double duration = 5.0, dt = 0.5;

        var mesh = MeshBox(0.03, 0.02, 0.01, 0.008);
        var output = new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            ElementHeatSource = Enumerable.Repeat(q, mesh.ElementCount).ToArray(),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = t0, Duration = duration, TimeStep = dt
            }
        });

        double exact = t0 + q * duration / VolumetricCapacity;
        var temperature = Temperature(output.Fields);
        for (int n = 0; n < mesh.NodeCount; n++)
            Assert.Equal(exact, temperature.Values[n], exact * 1e-9);
    }

    // ------------------------------------------------------------------
    // Lumped-capacitance cooling: a small copper block (Bi = hL/k ≪ 1, so it stays
    // near-uniform) convecting from all faces follows T(t) = T∞ + (T₀−T∞)·e^(−t/τ)
    // with τ = ρc_p·V/(h·A). Areas/volume from the actual mesh cancel mesher jitter.
    // ------------------------------------------------------------------
    [Fact]
    public void LumpedCapacitanceCooling_MatchesExponential()
    {
        const double size = 0.02;
        const double h = 100.0, ambient = 300.0, t0 = 400.0;
        var allFaces = new[] { 0, 1, 2, 3, 4, 5 };

        var mesh = MeshBox(size, size, size, 0.008);
        double area = FaceArea(mesh, allFaces);
        double volume = mesh.TotalVolume();
        double tau = VolumetricCapacity * volume / (h * area);

        var output = new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new Convection
                {
                    Name = "All faces", FaceIds = allFaces,
                    Coefficient = h, AmbientTemperature = ambient
                }
            },
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = t0, Duration = tau, TimeStep = tau / 100
            }
        });

        var temperature = Temperature(output.Fields);
        double mean = temperature.Values.Average();
        double analytic = ambient + (t0 - ambient) * Math.Exp(-1.0);
        // 2%: covers BE's O(Δt) decay error at Δt = τ/100 (~0.5%) plus the small
        // surface-to-core gradient a finite Biot number leaves behind.
        Assert.Equal(analytic - ambient, mean - ambient, (analytic - ambient) * 2e-2);
    }

    // ------------------------------------------------------------------
    // t → ∞ limit: after many time constants the transient solution must land on the
    // steady-state solver's answer for the identical flux-in/convection-out input.
    // ------------------------------------------------------------------
    [Fact]
    public void LongDuration_ConvergesToSteadyState()
    {
        const double length = 0.1, width = 0.02, thick = 0.01;
        const double power = 20.0, h = 500.0, ambient = 300.0;

        var mesh = MeshBox(length, width, thick, 0.008);
        var bcs = new BoundaryCondition[]
        {
            new HeatFlux { Name = "Heater", FaceIds = new[] { 0 }, TotalPower = power },
            new Convection
            {
                Name = "Cooling", FaceIds = new[] { 1 },
                Coefficient = h, AmbientTemperature = ambient
            }
        };
        var steadyInput = new SolveInput { Mesh = mesh, Material = Copper, BoundaryConditions = bcs };
        var steady = Temperature(new HeatConductionSolver().Solve(steadyInput).Fields);

        // Slowest mode of the near-lumped block: τ ≈ ρc_p·V/(h·A_conv).
        double tau = VolumetricCapacity * mesh.TotalVolume() / (h * FaceArea(mesh, new[] { 1 }));
        var transient = new TransientThermalSolver().Solve(steadyInput with
        {
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = ambient, Duration = 40 * tau, TimeStep = tau / 2
            }
        });

        var final = Temperature(transient.Fields);
        double range = steady.Values.Max() - steady.Values.Min();
        double maxDiff = Enumerable.Range(0, mesh.NodeCount)
            .Max(n => Math.Abs(final.Values[n] - steady.Values[n]));
        Assert.True(maxDiff < 1e-6 * range,
            $"Transient end state differs from steady state by {maxDiff:g3} K (range {range:g3} K).");
    }

    // ------------------------------------------------------------------
    // 1D slab step response: a slab at T₀ whose two x-faces are stepped to T_s follows
    // θ(x,t)/θ₀ = Σ_{k odd} (4/(kπ))·sin(kπx/L)·exp(−(kπ/L)²·α·t). Checked at the node
    // nearest the midplane at Fo = αt/L² = 0.1 (first terms dominate, signal ≈ 0.47·θ₀).
    // ------------------------------------------------------------------
    [Fact]
    public void SlabStepResponse_MatchesFourierSeries()
    {
        const double length = 0.05, cross = 0.01;
        const double t0 = 400.0, ts = 300.0;
        double time = 0.1 * length * length / Diffusivity;         // Fo = 0.1

        var mesh = MeshBox(length, cross, cross, 0.004);
        var output = new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "Left", FaceIds = new[] { 0 }, Kelvin = ts },
                new FixedTemperature { Name = "Right", FaceIds = new[] { 1 }, Kelvin = ts }
            },
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = t0, Duration = time, TimeStep = time / 200
            }
        });

        int midNode = Enumerable.Range(0, mesh.NodeCount)
            .OrderBy(n => Math.Abs(mesh.Nodes[n].X - length / 2)).First();
        double x = mesh.Nodes[midNode].X;

        double series = 0;
        for (int k = 1; k <= 9; k += 2)
            series += 4.0 / (k * Math.PI) * Math.Sin(k * Math.PI * x / length)
                      * Math.Exp(-Math.Pow(k * Math.PI / length, 2) * Diffusivity * time);
        double analytic = ts + (t0 - ts) * series;

        var temperature = Temperature(output.Fields);
        // 3%: BE temporal error at 200 steps (~0.5% on the dominant mode) plus the
        // linear-element spatial error of the sine profile.
        Assert.Equal(analytic - ts, temperature.Values[midNode] - ts, (analytic - ts) * 3e-2);
    }

    // ------------------------------------------------------------------
    // Frames contract: monotone time values, initial frame = IC, Fields is the final
    // frame's field list (the default-frame convention the UI relies on).
    // ------------------------------------------------------------------
    [Fact]
    public void Frames_OrderedAndAnchoredToInitialAndFinalState()
    {
        const double t0 = 350.0;
        var mesh = MeshBox(0.02, 0.02, 0.02, 0.008);
        var output = new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new Convection
                {
                    Name = "Cooling", FaceIds = new[] { 0, 1, 2, 3, 4, 5 },
                    Coefficient = 50, AmbientTemperature = 300
                }
            },
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = t0, Duration = 10, TimeStep = 1, OutputStride = 3
            }
        });

        Assert.NotNull(output.Frames);
        Assert.Equal("Time", output.FrameAxis);
        var frames = output.Frames!;
        Assert.Equal(0.0, frames[0].Value);
        Assert.Equal(10.0, frames[^1].Value, 1e-12);
        for (int i = 1; i < frames.Count; i++)
            Assert.True(frames[i].Value > frames[i - 1].Value, "Frame times must increase.");

        var initial = Temperature(frames[0].Fields);
        Assert.All(initial.Values, v => Assert.Equal(t0, v, 1e-12));
        Assert.Same(frames[^1].Fields, output.Fields);
        Assert.All(frames, f => Assert.Equal("s", f.Unit));
    }

    // ------------------------------------------------------------------
    // Transient Joule coupling: constant DC power over a long transient must land on
    // the steady Joule-heated temperature field.
    // ------------------------------------------------------------------
    [Fact]
    public void TransientJoule_LongDuration_MatchesSteadyJoule()
    {
        var conductive = Copper with { ElectricalConductivity = 5.96e7 };
        const double length = 0.05, cross = 0.01;
        var mesh = MeshBox(length, cross, cross, 0.006);
        var bcs = new BoundaryCondition[]
        {
            new VoltagePotential { Name = "V+", FaceIds = new[] { 0 }, Volts = 0.05 },
            new VoltagePotential { Name = "V-", FaceIds = new[] { 1 }, Volts = 0 },
            new FixedTemperature { Name = "T left", FaceIds = new[] { 0 }, Kelvin = 300 },
            new FixedTemperature { Name = "T right", FaceIds = new[] { 1 }, Kelvin = 300 }
        };
        var input = new SolveInput { Mesh = mesh, Material = conductive, BoundaryConditions = bcs };

        var steady = Temperature(new JouleHeatingStudy().Solve(input).Fields);

        // Dirichlet-dominated: the slowest mode is the L²/α conduction time.
        double tau = length * length / (Math.PI * Math.PI * Diffusivity);
        var transientOutput = new JouleHeatingStudy().Solve(input with
        {
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = 300, Duration = 30 * tau, TimeStep = tau / 2
            }
        });

        Assert.NotNull(transientOutput.Frames);
        var final = Temperature(transientOutput.Fields);
        double analyticRange = steady.Values.Max() - steady.Values.Min();
        double maxDiff = Enumerable.Range(0, mesh.NodeCount)
            .Max(n => Math.Abs(final.Values[n] - steady.Values[n]));
        Assert.True(maxDiff < 1e-2 * analyticRange,
            $"Transient Joule end state differs from steady by {maxDiff:g3} K (range {analyticRange:g3} K).");
    }

    // ------------------------------------------------------------------
    // Validation errors must be actionable.
    // ------------------------------------------------------------------
    [Fact]
    public void Validate_BadSettingsOrMaterial_Throws()
    {
        var mesh = MeshBox(0.02, 0.02, 0.02, 0.008);
        var solver = new TransientThermalSolver();
        SolveInput Input(TransientThermalSettings? settings, Material? material = null) => new()
        {
            Mesh = mesh,
            Material = material ?? Copper,
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            TransientThermal = settings
        };

        var ex1 = Assert.Throws<InvalidOperationException>(() => solver.Validate(Input(null)));
        Assert.Contains("settings", ex1.Message, StringComparison.OrdinalIgnoreCase);

        var ex2 = Assert.Throws<InvalidOperationException>(() => solver.Validate(Input(
            new TransientThermalSettings { InitialTemperature = 300, Duration = 1, TimeStep = 0 })));
        Assert.Contains("time step", ex2.Message, StringComparison.OrdinalIgnoreCase);

        var ex3 = Assert.Throws<InvalidOperationException>(() => solver.Validate(Input(
            new TransientThermalSettings { InitialTemperature = 300, Duration = 1, TimeStep = 0.01 },
            Copper with { SpecificHeat = null })));
        Assert.Contains("specific heat", ex3.Message, StringComparison.OrdinalIgnoreCase);

        var ex4 = Assert.Throws<InvalidOperationException>(() => solver.Validate(Input(
            new TransientThermalSettings
            {
                InitialTemperature = 300, Duration = 1000, TimeStep = 0.001, OutputStride = 1
            })));
        Assert.Contains("OutputStride", ex4.Message);

        var quadratic = QuadraticMeshBuilder.Upgrade(mesh);
        var ex5 = Assert.Throws<InvalidOperationException>(() => solver.Validate(new SolveInput
        {
            Mesh = quadratic,
            Material = Copper,
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = 300, Duration = 1, TimeStep = 0.01
            }
        }));
        Assert.Contains("linear", ex5.Message, StringComparison.OrdinalIgnoreCase);
    }
}
