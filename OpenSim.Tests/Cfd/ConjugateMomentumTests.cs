using OpenSim.Cfd;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Tests.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Cfd;

/// <summary>
/// FU-43: a transient whose flow the heating drives, and the steady gate at a grid that
/// resolves the thermal boundary layer.
/// </summary>
public class ConjugateMomentumTests
{
    private readonly ITestOutputHelper _out;
    public ConjugateMomentumTests(ITestOutputHelper output) => _out = output;

    private const double Ambient = 293.15, Power = 5.0;

    /// <summary>A 0.2 m heated cube, x from 0.4 to 0.6 and y, z from <paramref name="low"/>
    /// to low + 0.2.</summary>
    private static (SolveInput Input, IReadOnlyList<int> NodeBases) HeatedBlock(
        TransientThermalSettings? transient = null, double low = 0.15)
    {
        var material = StructuredBoxMesh.Conductor("aluminium-ish", 200);
        var body = StructuredBoxMesh.Box("block", 0.4, 0.6, low, low + 0.2, low, low + 0.2, 2, 2, 2, material);
        var assembled = FeMeshAssembler.Assemble(new[] { body });
        double volume = assembled.Mesh.TotalVolume();
        return (new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = material,
            BoundaryConditions = assembled.BoundaryConditions,
            RegionMaterials = assembled.RegionMaterials,
            ElementHeatSource = Enumerable.Repeat(Power / volume, assembled.Mesh.ElementCount).ToArray(),
            TransientThermal = transient
        }, assembled.NodeBases);
    }

    private static double MeanRise(SolveOutput output) =>
        ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values.Average() - Ambient;

    // ---------------------------------------------------------------- buoyant transient

    /// <summary>A still, viscous test gas under gravity (Pr 0.7, ν = 4.5e-4 m²/s): the
    /// block's own plume is the only flow, Ra about 10³ on the block — laminar and steady.</summary>
    private static EnvironmentSettings StillGas() => new()
    {
        Medium = MediumKind.StillFluid,
        AmbientTemperature = Ambient,
        CustomFluid = FluidProperties.Constant("viscous test gas",
            density: 1.2, dynamicViscosity: 5.4e-4, thermalConductivity: 0.78,
            specificHeat: 1005, thermalExpansion: 3.4e-3),
        IncludeRadiation = false,
        Gravity = new Vector3D(0, 0, -9.81)
    };

    private static CfdSettings OpenBox(bool momentum) =>
        CfdSettings.ForExternalFlow(new Vector3D(0, 0, 0)) with
        {
            // 10 × 6 × 8 cells, the block two cells across and on cell boundaries: coarse,
            // but what is tested is that two formulations reach one fixed point, which
            // holds on any grid.
            DomainBox = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1.0, 0.6, 0.8)),
            CellSize = 0.1,
            SteadyTolerance = 1e-6,
            MaxSteps = 20000,
            TransientMomentum = momentum
        };

    [Fact]
    public void BuoyantTransient_WithTheFlowMarched_LandsOnTheSteadyPlume_TheFrozenFlowCannot()
    {
        // At t = 0 the gas is at rest and uniform, so the frozen-momentum mode holds a flow
        // of zero: the block can only conduct into still gas and settles far hotter. With
        // the momentum marched, the plume develops as the block warms, and held for many
        // time constants the transient must land on the steady conjugate answer — the
        // same fixed point by two different formulations.
        var environment = StillGas();
        var (steadyInput, bases) = HeatedBlock(low: 0.2);
        var steady = ConjugateHeatStudy.Run(steadyInput, bases, environment, OpenBox(false));
        double steadyRise = MeanRise(steady.Thermal);
        double tau = 1000 * 500 * 0.008 * steadyRise / Power;   // C·ΔT/P for the lumped block
        _out.WriteLine($"steady plume: rise {steadyRise:F4} K, peak gas speed {steady.Flow.MaxSpeed():G3} m/s; τ ≈ {tau:G4} s");

        var transient = new TransientThermalSettings { InitialTemperature = Ambient, Duration = 12 * tau, TimeStep = 2 * tau };
        var (marchedInput, b1) = HeatedBlock(transient, low: 0.2);
        var marched = ConjugateHeatStudy.Run(marchedInput, b1, environment, OpenBox(true));
        var (frozenInput, b2) = HeatedBlock(transient, low: 0.2);
        var frozen = ConjugateHeatStudy.Run(frozenInput, b2, environment, OpenBox(false));
        double marchedRise = MeanRise(marched.Thermal), frozenRise = MeanRise(frozen.Thermal);
        _out.WriteLine($"after 12 τ: marched flow {marchedRise:F4} K ({marchedRise / steadyRise:F4} of steady), " +
                       $"frozen flow {frozenRise:F4} K; peak gas speed marched {marched.Flow.MaxSpeed():G3} m/s, frozen {frozen.Flow.MaxSpeed():G3}");
        foreach (var line in marched.Thermal.Log.Where(l => l.StartsWith("Conjugate transient"))) _out.WriteLine(line);

        Assert.InRange(marchedRise / steadyRise, 0.97, 1.03);
        Assert.True(frozenRise > 1.2 * marchedRise, "the frozen still gas should leave the block much hotter");
        Assert.True(frozen.Flow.MaxSpeed() < 1e-9);
        Assert.True(marched.Flow.MaxSpeed() > 0.5 * steady.Flow.MaxSpeed());
        Assert.Contains(marched.Thermal.Log, l => l.Contains("momentum and energy marched"));
    }

    // ---------------------------------------------------------------- the six-cell gate

    [Fact]
    public void SteadyExchange_WithTheThermalLayerResolved_SettlesAndBalances()
    {
        // The audit's gate (F-04): a heated block in a stream at a cell size giving at least
        // six cells across the thermal boundary layer converges in at most 30 outer
        // iterations and the stream carries the dissipated power within 0.5 %. What makes a
        // resolved layer hard for the outer loop is the wall conductance 2k/h against the
        // film k/δ — their ratio is 2δ/h, the cells across the layer, whatever the scale. So
        // the layer is made thick (a test gas twenty times as conductive as air, Pe ≈ 5 on
        // the block) and the grid coarse (6 000 cells), rather than the layer thin and the
        // grid fine (32 cells across the stream, 98 000 cells, did not finish in three
        // hours). The layer is measured on the solution itself: the fluid cells above the
        // block's top face, over its trailing edge, out to where the excess falls below 1 %
        // of the first cell's — and that edge must lie inside the domain, so the count is
        // the layer's and not the box's.
        var (input, bases) = HeatedBlock();
        var environment = new EnvironmentSettings
        {
            Medium = MediumKind.MovingFluid,
            AmbientTemperature = Ambient,
            FlowVelocity = new Vector3D(0.01, 0, 0),
            CustomFluid = FluidProperties.Constant("conductive test gas",
                density: 1.2, dynamicViscosity: 1.8e-5, thermalConductivity: 0.52,
                specificHeat: 1005, thermalExpansion: 3.4e-3),
            IncludeRadiation = false,
            Gravity = new Vector3D(0, 0, 0)
        };
        var cfd = CfdSettings.ForExternalFlow(new Vector3D(0.01, 0, 0)) with
        {
            // 30 × 10 × 20 cells, the block on cell boundaries; 0.65 m of gas above it.
            DomainBox = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1.5, 0.5, 1.0)),
            CellSize = 0.05,
            SteadyTolerance = 1e-6,
            MaxSteps = 60000
        };
        var result = ConjugateHeatStudy.Run(input, bases, environment, cfd);

        var grid = result.Flow.Grid;
        double h = grid.H;
        int i = (int)Math.Floor((0.6 - grid.Origin.X) / h) - 1;   // last column over the block
        int j = (int)Math.Floor((0.25 - grid.Origin.Y) / h);
        int k0 = (int)Math.Ceiling((0.35 - grid.Origin.Z) / h - 1e-9);
        double first = result.Flow.Temperature![grid.CellIndex(i, j, k0)] - Ambient;
        int across = 0;
        bool ended = false;
        for (int k = k0; k < grid.Nz; k++)
        {
            if (result.Flow.Temperature[grid.CellIndex(i, j, k)] - Ambient < 0.01 * first) { ended = true; break; }
            across++;
        }
        var settled = result.Thermal.Log.First(l => l.StartsWith("Conjugate exchange settled in"));
        int outer = int.Parse(settled.Split(' ')[4]);
        double carried = result.Outlets!.HeatRemoved;
        _out.WriteLine($"h = {h * 1e3:F2} mm: {across} cells across the thermal layer above the trailing edge " +
                       $"(first cell {first:F4} K over ambient; edge inside the domain: {ended}); " +
                       $"{outer} outer iterations; the stream carries {carried:F4} W of {Power} W");
        _out.WriteLine(settled);
        Assert.True(ended, "the 1 % edge of the layer is not inside the domain");
        Assert.True(across >= 6, $"only {across} cells across the thermal layer");
        Assert.True(outer <= 30, $"{outer} outer iterations");
        Assert.True(Math.Abs(carried - Power) <= 0.005 * Power, $"the stream carries {carried:F4} W of {Power} W");
    }
}
