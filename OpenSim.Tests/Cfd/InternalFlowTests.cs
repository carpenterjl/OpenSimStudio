using OpenSim.Cfd;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Tests.Solvers;

namespace OpenSim.Tests.Cfd;

/// <summary>
/// Flow THROUGH a passage bored in a solid — the heat-exchanger topology, as opposed to
/// the external flow AROUND a body every earlier CFD gate covers. The claims here are the
/// ones the reproduction rests on: a hollow body classifies its bore as fluid, an opening
/// carries its own stream temperature into BOTH the diffusive and the advective terms, the
/// Reynolds guard measures the passage rather than the block, and the port ledger closes.
/// </summary>
public class InternalFlowTests
{
    // A 12×6×6-cell block of 10 mm cells with a 2×2-cell bore along x.
    private const double H = 0.01;
    private const int Nx = 12, Ny = 6, Nz = 6;
    private const int J0 = 2, J1 = 4, K0 = 2, K1 = 4;

    private static FeMesh HollowBlock() => StructuredHollowBlock.Build(Nx, Ny, Nz, H, J0, J1, K0, K1);

    private static CfdSettings.ResolvedGrid BlockGrid() => new(
        new Aabb(new Vector3D(0, 0, 0), new Vector3D(Nx * H, Ny * H, Nz * H)),
        H, Nx, Ny, Nz, Array.Empty<string>());

    private static FluidProperties TestWater() => FluidProperties.Constant("test water",
        density: 1000, dynamicViscosity: 1e-3, thermalConductivity: 0.6,
        specificHeat: 4000, thermalExpansion: 2e-4);

    // ------------------------------------------------------------------ voxelization

    [Fact]
    public void AHollowBody_ClassifiesItsBoreAsFluid_AndWallsMapToTheInnerSurface()
    {
        // The bore is 2×2 cells over the full 12-cell length: an exact count, because the
        // FE skin is aligned to the same lattice the grid uses.
        var mesh = HollowBlock();
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, BlockGrid());

        Assert.Equal((J1 - J0) * (K1 - K0) * Nx, domain.FluidCellCount);
        Assert.Equal(Nx * Ny * Nz - domain.FluidCellCount, domain.SolidCellCounts[0]);

        // Every wall face must map to a BORE triangle: the fluid only ever touches the
        // inner surface, so a face mapped to an outer face id would mean the nearest-
        // centroid map jumped through the metal.
        Assert.NotEmpty(domain.WallFaces);
        foreach (var wf in domain.WallFaces)
            Assert.Equal(StructuredHollowBlock.FaceBore,
                mesh.BoundaryTriangles[wf.BoundaryTriangle].FaceId);

        // The bore is open at both ends, so the wall faces are the four sides only:
        // 4 sides × 12 cells long × 2 cells wide.
        Assert.Equal(4 * Nx * (J1 - J0), domain.WallFaces.Count);
    }

    [Fact]
    public void AHollowBody_ConservesMassThroughTheBore()
    {
        var mesh = HollowBlock();
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, BlockGrid());
        var settings = Channel(0.05, out _);

        var flow = new IncompressibleFlowSolver(domain, settings, TestWater().AtTemperature(300))
            .SolveSteady();

        Assert.True(flow.MaxDivergence < 1e-9,
            $"max |div u| = {flow.MaxDivergence:G3} 1/s");
        Assert.Equal(flow.InflowRate, flow.OutflowRate, 12);
        // Continuity across the bore: the open area is the bore's cross-section exactly.
        double area = (J1 - J0) * (K1 - K0) * H * H;
        Assert.Equal(0.05 * area, flow.InflowRate, 10);
    }

    // ------------------------------------------------------------------ Reynolds scale

    [Fact]
    public void InternalRegime_MeasuresThePassage_NotTheBlock()
    {
        // The block is 6× the bore, so an external-length Reynolds number reads 6× high.
        // With the bore this case is laminar; with the block it would be refused — and
        // refusing a laminar case is exactly the failure the regime switch exists to stop.
        var mesh = HollowBlock();
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, BlockGrid());
        var water = TestWater().AtTemperature(300);

        var settings = Channel(0.2, out _);
        var internalSolver = new IncompressibleFlowSolver(domain, settings, water);
        string internalNote = internalSolver.Notes.First(n => n.StartsWith("Re ="));

        // 4·V/A of a 20×20 mm square bore is 20 mm exactly — the textbook hydraulic
        // diameter, and here the staircase IS the geometry, so it is exact.
        Assert.Contains("L = 0.02", internalNote);

        // The same case declared external refuses: the block's own 120 mm length is not
        // the scale of a flow that never touches its outside.
        var external = settings with { Regime = FlowRegime.External };
        var refusal = Assert.Throws<InvalidOperationException>(
            () => new IncompressibleFlowSolver(domain, external, water));
        Assert.Contains("laminar band", refusal.Message);
    }

    // ------------------------------------------------------------------ inlet temperature

    [Fact]
    public void AnOpeningsTemperature_ReachesBothTheDiffusiveAndTheAdvectiveTerm()
    {
        // A hot stream through an ADIABATIC bore must arrive, fill and leave at exactly
        // the inlet temperature. The advective flux term is what carries it down the
        // channel and it used to read the FACE temperature, which no opening can set —
        // so before this was wired, this test would have found 300 K everywhere.
        var mesh = HollowBlock();
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, BlockGrid());
        var settings = Channel(0.05, out _);
        var water = TestWater().AtTemperature(300);
        var thermal = new FlowThermalOptions { AmbientTemperature = 300 };

        var flow = new IncompressibleFlowSolver(domain, settings, water, thermal: thermal)
            .SolveSteady();          // null wall temperatures = adiabatic walls

        var grid = flow.Grid;
        for (int k = 0; k < grid.Nz; k++)
            for (int j = 0; j < grid.Ny; j++)
                for (int i = 0; i < grid.Nx; i++)
                {
                    if (!grid.IsFluid(i, j, k)) continue;
                    // The march tolerance is RELATIVE to the imposed span (1e-7 x 50 K),
                    // so this is the converged answer to the last bit the solve claims.
                    double t = flow.Temperature![grid.CellIndex(i, j, k)];
                    Assert.True(Math.Abs(t - 350) < 1e-4,
                        $"cell ({i},{j},{k}) reads {t:F6} K, not the inlet's 350 K");
                }

        var ledger = FlowOutletReporter.Build(flow, settings, 1000, 4000);
        var outlet = Assert.Single(ledger.Outlets);
        var inlet = Assert.Single(ledger.Inlets);
        Assert.True(Math.Abs(outlet.MixedTemperature - 350) < 1e-4);
        Assert.True(Math.Abs(inlet.MixedTemperature - 350) < 1e-4);
        // Adiabatic: what came in left again, so the enthalpy balance is zero.
        Assert.True(Math.Abs(ledger.HeatRemoved) < 1e-6 * Math.Abs(inlet.MassFlow * 4000 * 350),
            $"adiabatic bore carried {ledger.HeatRemoved:G4} W");
    }

    [Fact]
    public void ColdWalls_CoolTheStream_AndTheEnthalpyLedgerMatchesTheWallHeat()
    {
        // The identity the whole heat-exchanger workflow rests on: whatever enthalpy the
        // stream loses between the ports, the walls must have taken. Both sides are
        // computed from the SAME converged field but by different sums — the ledger over
        // the open boundary, the wall term over the fluid–solid faces — so agreement is a
        // real check of the conservative flux form, not a tautology.
        var mesh = HollowBlock();
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, BlockGrid());
        var settings = Channel(0.02, out _);
        var props = TestWater();
        var water = props.AtTemperature(300);
        var thermal = new FlowThermalOptions { AmbientTemperature = 300 };

        var wallT = new double[domain.WallFaces.Count];
        Array.Fill(wallT, 300.0);                       // cold bore wall
        var flow = new IncompressibleFlowSolver(domain, settings, water, thermal: thermal)
            .SolveSteady(wallT);

        var ledger = FlowOutletReporter.Build(flow, settings, water.Density, water.SpecificHeat);
        var outlet = Assert.Single(ledger.Outlets);
        Assert.True(outlet.MixedTemperature < 350 - 1,
            $"a cold wall must cool the stream, got {outlet.MixedTemperature:F2} K");
        Assert.True(outlet.MixedTemperature > 300,
            $"the stream cannot fall below the wall, got {outlet.MixedTemperature:F2} K");

        // Wall heat, summed the other way: g·A·(T_wall − T_cell) over every wall face.
        double g = water.ThermalConductivity / (flow.Grid.H / 2);
        double faceArea = flow.Grid.H * flow.Grid.H;
        double wallHeat = 0;
        for (int w = 0; w < domain.WallFaces.Count; w++)
            wallHeat += g * faceArea *
                        (wallT[w] - flow.Temperature![domain.WallFaces[w].FluidCell]);

        // Both are the same signed quantity: net enthalpy the stream GAINED between the
        // ports, and net heat the WALLS gave it. A cold wall makes both negative.
        Assert.True(Math.Abs(ledger.HeatRemoved - wallHeat) <= 0.01 * Math.Abs(wallHeat),
            $"stream gained {ledger.HeatRemoved:G5} W, walls gave {wallHeat:G5} W");
        Assert.True(wallHeat < 0, "a cold wall takes heat OUT of the stream");
    }

    // ------------------------------------------------------------------ domain rules

    [Fact]
    public void AnInternalDomain_MayBeASubsetOfTheSolid_ButNeverDisjointFromIt()
    {
        var solid = new Aabb(new Vector3D(0, 0, 0), new Vector3D(0.3, 0.2, 0.05));
        var inside = new Aabb(new Vector3D(0, 0.01, 0.015), new Vector3D(0.3, 0.19, 0.035));

        var settings = new CfdSettings { DomainBox = inside, CellSize = 0.005 };
        var resolved = settings.ResolveGrid(solid, new Vector3D(0, 0, 0));
        Assert.Equal(inside.Min.X, resolved.Domain.Min.X, 12);
        Assert.Contains(resolved.Notes, n => n.Contains("% of"));

        var elsewhere = new CfdSettings
        {
            DomainBox = new Aabb(new Vector3D(1, 1, 1), new Vector3D(2, 2, 2)),
            CellSize = 0.005
        };
        var failure = Assert.Throws<InvalidOperationException>(
            () => elsewhere.ResolveGrid(solid, new Vector3D(0, 0, 0)));
        Assert.Contains("does not overlap", failure.Message);
    }

    [Fact]
    public void ForInternalFlow_WallsEveryFace_AndRefusesASealedPassage()
    {
        var domain = new Aabb(new Vector3D(0, 0, 0), new Vector3D(0.1, 0.05, 0.05));
        var opening = new FlowOpening
        {
            Face = BoxFace.XMin, UMin = 0, UMax = 0.05, VMin = 0, VMax = 0.05,
            Kind = FlowFaceKind.InletVelocity, Velocity = new Vector3D(0.1, 0, 0)
        };
        var settings = CfdSettings.ForInternalFlow(domain, new[] { opening }, "Water");

        foreach (var face in new[] { BoxFace.XMin, BoxFace.XMax, BoxFace.YMin, BoxFace.YMax,
                     BoxFace.ZMin, BoxFace.ZMax })
            Assert.Equal(FlowFaceKind.Wall, settings.FaceKind(face));
        Assert.Equal(FlowRegime.Internal, settings.Regime);
        Assert.False(settings.ResolvesSurroundings);

        var failure = Assert.Throws<InvalidOperationException>(
            () => CfdSettings.ForInternalFlow(domain, Array.Empty<FlowOpening>(), "Water"));
        Assert.Contains("at least one opening", failure.Message);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An internal-flow case through the bore: inlet on XMin at 350 K, outlet on
    /// XMax, every other face a wall.</summary>
    private static CfdSettings Channel(double speed, out FlowOpening inlet)
    {
        double lo = J0 * H, hi = J1 * H;
        inlet = new FlowOpening
        {
            Face = BoxFace.XMin, UMin = lo, UMax = hi, VMin = lo, VMax = hi,
            Kind = FlowFaceKind.InletVelocity, Velocity = new Vector3D(speed, 0, 0),
            Temperature = 350
        };
        var outlet = new FlowOpening
        {
            Face = BoxFace.XMax, UMin = lo, UMax = hi, VMin = lo, VMax = hi,
            Kind = FlowFaceKind.OutletPressure
        };
        return CfdSettings.ForInternalFlow(
            new Aabb(new Vector3D(0, 0, 0), new Vector3D(Nx * H, Ny * H, Nz * H)),
            new[] { inlet, outlet }, "test water",
            new CfdSettings { CellSize = H, SteadyTolerance = 1e-7, MaxSteps = 20000 });
    }
}
