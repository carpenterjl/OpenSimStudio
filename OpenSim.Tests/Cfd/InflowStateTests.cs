using System.Globalization;
using System.Text.RegularExpressions;
using OpenSim.Cfd;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Tests.Solvers;

namespace OpenSim.Tests.Cfd;

/// <summary>
/// The state the conjugate study evaluates the working fluid at (D11). It used to be the
/// SURROUNDINGS' ambient — the air around a water circuit — so water arriving at 363 K
/// was given the ν of water at 300 K (2.6× too high) and the Reynolds guard reported a
/// laminar passage that was not. The rule now: the inflow-weighted stream temperature,
/// in every fluid-selection mode, falling back to the ambient only when nothing is
/// prescribed to flow in. The precise claims: the weighting counts only the inward
/// normal flow through FLUID faces; one stream temperature comes back bitwise; no inflow
/// is the ambient bitwise; and the study's log, its ν and its Reynolds line all quote
/// the same state.
/// </summary>
public class InflowStateTests
{
    // ---------------------------------------------------------------- the weighting

    private static FlowOpening Inlet(BoxFace face, double uMin, double uMax, double vMin,
        double vMax, Vector3D velocity, double? temperature) => new()
    {
        Face = face, UMin = uMin, UMax = uMax, VMin = vMin, VMax = vMax,
        Kind = FlowFaceKind.InletVelocity, Velocity = velocity, Temperature = temperature
    };

    [Fact]
    public void TheTemperature_IsWeightedByInwardFlowThroughFluidFacesOnly()
    {
        // 4×4×4 unit box, h = 0.25. On XMin (u = y, v = z) two inlet openings: the lower
        // half at 0.2 m/s and 350 K, the upper half at 0.1 m/s and 310 K. Four of the
        // upper opening's eight cells are SOLID, so only four of its faces carry flow.
        // Also present, and all of them must carry NOTHING: a YMax inlet FACE whose
        // uniform velocity is tangential (a lid), an XMax inlet opening whose velocity
        // points OUT of the domain, and a ZMax pressure outlet.
        var grid = new CartesianGrid(4, 4, 4, new Vector3D(0, 0, 0), 0.25);
        for (int j = 0; j < 4; j++) grid.CellBody[grid.CellIndex(0, j, 3)] = 0;   // solid row

        var settings = new CfdSettings
        {
            YMaxFace = FlowFaceKind.InletVelocity, InletVelocity = new Vector3D(0.3, 0, 0),
            ZMaxFace = FlowFaceKind.OutletPressure,
            Openings = new[]
            {
                Inlet(BoxFace.XMin, 0, 1, 0, 0.5, new Vector3D(0.2, 0, 0), 350),
                Inlet(BoxFace.XMin, 0, 1, 0.5, 1, new Vector3D(0.1, 0, 0), 310),
                Inlet(BoxFace.XMax, 0, 1, 0, 1, new Vector3D(0.4, 0, 0), 400)   // outward
            }
        };
        var thermal = new FlowThermalOptions { AmbientTemperature = 300 };

        var state = InflowState.PropertyTemperature(grid, settings, thermal);

        // Lower opening: 8 faces × 0.2 m/s; upper: 4 fluid faces × 0.1 m/s.
        double area = 0.25 * 0.25;
        double qLow = 8 * 0.2 * area, qHigh = 4 * 0.1 * area;
        Assert.True(state.FromInflow);
        Assert.Equal(12, state.InletFaces);
        Assert.True(Math.Abs(state.InflowRate - (qLow + qHigh)) <= 1e-12 * (qLow + qHigh),
            $"inflow {state.InflowRate:R} vs {qLow + qHigh:R}");
        double expected = (qLow * 350 + qHigh * 310) / (qLow + qHigh);
        Assert.True(Math.Abs(state.Temperature - expected) <= 1e-9,
            $"T = {state.Temperature:R} vs {expected:R}");
        Assert.Equal(310, state.Minimum);
        Assert.Equal(350, state.Maximum);
        Assert.Contains("inflow-weighted", state.Describe());
    }

    [Fact]
    public void NoPrescribedInflow_IsTheAmbient_Bitwise()
    {
        // Three cases with nothing flowing in by prescription: a sealed cavity, a still
        // box open to ambient on every face, and a lid-driven cavity (a tangential
        // inlet face). Every one must hand back the ambient EXACTLY — these are the
        // cases whose properties must not move by a bit.
        var grid = new CartesianGrid(3, 3, 3, new Vector3D(0, 0, 0), 0.1);
        var thermal = new FlowThermalOptions { AmbientTemperature = 293.15 };

        foreach (var settings in new[]
                 {
                     new CfdSettings(),
                     CfdSettings.ForExternalFlow(new Vector3D(0, 0, 0)),
                     new CfdSettings
                     {
                         YMaxFace = FlowFaceKind.InletVelocity, InletVelocity = new Vector3D(1, 0, 0)
                     }
                 })
        {
            var state = InflowState.PropertyTemperature(grid, settings, thermal);
            Assert.False(state.FromInflow);
            Assert.Equal(0, state.InletFaces);
            Assert.Equal(293.15, state.Temperature);          // exact, not approximate
            Assert.Contains("ambient", state.Describe());
        }
    }

    [Fact]
    public void OneStreamTemperature_ComesBackExactly_WithTheEnergyEquationsPrecedence()
    {
        // The precedence the energy equation uses at an inlet face: the opening's own
        // temperature, else the face's explicit temperature, else the ambient. And a
        // single stream temperature is returned as ITSELF — not as a weighted quotient
        // that could round away from it — so "same inlet temperature as before" means
        // bitwise the same fluid state.
        var grid = new CartesianGrid(5, 3, 3, new Vector3D(0, 0, 0), 0.01);
        var external = CfdSettings.ForExternalFlow(new Vector3D(0.05, 0, 0));

        // Face inlet, no temperature anywhere → ambient (this is every existing
        // external-flow gate).
        var ambientOnly = InflowState.PropertyTemperature(grid, external,
            new FlowThermalOptions { AmbientTemperature = 293.15 });
        Assert.True(ambientOnly.FromInflow);
        Assert.Equal(9, ambientOnly.InletFaces);
        Assert.Equal(293.15, ambientOnly.Temperature);

        // Face inlet with an explicit face temperature → that temperature.
        var faceT = InflowState.PropertyTemperature(grid, external,
            new FlowThermalOptions { AmbientTemperature = 293.15, XMinTemperature = 320 });
        Assert.Equal(320, faceT.Temperature);

        // An opening's stream temperature wins over both.
        var opening = external with
        {
            Openings = new[] { Inlet(BoxFace.XMin, 0, 0.03, 0, 0.03, new Vector3D(0.05, 0, 0), 363.15) }
        };
        var streamT = InflowState.PropertyTemperature(grid, opening,
            new FlowThermalOptions { AmbientTemperature = 293.15, XMinTemperature = 320 });
        Assert.Equal(363.15, streamT.Temperature);
        Assert.Equal(363.15, streamT.Minimum);
        Assert.Equal(363.15, streamT.Maximum);
    }

    // ---------------------------------------------------------------- the study

    private const double H = 0.01;
    private const int Nx = 12, Ny = 6, Nz = 6;
    private const int J0 = 2, J1 = 4, K0 = 2, K1 = 4;

    private static (SolveInput Input, IReadOnlyList<int> Bases) HollowBlockInput(double power)
    {
        var material = StructuredBoxMesh.Conductor("copper-ish", 400) with { Emissivity = 0.15 };
        var body = StructuredHollowBlock.Body("block", material, Nx, Ny, Nz, H, J0, J1, K0, K1);
        var assembled = FeMeshAssembler.Assemble(new[] { body });
        double volume = assembled.Mesh.TotalVolume();
        return (new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = body.Material!,
            RegionMaterials = assembled.RegionMaterials,
            BoundaryConditions = assembled.BoundaryConditions,
            ElementHeatSource = Enumerable.Repeat(power / volume, assembled.Mesh.ElementCount).ToArray()
        }, assembled.NodeBases);
    }

    /// <summary>The library-water circuit through the bore: the in-repo analogue of the
    /// heat-exchanger reproduction — water in at <paramref name="inletT"/>, block in air.</summary>
    private static CfdSettings WaterCircuit(double speed, double inletT)
    {
        double lo = J0 * H, hi = J1 * H;
        var inlet = Inlet(BoxFace.XMin, lo, hi, lo, hi, new Vector3D(speed, 0, 0), inletT);
        var outlet = new FlowOpening
        {
            Face = BoxFace.XMax, UMin = lo, UMax = hi, VMin = lo, VMax = hi,
            Kind = FlowFaceKind.OutletPressure
        };
        return CfdSettings.ForInternalFlow(
            new Aabb(new Vector3D(0, 0, 0), new Vector3D(Nx * H, Ny * H, Nz * H)),
            new[] { inlet, outlet }, "Water",
            new CfdSettings { CellSize = H, SteadyTolerance = 1e-6, MaxSteps = 20000 });
    }

    private static EnvironmentSettings StillAir(double ambient) => new()
    {
        Medium = MediumKind.StillFluid,
        AmbientTemperature = ambient,
        FluidName = "Air",
        Gravity = new Vector3D(0, -9.80665, 0),
        IncludeRadiation = false
    };

    /// <summary>The ν a log line quotes ("ν = 3.34E-07 m²/s").</summary>
    private static double LoggedViscosity(string line)
    {
        var m = Regex.Match(line, @"ν = ([0-9.]+E[+-]?[0-9]+|[0-9.]+) m²/s");
        Assert.True(m.Success, $"no ν on: {line}");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void AnInternalWaterCircuit_TakesItsPropertiesFromTheInletStream_NotTheAirOutside()
    {
        // Water in at 363 K through a block sitting in 300 K air. The logged ν must be
        // the library's 363 K value (within the 3 significant figures the log prints),
        // and the Reynolds line must quote the SAME ν — before this, both quoted water
        // at 300 K, 2.6× too viscous, and the Reynolds number read 2.6× too low.
        var (input, bases) = HollowBlockInput(40.0);
        var result = ConjugateHeatStudy.Run(input, bases, StillAir(300), WaterCircuit(0.1, 363));

        string fluidLine = result.Thermal.Log.Single(l => l.StartsWith("Conjugate heat:"));
        Assert.Contains("Water properties at inflow T = 363.00 K", fluidLine);

        double expected = FluidLibrary.Water.AtTemperature(363).KinematicViscosity;
        double wrong = FluidLibrary.Water.AtTemperature(300).KinematicViscosity;
        double logged = LoggedViscosity(fluidLine);
        Assert.True(Math.Abs(logged - expected) <= 0.01 * expected,
            $"logged ν = {logged:G3}, library 363 K value {expected:G3}");
        Assert.True(Math.Abs(logged - wrong) > 0.5 * wrong, "still the 300 K viscosity");

        string reLine = result.Thermal.Log.Single(l => l.StartsWith("Re ="));
        Assert.Equal(logged, LoggedViscosity(reLine));
        // Re = U·D_h/ν on the 20 mm bore: 0.1 × 0.02 / 3.34e-7 ≈ 6.0e3 — above the
        // laminar band, and SAID: the solver's own warning line must be in the log.
        Assert.Contains(result.Thermal.Log, l => l.StartsWith("WARNING: Re ="));
        Assert.Contains("L = 0.02", reLine);
    }

    [Fact]
    public void AnExternalFlowWithAHotInletOpening_TakesItsPropertiesFromTheOpening()
    {
        // The other mode: the CFD resolves the surroundings themselves (no named fluid),
        // yet the inlet opening carries 350 K into a 300 K ambient. "Ambient" was wrong
        // here too — the properties belong to the 350 K stream.
        var material = StructuredBoxMesh.Conductor("aluminium-ish", 200);
        var body = StructuredBoxMesh.Box("block", 0.4, 0.6, 0.15, 0.35, 0.15, 0.35, 2, 2, 2, material);
        var assembled = FeMeshAssembler.Assemble(new[] { body });
        double volume = assembled.Mesh.TotalVolume();
        var input = new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = material,
            BoundaryConditions = assembled.BoundaryConditions,
            RegionMaterials = assembled.RegionMaterials,
            ElementHeatSource = Enumerable.Repeat(5.0 / volume, assembled.Mesh.ElementCount).ToArray()
        };
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.MovingFluid,
            AmbientTemperature = 300,
            FlowVelocity = new Vector3D(0.01, 0, 0),
            FluidName = "Air",
            IncludeRadiation = false,
            Gravity = new Vector3D(0, 0, 0)
        };
        var cfd = CfdSettings.ForExternalFlow(new Vector3D(0.01, 0, 0)) with
        {
            DomainBox = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1.5, 0.5, 0.5)),
            CellSize = 0.5 / 6,
            SteadyTolerance = 1e-5,
            MaxSteps = 30000,
            Openings = new[] { Inlet(BoxFace.XMin, 0, 0.5, 0, 0.5, new Vector3D(0.01, 0, 0), 350) }
        };
        Assert.True(cfd.ResolvesSurroundings);

        var result = ConjugateHeatStudy.Run(input, assembled.NodeBases, environment, cfd);

        string fluidLine = result.Thermal.Log.Single(l => l.StartsWith("Conjugate heat:"));
        Assert.Contains("Air properties at inflow T = 350.00 K", fluidLine);
        double expected = FluidLibrary.Air.AtTemperature(350).KinematicViscosity;
        double logged = LoggedViscosity(fluidLine);
        Assert.True(Math.Abs(logged - expected) <= 0.01 * expected,
            $"logged ν = {logged:G3}, library 350 K value {expected:G3}");
        Assert.Equal(logged, LoggedViscosity(result.Thermal.Log.Single(l => l.StartsWith("Re ="))));
    }

    [Fact]
    public void AStreamOutsideThePropertyTable_IsSaid()
    {
        // Library water stops at 370 K. A 380 K stream clamps to the end row — and the
        // log must say so rather than silently use it.
        var (input, bases) = HollowBlockInput(40.0);
        var result = ConjugateHeatStudy.Run(input, bases, StillAir(300), WaterCircuit(0.02, 380));

        Assert.Contains(result.Thermal.Log, l => l.StartsWith("WARNING: 380.00 K lies outside the Water property table"));
        double logged = LoggedViscosity(result.Thermal.Log.Single(l => l.StartsWith("Conjugate heat:")));
        double endRow = FluidLibrary.Water.AtTemperature(370).KinematicViscosity;
        Assert.True(Math.Abs(logged - endRow) <= 0.01 * endRow);
    }
}
