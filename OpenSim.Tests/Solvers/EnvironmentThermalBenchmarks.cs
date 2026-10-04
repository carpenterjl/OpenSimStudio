using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using OpenSim.Solvers.Environment;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// End-to-end gates on the environment: surfaces classified into panels, correlations and
/// radiation evaluated on them, and the resulting nonlinear boundary condition solved by
/// lagged-coefficient iteration inside the thermal solvers.
///
/// The structured box mesh is used throughout so surface areas, volumes and face normals
/// are exact and the only error left to measure is the one under test.
/// </summary>
public class EnvironmentThermalBenchmarks
{
    private const double Side = 0.04;          // 40 mm cube
    private const double Ambient = 300.0;
    private const double Initial = 500.0;

    private static double Volume => Side * Side * Side;
    private static double SurfaceArea => 6 * Side * Side;

    /// <summary>A near-isothermal body: high conductivity so the Biot number is ~1e-3 and the
    /// lumped-capacitance solution the gates compare against is the exact answer.</summary>
    private static Material Lump(double emissivity = 0.9) =>
        StructuredBoxMesh.Conductor("Lump", 400) with { Emissivity = emissivity };

    private static FeMesh Cube(int n = 2) =>
        StructuredBoxMesh.Build(0, Side, 0, Side, 0, Side, n, n, n);

    /// <summary>Constant-property air-like fluid. β = 0 on purpose in the forced-convection
    /// gate: it removes the temperature dependence of natural convection so the exact
    /// exponential is the truth, and h stays a number the test can compute by hand.</summary>
    private static FluidProperties TestFluid(double expansion) => FluidProperties.Constant(
        "Test gas", density: 1.2, dynamicViscosity: 1.8e-5, thermalConductivity: 0.026,
        specificHeat: 1005, thermalExpansion: expansion);

    private static SolveInput Input(FeMesh mesh, EnvironmentSettings? environment,
        Material? material = null, IReadOnlyList<BoundaryCondition>? conditions = null,
        TransientThermalSettings? transient = null, IReadOnlyList<double>? source = null) =>
        new()
        {
            Mesh = mesh,
            Material = material ?? Lump(),
            BoundaryConditions = conditions ?? Array.Empty<BoundaryCondition>(),
            Environment = environment,
            TransientThermal = transient,
            ElementHeatSource = source
        };

    private static double[] Temperatures(SolveOutput output) =>
        ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values.ToArray();

    // ---------------------------------------------------------------- legacy path

    [Fact]
    public void NoEnvironment_KeepsTheOriginalFieldSet()
    {
        // The field contract of every pre-environment thermal solve: temperature and flux,
        // nothing else. An extra field would break consumers that index the list.
        var mesh = Cube();
        var conditions = new BoundaryCondition[]
        {
            new FixedTemperature { Name = "Cold", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = Ambient },
            new HeatFlux { Name = "In", FaceIds = new[] { StructuredBoxMesh.FaceXMax }, TotalPower = 5 }
        };
        var output = new HeatConductionSolver().Solve(Input(mesh, null, conditions: conditions));

        Assert.Equal(new[] { "Temperature", "Heat flux" }, output.Fields.Select(f => f.Name));
    }

    [Fact]
    public void ZeroFilmCoefficients_LeaveTheAssembledMatrixBitwiseUnchanged()
    {
        // The exact statement of "an environment that exchanges nothing perturbs nothing".
        // Gated bitwise here, at the assembly, where the claim is exactly true — a full
        // solve can only match to the CG tolerance because the extra fixed-point iterate
        // re-solves the same system from a better warm start.
        var mesh = Cube();
        var assembler = new ScalarDiffusionAssembler(mesh, _ => 400);
        var baseMatrix = assembler.AssembleStiffness();

        var zero = new SurfaceFilmModel
        {
            TriangleFilmCoefficient = Enumerable.Repeat(0.0, mesh.BoundaryTriangles.Count).ToArray(),
            ReferenceTemperature = Ambient,
            Origin = "test"
        };
        var unwetted = zero with
        {
            TriangleFilmCoefficient = Enumerable.Repeat(double.NaN, mesh.BoundaryTriangles.Count).ToArray()
        };

        foreach (var film in new[] { zero, unwetted })
        {
            var stamped = EnvironmentThermalTerms.WithFilm(baseMatrix, mesh, film);
            Assert.Equal(baseMatrix.NonZeroCount, stamped.NonZeroCount);
            for (int i = 0; i < baseMatrix.Values.Length; i++)
                Assert.True(baseMatrix.Values[i].Equals(stamped.Values[i]),
                    $"entry {i} moved from {baseMatrix.Values[i]:r} to {stamped.Values[i]:r}");
        }
    }

    [Fact]
    public void EveryFaceClaimedByTheUser_MakesTheEnvironmentAbsent()
    {
        // Precedence taken to its limit: with no face left over the environment has nothing
        // to act on, the model is not built at all, and the solve is the ordinary linear one.
        var mesh = Cube();
        var conditions = new BoundaryCondition[]
        {
            new FixedTemperature { Name = "Cold", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = Ambient },
            new Convection
            {
                Name = "Rest", AmbientTemperature = Ambient, Coefficient = 12,
                FaceIds = new[]
                {
                    StructuredBoxMesh.FaceXMax, StructuredBoxMesh.FaceYMin, StructuredBoxMesh.FaceYMax,
                    StructuredBoxMesh.FaceZMin, StructuredBoxMesh.FaceZMax
                }
            }
        };
        var environment = new EnvironmentSettings { Medium = MediumKind.StillFluid, AmbientTemperature = Ambient };

        var log = new List<string>();
        Assert.Null(EnvironmentBoundaryModel.Build(
            Input(mesh, environment, conditions: conditions), log));
        Assert.Contains(log, line => line.Contains("adds nothing"));

        var withEnvironment = new HeatConductionSolver()
            .Solve(Input(mesh, environment, conditions: conditions));
        var without = new HeatConductionSolver().Solve(Input(mesh, null, conditions: conditions));
        Assert.Equal(Temperatures(without), Temperatures(withEnvironment));   // bitwise
    }

    [Fact]
    public void VacuumWithRadiationOff_IsAdiabatic()
    {
        // A vacuum that does not radiate exchanges no heat at all, so the transient must
        // reproduce the environment-free run. Not bitwise: the fixed-point loop always takes
        // a second iterate, which re-solves the identical system from a better warm start and
        // lands a CG tolerance away.
        var mesh = Cube();
        var transient = new TransientThermalSettings
        {
            InitialTemperature = Initial, Duration = 20, TimeStep = 1
        };
        var source = Enumerable.Repeat(2e5, mesh.ElementCount).ToArray();
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.Vacuum, AmbientTemperature = Ambient, IncludeRadiation = false
        };

        var withEnvironment = Temperatures(new TransientThermalSolver()
            .Solve(Input(mesh, environment, transient: transient, source: source)));
        var without = Temperatures(new TransientThermalSolver()
            .Solve(Input(mesh, null, transient: transient, source: source)));

        for (int i = 0; i < without.Length; i++)
            Assert.True(Math.Abs(without[i] - withEnvironment[i]) < 1e-6,
                $"node {i}: {without[i]:F9} vs {withEnvironment[i]:F9} K");
    }

    // ---------------------------------------------------------------- physics

    [Fact]
    public void RadiationOnlyCooling_MatchesAnIndependentIntegrationOfTheLumpedOde()
    {
        // A small high-conductivity body in vacuum: the whole heat path is εσA(T⁴−T_a⁴), and
        // at Bi ≈ 7e-4 the body is isothermal, so the FE answer must reproduce the scalar
        // ODE. The oracle is an RK4 integration at a thousandth of the solver's step, using
        // the volume and area of the ACTUAL mesh so no discretization difference is left.
        const double emissivity = 0.9, step = 1.0, duration = 100.0;
        var mesh = Cube();
        var material = Lump(emissivity);
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.Vacuum, AmbientTemperature = Ambient, IncludeRadiation = true
        };
        var output = new TransientThermalSolver().Solve(Input(mesh, environment, material,
            transient: new TransientThermalSettings
            {
                InitialTemperature = Initial, Duration = duration, TimeStep = step
            }));

        double capacity = material.Density * material.SpecificHeat!.Value * mesh.TotalVolume();
        double radiance = emissivity * ConvectionCorrelations.StefanBoltzmann * SurfaceArea;
        double reference = Rk4(Initial, duration, step / 1000.0,
            t => -radiance * (Math.Pow(t, 4) - Math.Pow(Ambient, 4)) / capacity);

        var temperatures = Temperatures(output);
        double mean = temperatures.Average();
        Assert.InRange(Math.Abs(mean - reference) / (Initial - Ambient), 0, 2e-3);   // measured 6.5e-4

        // Backward Euler is monotone: the body cools every step and never overshoots ambient.
        var frames = output.Frames!;
        for (int f = 1; f < frames.Count; f++)
            Assert.True(FrameMean(frames[f]) < FrameMean(frames[f - 1]));
        Assert.True(temperatures.Min() > Ambient);
    }

    [Fact]
    public void ForcedConvectionCooling_MatchesTheExactExponential()
    {
        // The sharp end-to-end gate. Forced convection has no ΔT dependence, and with a
        // constant-property fluid at β = 0 the natural contribution collapses to the stated
        // conduction floor — so every film coefficient is a constant this test computes by
        // hand from the correlations, and the exact answer is a plain exponential decay.
        const double speed = 2.0, step = 0.5, duration = 50.0;
        var fluid = TestFluid(expansion: 0);
        var mesh = Cube();
        var material = Lump();
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.MovingFluid,
            AmbientTemperature = Ambient,
            CustomFluid = fluid,
            FlowVelocity = new Vector3D(speed, 0, 0),
            IncludeRadiation = false
        };

        var state = fluid.AtTemperature(400);
        double reynolds = speed * Side / state.KinematicViscosity;
        double forced = ConvectionCorrelations.ForcedFlatPlate(reynolds, state.Prandtl)
                        * state.ThermalConductivity / Side;
        // Vertical faces take their height as the natural length scale; the top and bottom
        // take area over perimeter, a²/(4a) = a/4.
        double vertical = ConvectionCorrelations.BlendMixedConvection(
            forced, EnvironmentBoundaryModel.QuiescentNusseltFloor * state.ThermalConductivity / Side);
        double horizontal = ConvectionCorrelations.BlendMixedConvection(
            forced, EnvironmentBoundaryModel.QuiescentNusseltFloor * state.ThermalConductivity / (Side / 4));
        // The two faces standing across the stream (windward and leeward) take the
        // cross-flow plate average over their 40 mm extent instead of the parallel-plate
        // result; the other two vertical faces and the top and bottom run along the flow.
        double acrossFlow = ConvectionCorrelations.BlendMixedConvection(
            ConvectionCorrelations.ForcedNormalPlate(reynolds, state.Prandtl)
                * state.ThermalConductivity / Side,
            EnvironmentBoundaryModel.QuiescentNusseltFloor * state.ThermalConductivity / Side);
        double conductance = Side * Side * (2 * vertical + 2 * acrossFlow + 2 * horizontal);

        double capacity = material.Density * material.SpecificHeat!.Value * Volume;
        double exact = Ambient + (Initial - Ambient) * Math.Exp(-conductance * duration / capacity);

        var output = new TransientThermalSolver().Solve(Input(mesh, environment, material,
            transient: new TransientThermalSettings
            {
                InitialTemperature = Initial, Duration = duration, TimeStep = step
            }));
        double mean = Temperatures(output).Average();
        Assert.InRange(Math.Abs(mean - exact) / (Initial - Ambient), 0, 2e-3);   // measured 4.2e-4
    }

    [Fact]
    public void SteadyEnvironment_BalancesTheHeatItLetsOut()
    {
        // Conservation, at the level the discretization actually guarantees: the consistent
        // Robin term integrates h(T−T_a) exactly over each linear triangle, so the surface
        // loss must equal the injected power to the CG tolerance, not to a band.
        const double power = 4.0;
        var mesh = Cube();
        var material = Lump();
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient, IncludeRadiation = true
        };
        var source = Enumerable.Repeat(power / mesh.TotalVolume(), mesh.ElementCount).ToArray();
        var input = Input(mesh, environment, material, source: source);

        var output = new HeatConductionSolver().Solve(input);
        var temperature = Temperatures(output);

        var film = EnvironmentBoundaryModel.Build(input, new List<string>())!.Evaluate(temperature);
        double lost = 0;
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            if (!film.IsWetted(t)) continue;
            var triangle = mesh.BoundaryTriangles[t];
            double mean = (temperature[triangle.A] + temperature[triangle.B] + temperature[triangle.C]) / 3;
            lost += film.TriangleFilmCoefficient[t] * (mean - Ambient)
                    * ScalarDiffusionAssembler.SurfaceArea(mesh, triangle);
        }
        Assert.InRange(Math.Abs(lost - power) / power, 0, 1e-6);   // measured 1.4e-8
        Assert.True(temperature.Min() > Ambient, "an internally heated body must sit above ambient");
    }

    [Fact]
    public void ConvergedIterate_SatisfiesTheExactNonlinearEquations()
    {
        // The fixed point is not an approximation of the nonlinear problem, it IS its
        // solution: re-assembling A and b from the coefficients evaluated AT the converged
        // temperature must leave A·T − b at the linear solver's residual, with no
        // linearization error underneath. Assembled here independently of the solver.
        var mesh = Cube();
        var material = Lump();
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient, IncludeRadiation = true
        };
        var source = Enumerable.Repeat(6.0 / mesh.TotalVolume(), mesh.ElementCount).ToArray();
        var input = Input(mesh, environment, material, source: source);

        var temperature = Temperatures(new HeatConductionSolver().Solve(input));
        var film = EnvironmentBoundaryModel.Build(input, new List<string>())!.Evaluate(temperature);

        var builder = new SparseMatrixBuilder(mesh.NodeCount, mesh.NodeCount);
        var conduction = new ScalarDiffusionAssembler(mesh, _ => 400).AssembleStiffness();
        for (int row = 0; row < conduction.RowCount; row++)
            for (int k = conduction.RowPointers[row]; k < conduction.RowPointers[row + 1]; k++)
                builder.Add(row, conduction.ColumnIndices[k], conduction.Values[k]);

        var loads = new double[mesh.NodeCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double share = source[e] * mesh.ElementVolume(e) / 4;
            foreach (int node in mesh.GetElementNodes(e)) loads[node] += share;
        }
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            var triangle = mesh.BoundaryTriangles[t];
            double h = film.TriangleFilmCoefficient[t];
            ScalarDiffusionAssembler.AddRobinSurface(builder, mesh, triangle, h);
            double share = h * Ambient * ScalarDiffusionAssembler.SurfaceArea(mesh, triangle) / 3;
            loads[triangle.A] += share;
            loads[triangle.B] += share;
            loads[triangle.C] += share;
        }

        var product = new double[mesh.NodeCount];
        builder.Build().Multiply(temperature, product);
        double residual = Math.Sqrt(product.Select((v, i) => (v - loads[i]) * (v - loads[i])).Sum());
        double scale = Math.Sqrt(loads.Sum(v => v * v));
        Assert.InRange(residual / scale, 0, 1e-8);   // measured 1.7e-9
    }

    [Fact]
    public void EnvironmentAloneAnchorsABodyThatCarriesNoCondition()
    {
        // Without an environment this input is the documented "temperature level is
        // undetermined" failure; with one, the surface exchange anchors it.
        var mesh = Cube();
        var source = Enumerable.Repeat(3.0 / mesh.TotalVolume(), mesh.ElementCount).ToArray();
        var bare = Input(mesh, null, source: source);
        Assert.Throws<InvalidOperationException>(() => new HeatConductionSolver().Validate(bare));

        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient, IncludeRadiation = true
        };
        new HeatConductionSolver().Solve(Input(mesh, environment, source: source));

        // …but a vacuum with radiation switched off is genuinely adiabatic and must NOT be
        // mistaken for an anchor.
        var adiabatic = environment with { Medium = MediumKind.Vacuum, IncludeRadiation = false };
        Assert.Throws<InvalidOperationException>(() =>
            new HeatConductionSolver().Validate(Input(mesh, adiabatic, source: source)));
    }

    [Fact]
    public void RadiationWithoutAnEmissivity_FailsNamingTheMaterial()
    {
        var mesh = Cube();
        var material = StructuredBoxMesh.Conductor("Unfinished alloy", 400);   // no emissivity
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient, IncludeRadiation = true
        };
        var error = Assert.Throws<InvalidOperationException>(() =>
            new HeatConductionSolver().Validate(Input(mesh, environment, material)));
        Assert.Contains("Unfinished alloy", error.Message);
        Assert.Contains("emissivity", error.Message);

        // With radiation off the same material is perfectly usable — emissivity is only
        // required by the physics that needs it.
        new HeatConductionSolver().Validate(
            Input(mesh, environment with { IncludeRadiation = false }, material));
    }

    [Fact]
    public void EnvironmentRuns_CarryTheFilmCoefficientInEveryFrame()
    {
        var mesh = Cube();
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient, IncludeRadiation = true
        };
        var output = new TransientThermalSolver().Solve(Input(mesh, environment,
            transient: new TransientThermalSettings
            {
                InitialTemperature = Initial, Duration = 10, TimeStep = 1, OutputStride = 1
            }));

        var expected = new[] { "Temperature", "Heat flux", "Film coefficient" };
        Assert.Equal(expected, output.Fields.Select(f => f.Name));
        foreach (var frame in output.Frames!)
            Assert.Equal(expected, frame.Fields.Select(f => f.Name));

        var film = (NodalScalarField)output.Frames![^1].Fields.First(f => f.Name == "Film coefficient");
        Assert.Equal("W/(m²·K)", film.Unit);
        Assert.True(film.Values.Max() > 0);
        // Interior nodes are not wetted and stay at zero.
        Assert.Contains(film.Values, v => v == 0);
    }

    // ---------------------------------------------------------------- classification

    [Fact]
    public void PanelClassifier_ReadsTheSixBoxFacesExactly()
    {
        var mesh = Cube();
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient
        };
        var panels = EnvironmentBoundaryModel.Build(Input(mesh, environment), new List<string>())!
            .Panels.ToDictionary(p => p.FaceId);

        Assert.Equal(6, panels.Count);
        Assert.Equal(PanelShape.HorizontalPlateUpward, panels[StructuredBoxMesh.FaceZMax].Shape);
        Assert.Equal(PanelShape.HorizontalPlateDownward, panels[StructuredBoxMesh.FaceZMin].Shape);
        foreach (int side in new[]
                 {
                     StructuredBoxMesh.FaceXMin, StructuredBoxMesh.FaceXMax,
                     StructuredBoxMesh.FaceYMin, StructuredBoxMesh.FaceYMax
                 })
        {
            Assert.Equal(PanelShape.VerticalPlate, panels[side].Shape);
            Assert.Equal(Side, panels[side].CharacteristicLength, 12);       // the plate height
        }

        // A horizontal plate's length scale is area over perimeter: a²/(4a) = a/4.
        Assert.Equal(Side / 4, panels[StructuredBoxMesh.FaceZMax].CharacteristicLength, 12);
        foreach (var panel in panels.Values)
        {
            Assert.Equal(Side * Side, panel.Area, 12);
            Assert.Equal(0, panel.NormalSpreadDegrees, 9);                   // every face is planar
        }
        Assert.Equal(1, Vector3D.Dot(panels[StructuredBoxMesh.FaceZMax].MeanNormal,
            new Vector3D(0, 0, 1)), 12);
    }

    [Fact]
    public void HotAndColdBodies_SwapTheHorizontalPlateCorrelations()
    {
        // The plume leaves the top of a hot body and the bottom of a cold one, so the same
        // face carries the stronger correlation in one case and the weaker one in the other.
        // Same |ΔT| both ways, so any difference is the branch choice and nothing else.
        var mesh = Cube();
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient, IncludeRadiation = false
        };
        var model = EnvironmentBoundaryModel.Build(Input(mesh, environment), new List<string>())!;

        var hot = model.Evaluate(Enumerable.Repeat(Ambient + 60, mesh.NodeCount).ToArray());
        var cold = model.Evaluate(Enumerable.Repeat(Ambient - 60, mesh.NodeCount).ToArray());

        double HotFace(SurfaceFilmModel film, int faceId) => film.TriangleFilmCoefficient[
            mesh.BoundaryTriangles.Select((t, i) => (t, i)).First(x => x.t.FaceId == faceId).i];

        double hotTop = HotFace(hot, StructuredBoxMesh.FaceZMax);
        double hotBottom = HotFace(hot, StructuredBoxMesh.FaceZMin);
        double coldTop = HotFace(cold, StructuredBoxMesh.FaceZMax);
        double coldBottom = HotFace(cold, StructuredBoxMesh.FaceZMin);

        Assert.True(hotTop > hotBottom, "a hot body convects better off its top face");
        Assert.True(coldBottom > coldTop, "a cold body convects better off its bottom face");
        // The ratio is the correlations', 0.54 versus 0.27, not something the classifier invents.
        Assert.InRange(hotBottom / hotTop, 0.4, 0.6);
    }

    [Fact]
    public void TiltingGravity_MovesFacesAcrossThe45DegreeThreshold()
    {
        // Orientation is measured against gravity, not against the mesh axes. Tilting
        // gravity 50° from −Z swaps which faces of the same box count as horizontal.
        var mesh = Cube();
        double radians = 50 * Math.PI / 180;
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid,
            AmbientTemperature = Ambient,
            Gravity = new Vector3D(-Math.Sin(radians), 0, -Math.Cos(radians)) * 9.80665
        };
        var panels = EnvironmentBoundaryModel.Build(Input(mesh, environment), new List<string>())!
            .Panels.ToDictionary(p => p.FaceId);

        // 50° past the threshold: +X now faces "up", +Z has become a vertical wall.
        Assert.Equal(PanelShape.HorizontalPlateUpward, panels[StructuredBoxMesh.FaceXMax].Shape);
        Assert.Equal(PanelShape.HorizontalPlateDownward, panels[StructuredBoxMesh.FaceXMin].Shape);
        Assert.Equal(PanelShape.VerticalPlate, panels[StructuredBoxMesh.FaceZMax].Shape);
        Assert.Equal(PanelShape.VerticalPlate, panels[StructuredBoxMesh.FaceYMin].Shape);
    }

    [Fact]
    public void AFaceWhoseNormalsSpread_FallsBackToTheVerticalPlateAndSaysSo()
    {
        // The whole box skin tagged as ONE geometric face: its normals point every way, so
        // no plate orientation is meaningful and the panel is reported as curved.
        var cube = Cube();
        var mesh = new FeMesh(cube.Nodes, cube.Elements,
            cube.BoundaryTriangles.Select(t => t with { FaceId = 0 }).ToList());
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient
        };

        var log = new List<string>();
        var panel = Assert.Single(EnvironmentBoundaryModel.Build(Input(mesh, environment), log)!.Panels);
        Assert.Equal(PanelShape.CurvedSurface, panel.Shape);
        Assert.Equal(SurfaceArea, panel.Area, 12);
        Assert.Equal(Side, panel.CharacteristicLength, 12);   // its vertical extent
        Assert.Contains(log, line => line.Contains("Curved faces use the vertical-plate correlation"));
    }

    [Fact]
    public void UserConditionsOwnTheirFaces_LeavingTheRestToTheEnvironment()
    {
        var mesh = Cube();
        var conditions = new BoundaryCondition[]
        {
            new FixedTemperature { Name = "Base", FaceIds = new[] { StructuredBoxMesh.FaceZMin }, Kelvin = 350 },
            new Convection
            {
                Name = "Hand-set", FaceIds = new[] { StructuredBoxMesh.FaceXMin },
                Coefficient = 25, AmbientTemperature = Ambient
            }
        };
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient, IncludeRadiation = true
        };
        var input = Input(mesh, environment, conditions: conditions);
        var model = EnvironmentBoundaryModel.Build(input, new List<string>())!;

        Assert.Equal(new[]
        {
            StructuredBoxMesh.FaceXMax, StructuredBoxMesh.FaceYMin,
            StructuredBoxMesh.FaceYMax, StructuredBoxMesh.FaceZMax
        }, model.Panels.Select(p => p.FaceId));

        var film = model.Evaluate(Enumerable.Repeat(360.0, mesh.NodeCount).ToArray());
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            int faceId = mesh.BoundaryTriangles[t].FaceId;
            bool claimed = faceId is StructuredBoxMesh.FaceZMin or StructuredBoxMesh.FaceXMin;
            Assert.Equal(claimed, !film.IsWetted(t));
        }
    }

    [Fact]
    public void FlowLength_FollowsHowEachFaceStandsToTheFlow()
    {
        // Flow along +x over a 100 mm long box. The four faces the stream runs along take
        // their own 100 mm streamwise run; the two faces standing across it are windward
        // and leeward, with their extent across the flow (4A/P = the 40 mm side) as length.
        var mesh = StructuredBoxMesh.Build(0, 0.1, 0, Side, 0, Side, 2, 2, 2);
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.MovingFluid, AmbientTemperature = Ambient,
            CustomFluid = TestFluid(1.0 / 350), FlowVelocity = new Vector3D(3, 0, 0)
        };
        var panels = EnvironmentBoundaryModel.Build(Input(mesh, environment), new List<string>())!
            .Panels.ToDictionary(p => p.FaceId);

        foreach (int face in new[] { StructuredBoxMesh.FaceYMin, StructuredBoxMesh.FaceYMax,
                     StructuredBoxMesh.FaceZMin, StructuredBoxMesh.FaceZMax })
        {
            Assert.Equal(PanelFlowRegime.Parallel, panels[face].FlowRegime);
            Assert.Equal(0.1, panels[face].FlowLength, 12);
        }
        Assert.Equal(PanelFlowRegime.Windward, panels[StructuredBoxMesh.FaceXMin].FlowRegime);
        Assert.Equal(PanelFlowRegime.Leeward, panels[StructuredBoxMesh.FaceXMax].FlowRegime);
        Assert.Equal(Side, panels[StructuredBoxMesh.FaceXMin].FlowLength, 12);
        Assert.Equal(Side, panels[StructuredBoxMesh.FaceXMax].FlowLength, 12);
    }

    /// <summary>Film coefficient on one face of a 100 × 100 mm board of the given thickness
    /// in a 2 m/s stream of the test gas (ν = 1.5e-5 m²/s, Pr = 0.696), no gravity.</summary>
    private static double BoardFaceCoefficient(double thickness, Vector3D flow, int faceId,
        out List<string> log)
    {
        var mesh = StructuredBoxMesh.Build(0, 0.1, 0, 0.1, 0, thickness, 4, 4, 1);
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.MovingFluid, AmbientTemperature = Ambient,
            CustomFluid = TestFluid(1.0 / 350), FlowVelocity = flow,
            Gravity = new Vector3D(0, 0, 0), IncludeRadiation = false
        };
        log = new List<string>();
        var model = EnvironmentBoundaryModel.Build(Input(mesh, environment), log)!;
        var film = model.Evaluate(Enumerable.Repeat(Ambient + 30, mesh.NodeCount).ToArray());
        int triangle = mesh.BoundaryTriangles.Select((t, i) => (t, i)).First(x => x.t.FaceId == faceId).i;
        return film.TriangleFilmCoefficient[triangle];
    }

    [Fact]
    public void BoardBroadsideToTheFlow_TakesTheNormalPlateCorrelation()
    {
        // Air blown AT the broad face of a 1.6 mm board. The published cross-flow plate
        // average (Incropera Table 7.3): Nu = 0.228·Re^0.731·Pr^⅓ on the 100 mm extent.
        // Re = 2·0.1/1.5e-5 = 13 333, so h ≈ 53 W/m²K. The old model ran the
        // parallel-plate result over the 1.6 mm thickness and returned about 135.
        double reynolds = 2.0 * 0.1 / 1.5e-5;
        double prandtl = 1.8e-5 * 1005 / 0.026;
        double published = 0.228 * Math.Pow(reynolds, 0.731) * Math.Cbrt(prandtl) * 0.026 / 0.1;

        var flow = new Vector3D(0, 0, 2);
        double windward = BoardFaceCoefficient(1.6e-3, flow, StructuredBoxMesh.FaceZMin, out var log);
        double leeward = BoardFaceCoefficient(1.6e-3, flow, StructuredBoxMesh.FaceZMax, out _);
        Assert.InRange(windward, 0.75 * published, 1.25 * published);
        Assert.InRange(leeward, 0.75 * published, 1.25 * published);
        Assert.Contains(log, line => line.Contains("facing the flow"));
        Assert.Contains(log, line => line.Contains("in the wake"));
        Assert.Contains(log, line => line.Contains("0.228"));

        // And it is a property of the face, not of the board's thickness.
        double thinner = BoardFaceCoefficient(0.8e-3, flow, StructuredBoxMesh.FaceZMin, out _);
        Assert.InRange(thinner / windward, 0.8, 1.2);
    }

    [Fact]
    public void BoardEdgeOnToTheFlow_KeepsTheParallelPlateResult()
    {
        // The same board with the stream along its broad faces: 0.664·Re^½·Pr^⅓ over the
        // 100 mm run, as before.
        double reynolds = 2.0 * 0.1 / 1.5e-5;
        double prandtl = 1.8e-5 * 1005 / 0.026;
        double published = 0.664 * Math.Sqrt(reynolds) * Math.Cbrt(prandtl) * 0.026 / 0.1;
        double broad = BoardFaceCoefficient(1.6e-3, new Vector3D(2, 0, 0), StructuredBoxMesh.FaceZMax, out _);
        Assert.InRange(broad, 0.99 * published, 1.01 * published);
    }

    [Fact]
    public void StillFluid_LeavesTheForcedLegOut()
    {
        // A still medium must produce exactly the natural-convection coefficient — no
        // blend with a zero-velocity forced term, which would be a different number.
        var mesh = Cube();
        var fluid = TestFluid(1.0 / 350);
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.StillFluid, AmbientTemperature = Ambient,
            CustomFluid = fluid, FlowVelocity = new Vector3D(5, 0, 0), IncludeRadiation = false
        };
        var model = EnvironmentBoundaryModel.Build(Input(mesh, environment), new List<string>())!;
        var film = model.Evaluate(Enumerable.Repeat(Ambient + 40, mesh.NodeCount).ToArray());

        var side = model.Panels.First(p => p.FaceId == StructuredBoxMesh.FaceXMax);
        var state = fluid.AtTemperature(Ambient + 20);
        double rayleigh = ConvectionCorrelations.Rayleigh(state, 9.80665, 40, Side);
        double expected = ConvectionCorrelations.NaturalVerticalPlate(rayleigh, state.Prandtl)
                          * state.ThermalConductivity / Side;

        int triangle = mesh.BoundaryTriangles.Select((t, i) => (t, i))
            .First(x => x.t.FaceId == StructuredBoxMesh.FaceXMax).i;
        Assert.Equal(expected, film.TriangleFilmCoefficient[triangle], 12);
        Assert.Equal(Side, side.CharacteristicLength, 12);
    }

    // ---------------------------------------------------------------- helpers

    private static double FrameMean(ResultFrame frame) =>
        ((NodalScalarField)frame.Fields.First(f => f.Name == "Temperature")).Values.Average();

    /// <summary>Classical RK4 on a scalar ODE — the independent oracle for the lumped gates.</summary>
    private static double Rk4(double value, double duration, double step, Func<double, double> rate)
    {
        int steps = (int)Math.Round(duration / step);
        for (int i = 0; i < steps; i++)
        {
            double k1 = rate(value);
            double k2 = rate(value + 0.5 * step * k1);
            double k3 = rate(value + 0.5 * step * k2);
            double k4 = rate(value + step * k3);
            value += step * (k1 + 2 * k2 + 2 * k3 + k4) / 6;
        }
        return value;
    }
}
