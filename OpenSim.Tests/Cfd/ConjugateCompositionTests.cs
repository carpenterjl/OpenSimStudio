using OpenSim.Cfd;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Tests.Solvers;

namespace OpenSim.Tests.Cfd;

/// <summary>
/// What happens on the skin the flow does NOT wet. When the CFD resolves the surroundings
/// themselves (external flow) there is nothing left for a correlation to add and only
/// radiation joins the film — the path every earlier gate pins. When it resolves an
/// INTERNAL circuit, the block still sits in air, and that air is still unresolved: the
/// Stage 1 correlations must carry every unwetted triangle, or the outer surface would be
/// silently adiabatic and the block would just accumulate heat.
/// </summary>
public class ConjugateCompositionTests
{
    private const double H = 0.01;
    private const int Nx = 12, Ny = 6, Nz = 6;
    private const int J0 = 2, J1 = 4, K0 = 2, K1 = 4;

    private static Material Copperish() => StructuredBoxMesh.Conductor("copper-ish", 400)
        with { Emissivity = 0.15 };

    private static (SolveInput Input, IReadOnlyList<int> Bases) HollowBlockInput(double power)
    {
        var body = StructuredHollowBlock.Body("block", Copperish(), Nx, Ny, Nz, H, J0, J1, K0, K1);
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

    private static readonly FluidProperties TestWater = FluidProperties.Constant("test water",
        density: 1000, dynamicViscosity: 1e-3, thermalConductivity: 0.6,
        specificHeat: 4000, thermalExpansion: 2e-4);

    private static CfdSettings Circuit(FluidProperties? fluid, string? name = null)
    {
        double lo = J0 * H, hi = J1 * H;
        var inlet = new FlowOpening
        {
            Face = BoxFace.XMin, UMin = lo, UMax = hi, VMin = lo, VMax = hi,
            Kind = FlowFaceKind.InletVelocity, Velocity = new Vector3D(0.05, 0, 0),
            Temperature = 293.15
        };
        var outlet = new FlowOpening
        {
            Face = BoxFace.XMax, UMin = lo, UMax = hi, VMin = lo, VMax = hi,
            Kind = FlowFaceKind.OutletPressure
        };
        var settings = CfdSettings.ForInternalFlow(
            new Aabb(new Vector3D(0, 0, 0), new Vector3D(Nx * H, Ny * H, Nz * H)),
            new[] { inlet, outlet }, name ?? "unused",
            new CfdSettings { CellSize = H, SteadyTolerance = 1e-6, MaxSteps = 20000 });
        return settings with { FluidName = name, CustomFluid = fluid };
    }

    private static EnvironmentSettings StillAir() => new()
    {
        Medium = MediumKind.StillFluid,
        AmbientTemperature = 293.15,
        CustomFluid = FluidProperties.Constant("test air",
            density: 1.2, dynamicViscosity: 1.8e-5, thermalConductivity: 0.026,
            specificHeat: 1005, thermalExpansion: 3.4e-3),
        Gravity = new Vector3D(0, -9.80665, 0),
        IncludeRadiation = false
    };

    [Fact]
    public void AnInternalCircuit_LeavesTheOuterSkinToTheCorrelations()
    {
        // Same case twice. With a NAMED working fluid the block is a water circuit inside
        // air, so its outer skin exchanges through the Stage 1 correlations; with the
        // fluid left null the CFD claims to BE the surroundings, so the unwetted skin gets
        // nothing but radiation (off here) and the block can only shed heat through the
        // bore. The one with two heat paths must be cooler — and by a margin far outside
        // any solver noise.
        const double power = 40.0;
        var environment = StillAir() with { CustomFluid = TestWater, FluidName = null };

        var (boreOnlyInput, bases1) = HollowBlockInput(power);
        var boreOnly = ConjugateHeatStudy.Run(boreOnlyInput, bases1, environment, Circuit(null, null));

        // The internal case: water in the bore (named on the CFD), air outside (the
        // environment's own fluid).
        var (composedInput, bases2) = HollowBlockInput(power);
        var composed = ConjugateHeatStudy.Run(composedInput, bases2, StillAir(),
            Circuit(TestWater));

        double boreOnlyPeak = Peak(boreOnly);
        double composedPeak = Peak(composed);
        Assert.True(composedPeak < boreOnlyPeak,
            $"the composed film must shed more: {composedPeak:F3} K vs {boreOnlyPeak:F3} K");

        // And the correlation leg must actually be doing something: an outer skin at a few
        // W/(m²·K) over ~0.03 m² against a several-kelvin excess is worth a measurable
        // slice of 40 W, so the two answers cannot be within noise of each other.
        Assert.True(boreOnlyPeak - composedPeak > 1e-3,
            $"difference {boreOnlyPeak - composedPeak:E3} K reads as no correlation leg at all");
    }

    [Fact]
    public void TheFilmField_SeparatesTheResolvedWaterFromTheCorrelatedAir()
    {
        // Per-triangle composition, read off the film coefficient the solve reports: the
        // bore is a resolved WATER film (k_f/(h/2) = 120 W/(m²·K) on this grid), the outer
        // skin a natural-convection AIR film (single digits). Two populations, orders
        // apart, is what "the CFD wins where it is defined" looks like in the output.
        var (input, bases) = HollowBlockInput(40.0);
        var result = ConjugateHeatStudy.Run(input, bases, StillAir(), Circuit(TestWater));

        var film = (NodalScalarField)result.Thermal.Fields.First(f => f.Name == "Film coefficient");
        var mesh = input.Mesh;

        double maxAir = 0, minWater = double.PositiveInfinity;
        var boreNodes = new HashSet<int>();
        var outerNodes = new HashSet<int>();
        foreach (var tri in mesh.BoundaryTriangles)
            foreach (int n in new[] { tri.A, tri.B, tri.C })
                (tri.FaceId == StructuredHollowBlock.FaceBore ? boreNodes : outerNodes).Add(n);
        // A node on the bore RIM touches both surfaces, so its nodal average mixes the two
        // films by construction. Judge only the interiors of each population — the rim is
        // where the two legitimately meet, not where either is wrong.
        var rim = new HashSet<int>(boreNodes);
        rim.IntersectWith(outerNodes);
        boreNodes.ExceptWith(rim);
        outerNodes.ExceptWith(rim);

        foreach (int n in outerNodes) maxAir = Math.Max(maxAir, film.Values[n]);
        foreach (int n in boreNodes) minWater = Math.Min(minWater, film.Values[n]);

        Assert.True(maxAir > 0, "the outer skin carries no film at all — the correlations never ran");
        Assert.InRange(maxAir, 0.5, 40);          // natural convection in air, generously banded
        // The strictest form of "two populations": the LOWEST bore film still exceeds the
        // HIGHEST air film, so the two never overlap. Measured 85.7 against 8.7 — an order
        // of magnitude, which is the physical gap between forced water and still air.
        Assert.True(minWater > 5 * maxAir,
            $"the bore film ({minWater:F1}) must dwarf the air film ({maxAir:F1})");
    }

    [Fact]
    public void AnUnknownFluidName_IsATypedFailureNamingWhatIsAvailable()
    {
        var (input, bases) = HollowBlockInput(1.0);
        var failure = Assert.Throws<InvalidOperationException>(
            () => ConjugateHeatStudy.Run(input, bases, StillAir(), Circuit(null, "unobtainium")));
        Assert.Contains("unobtainium", failure.Message);
        Assert.Contains("Air", failure.Message);
    }

    private static double Peak(ConjugateHeatStudy.Result result) =>
        ((NodalScalarField)result.Thermal.Fields.First(f => f.Name == "Temperature"))
        .Values.Max();
}
