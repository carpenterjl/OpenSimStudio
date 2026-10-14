using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// FU-44: a transient that starts from a given field, the second-order and adaptive
/// integrators. Every expected value is first principles: separation of variables for a
/// cosine mode, the lumped body's exponential, the energy of a pulse train.
/// </summary>
public class ThermalLeftoverTests
{
    private readonly ITestOutputHelper _out;
    public ThermalLeftoverTests(ITestOutputHelper output) => _out = output;

    private static Material Solid(double conductivity, double volumetricCapacity) => new()
    {
        Name = "solid",
        Density = 1000,
        SpecificHeat = volumetricCapacity / 1000,
        ThermalConductivity = conductivity,
        YoungsModulus = 1e9,
        PoissonRatio = 0.3
    };

    private static IReadOnlyList<double> Temperature(IReadOnlyList<IResultField> fields) =>
        ((NodalScalarField)fields.Single(f => f.Name == "Temperature")).Values;

    private static readonly int[] AllFaces =
    {
        StructuredBoxMesh.FaceXMin, StructuredBoxMesh.FaceXMax, StructuredBoxMesh.FaceYMin,
        StructuredBoxMesh.FaceYMax, StructuredBoxMesh.FaceZMin, StructuredBoxMesh.FaceZMax
    };

    // ---------------- The initial state ----------------

    [Fact]
    public void AStartFromTheSteadyState_StaysThere()
    {
        var mesh = StructuredBoxMesh.Build(0, 0.02, 0, 0.01, 0, 0.005, 8, 4, 2);
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Solid(20, 2e6),
            BoundaryConditions = new BoundaryCondition[]
            {
                new HeatFlux { Name = "heat", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, TotalPower = 1.0 },
                new Convection { Name = "sink", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, Coefficient = 200, AmbientTemperature = 300 }
            },
            ThermalNetworks = new[]
            {
                new ThermalNetwork
                {
                    Name = "part",
                    Nodes = new[] { new ThermalNetworkNode { Name = "junction", PowerWatts = 0.5, CapacityJoulesPerKelvin = 0.05 } },
                    Attachments = new[] { new ThermalNetworkAttachment("junction", new[] { StructuredBoxMesh.FaceZMax }, 0.2) }
                }
            }
        };
        var steady = new HeatConductionSolver().Solve(input);
        var settings = new TransientThermalSettings { InitialTemperature = 300, Duration = 50, TimeStep = 5 };
        var held = new TransientThermalSolver().Solve(input with
        {
            TransientThermal = settings with { InitialState = ThermalInitialState.FromSteady(steady) }
        });
        var cold = new TransientThermalSolver().Solve(input with { TransientThermal = settings });

        var expected = Temperature(steady.Fields);
        var final = Temperature(held.Fields);
        double worst = 0;
        for (int i = 0; i < expected.Count; i++) worst = Math.Max(worst, Math.Abs(final[i] - expected[i]));
        double junction = steady.Networks!.Single().Of("junction");
        double heldJunction = held.Networks!.Single().Of("junction");
        double coldJunction = cold.Networks!.Single().Of("junction");
        _out.WriteLine($"steady junction {junction:F4} K, held {heldJunction:F4} K, from 300 K {coldJunction:F4} K; worst node {worst:g3} K");
        Assert.True(worst < 1e-6, $"a start in the steady state moved by {worst:g3} K");
        Assert.Equal(junction, heldJunction, 1e-6);
        Assert.True(junction - coldJunction > 1, "the uniform start should still be warming up — the state was not used");
    }

    /// <summary>
    /// An insulated bar starting as T = T₀ + ΔT·cos(πx/L): separation of variables gives the
    /// mode decaying as e^(−απ²t/L²) about the unchanged mean. The heat content (lumped
    /// capacity × temperature, which is what the discrete system conserves) must not move at
    /// all; the amplitude after one e-fold differs from e⁻¹ by the mesh's eigenvalue error,
    /// −(πh/L)²/12 for linear elements with lumped capacity (5.1e-4 at 40 elements).
    /// </summary>
    [Fact]
    public void ACosineStart_DecaysAtTheDiffusionRate_AboutAConservedMean()
    {
        const double length = 0.04, width = 0.001, k = 10, capacity = 1e6, amplitude = 10;
        var mesh = StructuredBoxMesh.Build(0, length, 0, width, 0, width, 40, 1, 1);
        double rate = k / capacity * Math.PI * Math.PI / (length * length);
        var start = mesh.Nodes.Select(p => 300 + amplitude * Math.Cos(Math.PI * p.X / length)).ToArray();
        var output = new TransientThermalSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Solid(k, capacity),
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = 300, Duration = 1 / rate, TimeStep = 0.25 / rate,
                Integrator = TimeIntegrator.Adaptive, Tolerance = 1e-6, Capacity = ThermalCapacity.Lumped,
                InitialState = new ThermalInitialState { NodalTemperature = start }
            }
        });
        var final = Temperature(output.Fields);
        var lumped = new ScalarDiffusionAssembler(mesh, _ => k).AssembleLumpedMass(_ => capacity).GetDiagonal();
        double before = 0, after = 0;
        for (int i = 0; i < mesh.NodeCount; i++) { before += lumped[i] * start[i]; after += lumped[i] * final[i]; }
        double hot = mesh.Nodes.Select((p, i) => (p, i)).Where(x => x.p.X < 1e-12).Average(x => final[x.i]);
        double cold = mesh.Nodes.Select((p, i) => (p, i)).Where(x => x.p.X > length - 1e-12).Average(x => final[x.i]);
        double measured = 0.5 * (hot - cold) / amplitude;
        _out.WriteLine($"amplitude after one e-fold: {measured:F6} (e^-1 = {Math.Exp(-1):F6}, ratio {measured / Math.Exp(-1) - 1:+0.0e+0;-0.0e+0}); " +
                       $"heat content moved by {(after - before) / before:g3}");
        foreach (var line in output.Log) _out.WriteLine(line);
        Assert.Equal(before, after, Math.Abs(before) * 1e-12);
        Assert.True(Math.Abs(measured / Math.Exp(-1) - 1) < 1e-3, $"amplitude {measured:F6} against e^-1");
    }

    [Fact]
    public void Validation_RefusesAWrongLengthStartAndAHigherOrderStepOnANonlinearModel()
    {
        var mesh = StructuredBoxMesh.Build(0, 0.01, 0, 0.01, 0, 0.01, 2, 2, 2);
        var input = new SolveInput
        {
            Mesh = mesh, Material = Solid(10, 1e6), BoundaryConditions = Array.Empty<BoundaryCondition>(),
            TransientThermal = new TransientThermalSettings
            {
                InitialTemperature = 300, Duration = 1, TimeStep = 0.1,
                InitialState = new ThermalInitialState { NodalTemperature = new double[3] }
            }
        };
        var ex = Assert.Throws<InvalidOperationException>(() => new TransientThermalSolver().Validate(input));
        Assert.Contains("nodes", ex.Message);
        ex = Assert.Throws<InvalidOperationException>(() => new TransientThermalSolver().Validate(input with
        {
            Material = Solid(10, 1e6) with { Emissivity = 0.9 },
            Environment = new EnvironmentSettings { Medium = MediumKind.Vacuum, AmbientTemperature = 300, IncludeRadiation = true },
            TransientThermal = input.TransientThermal! with { InitialState = null, Integrator = TimeIntegrator.TrBdf2 }
        }));
        Assert.Contains("backward Euler", ex.Message);
    }

    // ---------------- TR-BDF2 and the adaptive step ----------------

    /// <summary>
    /// A 1 cm cube conducting so well that it is one temperature (Bi = hL/k = 5e-8), heated
    /// by 3 W and convecting h = 50 W/(m²·K) from all six faces: T = T∞ + (P/hA)(1 − e^(−t/τ)),
    /// τ = ρcV/(hA) = 100 s, a 100 K rise. Halving the step must halve backward Euler's error
    /// at t = τ (first order) and quarter TR-BDF2's (second order).
    /// </summary>
    [Fact]
    public void TrBdf2_IsSecondOrder_WhereBackwardEulerIsFirst_AndTheAdaptiveStepKeepsItsTolerance()
    {
        const double side = 0.01, h = 50, capacity = 3e6, q = 3e6, ambient = 300;
        var mesh = StructuredBoxMesh.Build(0, side, 0, side, 0, side, 3, 3, 3);
        double area = 6 * side * side, volume = side * side * side;
        double tau = capacity * volume / (h * area), rise = q * volume / (h * area);
        double exact = ambient + rise * (1 - Math.Exp(-1));
        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Solid(1e7, capacity),
            ElementHeatSource = Enumerable.Repeat(q, mesh.ElementCount).ToArray(),
            BoundaryConditions = new BoundaryCondition[]
            {
                new Convection { Name = "air", FaceIds = AllFaces, Coefficient = h, AmbientTemperature = ambient }
            }
        };
        (double Error, SolveOutput Output) Run(TimeIntegrator integrator, double step, double tolerance = 0.01)
        {
            var output = new TransientThermalSolver().Solve(input with
            {
                TransientThermal = new TransientThermalSettings
                {
                    InitialTemperature = ambient, Duration = tau, TimeStep = step, Integrator = integrator, Tolerance = tolerance
                }
            });
            return (Temperature(output.Fields).Average() - exact, output);
        }
        var be1 = Run(TimeIntegrator.BackwardEuler, tau / 4).Error;
        var be2 = Run(TimeIntegrator.BackwardEuler, tau / 8).Error;
        var tr1 = Run(TimeIntegrator.TrBdf2, tau / 4).Error;
        var tr2 = Run(TimeIntegrator.TrBdf2, tau / 8).Error;
        var (adaptiveError, adaptive) = Run(TimeIntegrator.Adaptive, tau, 1e-3);
        double steps = adaptive.Summary!["Time steps"];
        _out.WriteLine($"exact {exact:F6} K; BE error {be1:g4} → {be2:g4} K (ratio {be1 / be2:F3}); " +
                       $"TR-BDF2 {tr1:g4} → {tr2:g4} K (ratio {tr1 / tr2:F3}); adaptive (1e-3 K) {adaptiveError:g4} K in {steps} steps");
        foreach (var line in adaptive.Log) _out.WriteLine(line);
        Assert.InRange(be1 / be2, 1.8, 2.1);
        Assert.InRange(tr1 / tr2, 3.6, 4.4);
        Assert.True(Math.Abs(tr1) < Math.Abs(be1) / 20, "TR-BDF2 should be far more accurate at the same step");
        // Local error control bounds the global error by the sum of the steps' errors.
        Assert.True(Math.Abs(adaptiveError) < steps * 1e-3, $"adaptive error {adaptiveError:g3} K over {steps} steps");
        Assert.Equal(2, adaptive.Frames!.Count);   // the start and t = τ (the output interval is the whole run)
    }

    /// <summary>
    /// An insulated cube heated by a pulse train (on 2.1 s of every 7 s from t = 1 s): over
    /// 30 s the power is on for 4 × 2.1 + 1.0 = 9.4 s, so the cube ends 9.4·P/(ρcV) above its
    /// start whatever the step — backward Euler, TR-BDF2 with edges inside its steps, and the
    /// adaptive step landing on every edge must all deliver that energy exactly.
    /// </summary>
    [Fact]
    public void APulseTrain_DeliversItsEnergy_WithEveryIntegrator()
    {
        const double side = 0.01, capacity = 2e6, q = 1e6;
        var mesh = StructuredBoxMesh.Build(0, side, 0, side, 0, side, 2, 2, 2);
        double expected = 300 + 9.4 * q / capacity;
        foreach (var (integrator, step) in new[]
                 {
                     (TimeIntegrator.BackwardEuler, 2.0), (TimeIntegrator.TrBdf2, 2.0), (TimeIntegrator.Adaptive, 10.0)
                 })
        {
            var output = new TransientThermalSolver().Solve(new SolveInput
            {
                Mesh = mesh,
                Material = Solid(50, capacity),
                ElementHeatSource = Enumerable.Repeat(q, mesh.ElementCount).ToArray(),
                BoundaryConditions = Array.Empty<BoundaryCondition>(),
                TransientThermal = new TransientThermalSettings
                {
                    InitialTemperature = 300, Duration = 30, TimeStep = step, Integrator = integrator,
                    PowerProfile = PowerProfile.Pulse(7, 0.3, delay: 1)
                }
            });
            var final = Temperature(output.Fields);
            _out.WriteLine($"{integrator}: {final.Average():F9} K (expected {expected:F9}); {output.Log.First(l => l.Contains(" steps"))}");
            foreach (double value in final) Assert.Equal(expected, value, 1e-8);
            if (integrator == TimeIntegrator.Adaptive)
                Assert.Equal(new[] { 0.0, 10, 20, 30 }, output.Frames!.Select(f => f.Value).ToArray());
        }
    }

    // ---------------- Convection varying over a face ----------------

    private const double PlateLength = 0.05, PlateWidth = 0.02, PlateThickness = 0.002;

    private static FeMesh Plate() => StructuredBoxMesh.Build(0, PlateLength, 0, PlateWidth, 0, PlateThickness, 20, 8, 1);

    private static double Area(FeMesh mesh, BoundaryTriangle t) =>
        0.5 * Vector3D.Cross(mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]).Length;

    /// <summary>
    /// A laminar layer, h ∝ x^−½ from the leading edge, with its mean over the top face held
    /// at h̄: the coefficients must integrate to h̄·A exactly; each triangle's away from the
    /// edge must be the law's own mean over it, (1 + n)·⟨(x/L)ⁿ⟩ with the continuous scale
    /// (the leading edge's singular row costs the discrete scale a little); a plate too
    /// conductive to have a gradient must sit where a uniform h̄ puts it; a poorly
    /// conducting one must run cooler at the leading edge than at the trailing one.
    /// </summary>
    [Fact]
    public void ADevelopingBoundaryLayer_KeepsTheMean_AndFollowsItsLaw()
    {
        const double mean = 20, exponent = -0.5;
        var mesh = Plate();
        var uniform = new Convection
        {
            Name = "air", FaceIds = new[] { StructuredBoxMesh.FaceZMax }, Coefficient = mean, AmbientTemperature = 300
        };
        var developing = uniform with
        {
            Profile = new ConvectionProfile
            {
                LeadingEdge = new Vector3D(0, 0, PlateThickness), FlowDirection = new Vector3D(1, 0, 0), Exponent = exponent
            }
        };
        var terms = ConvectionTerms.Triangles(mesh, developing);
        double area = terms.Sum(x => Area(mesh, x.Triangle)), conductance = terms.Sum(x => x.Coefficient * Area(mesh, x.Triangle));
        double worst = 0;
        foreach (var (t, h) in terms)
        {
            var (a, b, c) = (mesh.Nodes[t.A], mesh.Nodes[t.B], mesh.Nodes[t.C]);
            if (Math.Min(a.X, Math.Min(b.X, c.X)) < PlateLength / 10) continue;
            // The law's mean over the triangle on a 200 × 200 barycentric grid.
            const int grid = 200;
            double sum = 0;
            int count = 0;
            for (int i = 0; i < grid; i++)
                for (int j = 0; i + j < grid; j++)
                {
                    double u = (i + 1.0 / 3) / grid, v = (j + 1.0 / 3) / grid;
                    sum += Math.Pow((a.X + (b.X - a.X) * u + (c.X - a.X) * v) / PlateLength, exponent);
                    count++;
                    if (i + j >= grid - 1) continue;
                    u = (i + 2.0 / 3) / grid; v = (j + 2.0 / 3) / grid;
                    sum += Math.Pow((a.X + (b.X - a.X) * u + (c.X - a.X) * v) / PlateLength, exponent);
                    count++;
                }
            worst = Math.Max(worst, Math.Abs(h / (mean * (1 + exponent) * sum / count) - 1));
        }

        SolveOutput Run(Convection condition, double k) => new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh, Material = Solid(k, 2e6), BoundaryConditions = new BoundaryCondition[] { condition },
            ElementHeatSource = Enumerable.Repeat(1.0 / (PlateLength * PlateWidth * PlateThickness), mesh.ElementCount).ToArray()
        });
        double Edge(SolveOutput output, double x) => mesh.Nodes.Select((p, i) => (p, i))
            .Where(n => Math.Abs(n.p.X - x) < 1e-12 && Math.Abs(n.p.Z - PlateThickness) < 1e-12).Average(n => Temperature(output.Fields)[n.i]);
        double stiffUniform = Temperature(Run(uniform, 1e6).Fields).Average();
        double stiffDeveloping = Temperature(Run(developing, 1e6).Fields).Average();
        var poor = Run(developing, 0.5);
        var poorUniform = Run(uniform, 0.5);
        _out.WriteLine($"Σh·A = {conductance:g12} W/K against h̄·A = {mean * area:g12}; worst triangle off the law by {worst:g3}");
        _out.WriteLine($"conductive plate: uniform h̄ {stiffUniform:F6} K, developing {stiffDeveloping:F6} K; poor conductor: leading edge " +
                       $"{Edge(poor, 0):F3} K, trailing {Edge(poor, PlateLength):F3} K (uniform h: {Edge(poorUniform, 0):F3} / {Edge(poorUniform, PlateLength):F3} K)");
        Assert.Equal(mean * area, conductance, mean * area * 1e-12);
        // The solver's mean is exact; the reference grid's own centroid-rule error is ~1e-7.
        Assert.True(worst < 1e-6, $"a triangle's coefficient is {worst:g3} off the law");
        Assert.Equal(stiffUniform, stiffDeveloping, 1e-4);
        // With a uniform coefficient the ends differ only by the one-layer tetrahedral split's
        // asymmetry, a small fraction of what the developing layer does.
        double developingSpread = Edge(poor, PlateLength) - Edge(poor, 0);
        Assert.True(developingSpread > 10, "the leading edge should run cooler");
        Assert.True(Math.Abs(Edge(poorUniform, PlateLength) - Edge(poorUniform, 0)) < 0.02 * developingSpread);
    }

    /// <summary>A wake behind a part, and apart from it a factor table, each linear in
    /// distance, so each triangle's mean is the factor at its centroid — exactly; outside
    /// the wake the factor is 1, exactly.</summary>
    [Fact]
    public void AWakeAndATable_ScaleTheCoefficientWhereTheySay()
    {
        const double h = 30;
        var mesh = Plate();
        var wake = new ConvectionWake(new Vector3D(0.01, 0.01, PlateThickness), HalfWidth: 0.004, Length: 0.02, Factor: 0.4);
        var bare = new Convection { Name = "air", FaceIds = new[] { StructuredBoxMesh.FaceZMax }, Coefficient = h, AmbientTemperature = 300 };
        var withWake = bare with
        {
            Profile = new ConvectionProfile
            {
                LeadingEdge = new Vector3D(0, 0, PlateThickness), FlowDirection = new Vector3D(2, 0, 0), Wakes = new[] { wake }
            }
        };
        var withTable = bare with
        {
            Profile = new ConvectionProfile
            {
                LeadingEdge = new Vector3D(0, 0, PlateThickness), FlowDirection = new Vector3D(2, 0, 0),
                Distances = new[] { 0.0, PlateLength }, Factors = new[] { 1.0, 0.5 }
            }
        };
        int inside = 0, outside = 0;
        double worst = 0;
        foreach (var (t, coefficient) in ConvectionTerms.Triangles(mesh, withWake))
        {
            var corners = new[] { mesh.Nodes[t.A], mesh.Nodes[t.B], mesh.Nodes[t.C] };
            var centroid = (corners[0] + corners[1] + corners[2]) * (1.0 / 3);
            bool allIn = corners.All(p => p.X >= 0.01 - 1e-12 && p.X <= 0.03 + 1e-12 && Math.Abs(p.Y - 0.01) <= 0.0025 + 1e-12);
            bool allOut = corners.All(p => Math.Abs(p.Y - 0.01) > 0.004) || corners.All(p => p.X <= 0.01);
            double expected;
            if (allIn) { expected = h * (0.4 + 0.6 * (centroid.X - 0.01) / 0.02); inside++; }
            else if (allOut) { expected = h; outside++; }
            else continue;
            worst = Math.Max(worst, Math.Abs(coefficient - expected) / expected);
        }
        foreach (var (t, coefficient) in ConvectionTerms.Triangles(mesh, withTable))
        {
            double x = (mesh.Nodes[t.A].X + mesh.Nodes[t.B].X + mesh.Nodes[t.C].X) / 3;
            worst = Math.Max(worst, Math.Abs(coefficient / (h * (1 - 0.5 * x / PlateLength)) - 1));
        }
        _out.WriteLine($"{inside} triangles inside the wake, {outside} clear of it; worst {worst:g3}");
        Assert.True(inside > 10 && outside > 100);
        Assert.True(worst < 1e-12, $"worst {worst:g3}");
        var bad = withTable with { Profile = withTable.Profile! with { Exponent = -1.2 } };
        var ex = Assert.Throws<InvalidOperationException>(() => new HeatConductionSolver().Validate(new SolveInput
        {
            Mesh = mesh, Material = Solid(10, 1e6), BoundaryConditions = new BoundaryCondition[] { bad }
        }));
        Assert.Contains("exponent", ex.Message);
    }

    // ---------------- Thermal impedance: nonlinear, several sources ----------------

    /// <summary>
    /// A 2 cm cube conducting too well for a gradient, heated by 2 W and radiating (ε = 0.8)
    /// to 300 K surroundings — by the environment's radiation, and again by a radiation set on
    /// its faces: one temperature, C·dT/dt = P − ε·A·σ(T⁴ − T∞⁴), integrated here by classical
    /// Runge–Kutta as the reference. The nonlinear curve must follow it; the linear curve
    /// (the exchange held at the steady state's secant) shares the steady value but not the
    /// way there.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheNonlinearCurve_FollowsTheRadiatingBody(bool bySet)
    {
        const double side = 0.02, power = 2, emissivity = 0.8, capacity = 1e6, ambient = 300;
        double sigma = 2 * Math.Pow(Math.PI, 5) * Math.Pow(1.380649e-23, 4) / (15 * Math.Pow(6.62607015e-34, 3) * 299792458.0 * 299792458.0);
        var mesh = StructuredBoxMesh.Build(0, side, 0, side, 0, side, 3, 3, 3);
        double volume = side * side * side, area = 6 * side * side, heat = capacity * volume;
        var input = new SolveInput
        {
            Mesh = mesh, Material = Solid(5000, capacity) with { Emissivity = emissivity },
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            ElementHeatSource = Enumerable.Repeat(power / volume, mesh.ElementCount).ToArray(),
            Environment = bySet ? null : new EnvironmentSettings { Medium = MediumKind.Vacuum, AmbientTemperature = ambient, IncludeRadiation = true },
            SurfaceRadiation = bySet ? new[] { new SurfaceRadiation { Name = "cube", FaceIds = AllFaces, AmbientTemperature = ambient } } : null
        };
        var settings = new ThermalImpedanceSettings { StartTime = 1, EndTime = 1e4, PointsPerDecade = 12 };
        var nonlinear = ThermalImpedanceSolver.Solve(input, null, settings with { Nonlinear = true }).Curves.Single();
        var linear = ThermalImpedanceSolver.Solve(input, null, settings).Curves.Single();

        double Rate(double t) => (power - emissivity * area * sigma * (Math.Pow(t, 4) - Math.Pow(ambient, 4))) / heat;
        double temperature = ambient, time = 0, worst = 0, apart = 0;
        for (int i = 0; i < nonlinear.TimesSeconds.Count; i++)
        {
            double target = nonlinear.TimesSeconds[i];
            int steps = Math.Max(10, (int)Math.Ceiling((target - time) / 0.05));
            double h = (target - time) / steps;
            for (int s = 0; s < steps; s++)
            {
                double k1 = Rate(temperature), k2 = Rate(temperature + 0.5 * h * k1), k3 = Rate(temperature + 0.5 * h * k2), k4 = Rate(temperature + h * k3);
                temperature += h / 6 * (k1 + 2 * k2 + 2 * k3 + k4);
            }
            time = target;
            double reference = (temperature - ambient) / power;
            worst = Math.Max(worst, Math.Abs(nonlinear.KelvinPerWatt[i] - reference));
            apart = Math.Max(apart, Math.Abs(linear.KelvinPerWatt[i] - reference));
        }
        double steady = Math.Pow(power / (emissivity * area * sigma) + Math.Pow(ambient, 4), 0.25) - ambient;
        _out.WriteLine($"{(bySet ? "radiation set" : "environment")}: steady rise {steady / power:F5} K/W (nonlinear {nonlinear.SteadyKelvinPerWatt:F5}, " +
                       $"linear {linear.SteadyKelvinPerWatt:F5}); nonlinear curve within {worst / (steady / power):P3} of the lumped equation, " +
                       $"linear within {apart / (steady / power):P2}");
        Assert.True(worst < 0.005 * steady / power, $"nonlinear curve off by {worst:g3} K/W");
        Assert.Equal(nonlinear.SteadyKelvinPerWatt, linear.SteadyKelvinPerWatt, 1e-6);
        Assert.Equal(steady / power, nonlinear.SteadyKelvinPerWatt, 0.002 * steady / power);
        Assert.True(apart > 0.03 * steady / power, "the linear curve should part from the radiating body's");
    }

    /// <summary>
    /// Two heated pads at the ends of a plate cooled from below. Read where each source's
    /// heat goes in, the matrix is symmetric (a conduction model is reciprocal); both
    /// sources on together are the sum of the columns; the mutual curve starts late; the
    /// shared-time-constant Foster matrix follows every entry.
    /// </summary>
    [Fact]
    public void TheImpedanceMatrix_IsReciprocal_Superposes_AndFits()
    {
        var plate = StructuredBoxMesh.Build(0, 0.03, 0, 0.01, 0, 0.002, 12, 4, 2);
        var skin = plate.BoundaryTriangles.Select(t =>
        {
            if (t.FaceId != StructuredBoxMesh.FaceZMax) return t;
            double x = (plate.Nodes[t.A].X + plate.Nodes[t.B].X + plate.Nodes[t.C].X) / 3;
            return x < 0.006 ? t with { FaceId = 20 } : x > 0.024 ? t with { FaceId = 21 } : t;
        }).ToList();
        var mesh = new FeMesh(plate.Nodes, plate.Elements, skin, plate.ElementRegionIds);
        var input = new SolveInput
        {
            Mesh = mesh, Material = Solid(20, 2e6),
            BoundaryConditions = new BoundaryCondition[]
            {
                new HeatFlux { Name = "A", FaceIds = new[] { 20 }, TotalPower = 1 },
                new HeatFlux { Name = "B", FaceIds = new[] { 21 }, TotalPower = 2 },
                new Convection { Name = "sink", FaceIds = new[] { StructuredBoxMesh.FaceZMin }, Coefficient = 50, AmbientTemperature = 300 }
            }
        };
        var settings = new ThermalImpedanceSettings { StartTime = 1e-3, EndTime = 1e4, PointsPerDecade = 10 };
        var sources = new[] { new ThermalSource { Name = "A", HeatFlow = "A" }, new ThermalSource { Name = "B", HeatFlow = "B" } };
        var matrix = ThermalImpedanceSolver.SolveMatrix(input, sources, settings);
        var both = ThermalImpedanceSolver.Solve(input, new[]
        {
            new ThermalProbe { Name = "A", FaceIds = new[] { 20 } }, new ThermalProbe { Name = "B", FaceIds = new[] { 21 } }
        }, settings);
        double scale = matrix[0, 0].KelvinPerWatt[^1];
        double asymmetry = 0, superposition = 0;
        for (int i = 0; i < matrix[0, 0].TimesSeconds.Count; i++)
        {
            asymmetry = Math.Max(asymmetry, Math.Abs(matrix[0, 1].KelvinPerWatt[i] - matrix[1, 0].KelvinPerWatt[i]));
            for (int at = 0; at < 2; at++)
            {
                double sum = matrix.Powers[0] * matrix[at, 0].KelvinPerWatt[i] + matrix.Powers[1] * matrix[at, 1].KelvinPerWatt[i];
                superposition = Math.Max(superposition, Math.Abs(both.Curves[at].KelvinPerWatt[i] * both.PowerWatts - sum));
            }
        }
        var fit = ThermalNetworkFit.FitFosterMatrix(matrix);
        string spice = fit.Network.ToSpice();
        _out.WriteLine($"Z_AA {matrix[0, 0].KelvinPerWatt[^1]:F4}, Z_BB {matrix[1, 1].KelvinPerWatt[^1]:F4}, Z_AB {matrix[0, 1].KelvinPerWatt[^1]:F4} K/W at the end; " +
                       $"|Z_AB − Z_BA| ≤ {asymmetry / scale:g3} of Z_AA; superposition within {superposition:g3} K; " +
                       $"at 1 ms Z_AB/Z_AA = {matrix[0, 1].KelvinPerWatt[0] / matrix[0, 0].KelvinPerWatt[0]:g3}");
        _out.WriteLine(fit.Describe());
        Assert.Equal(1.0, matrix.Powers[0], 1e-12);
        Assert.Equal(2.0, matrix.Powers[1], 1e-12);
        Assert.True(asymmetry < 1e-8 * scale, $"Z_AB and Z_BA differ by {asymmetry:g3}");
        Assert.True(superposition < 1e-8 * scale * 3, $"superposition off by {superposition:g3} K");
        Assert.True(matrix[0, 1].KelvinPerWatt[0] < 1e-3 * matrix[0, 0].KelvinPerWatt[0], "the mutual curve should start late");
        Assert.True(fit.WorstError < 0.01, fit.Describe());
        Assert.Contains(".SUBCKT ZTH_MATRIX p1 p2 amb", spice);
        Assert.Contains("F1_2 amb c1_2_0 V2 1", spice);
        Assert.Throws<InvalidOperationException>(() => ThermalImpedanceSolver.SolveMatrix(input, sources, settings with { Nonlinear = true }));
    }
}
