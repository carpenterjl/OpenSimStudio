using OpenSim.Cfd;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers.Environment;
using OpenSim.Tests.Solvers;

namespace OpenSim.Tests.Cfd;

/// <summary>
/// Fix 16 (TH-03, TH-04, TH-07, TH-15): the accuracy of the conjugate coupling itself.
/// The oracles are independent of the coupling's arithmetic: a lumped body's exponential,
/// the stream's own enthalpy ledger, an analytic conduction profile.
/// </summary>
public class ConjugateCouplingTests
{
    private const double Ambient = 293.15;
    private const double Power = 5.0;

    // The block of ConjugateHeatTests: 0.2 m cube, k = 200 (Biot ≪ 1 — a lumped body),
    // ρ·c = 5e5 J/(m³·K), so C = 4000 J/K.
    private const double Capacity = 1000 * 500 * 0.2 * 0.2 * 0.2;

    private static (SolveInput Input, IReadOnlyList<int> NodeBases) HeatedBlock(
        TransientThermalSettings? transient = null)
    {
        var material = StructuredBoxMesh.Conductor("aluminium-ish", 200);
        var body = StructuredBoxMesh.Box("block", 0.4, 0.6, 0.15, 0.35, 0.15, 0.35,
            2, 2, 2, material);
        var assembled = FeMeshAssembler.Assemble(new[] { body });
        double volume = assembled.Mesh.TotalVolume();
        var input = new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = material,
            BoundaryConditions = assembled.BoundaryConditions,
            RegionMaterials = assembled.RegionMaterials,
            ElementHeatSource = Enumerable.Repeat(Power / volume, assembled.Mesh.ElementCount).ToArray(),
            TransientThermal = transient
        };
        return (input, assembled.NodeBases);
    }

    private static EnvironmentSettings Stream(double speed) => new()
    {
        Medium = MediumKind.MovingFluid,
        AmbientTemperature = Ambient,
        FlowVelocity = new Vector3D(speed, 0, 0),
        CustomFluid = FluidProperties.Constant("test air",
            density: 1.2, dynamicViscosity: 1.8e-5, thermalConductivity: 0.026,
            specificHeat: 1005, thermalExpansion: 3.4e-3),
        IncludeRadiation = false,
        Gravity = new Vector3D(0, 0, 0)
    };

    private static CfdSettings Grid(double speed, int cellsAcross) =>
        CfdSettings.ForExternalFlow(new Vector3D(speed, 0, 0)) with
        {
            DomainBox = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1.5, 0.5, 0.5)),
            CellSize = 0.5 / cellsAcross,
            SteadyTolerance = 1e-6,
            MaxSteps = 60000
        };

    private static double MeanRise(SolveOutput output) =>
        ((NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values.Average()
        - Ambient;

    // ---------------------------------------------------------------- TH-03: transient

    [Fact]
    public void LumpedBlock_HeatsWithItsOwnTimeConstant_WhateverTheStep()
    {
        // The known film: the steady run gives the block's whole exchange with the stream,
        // K = P/ΔT_steady. Velocity is frozen and buoyancy is off, so the fluid answers the
        // wall linearly and (its transit, 150 s, being short against the block) at once:
        // C·dT/dt = P − K·(T − T_in), i.e. T − T_in = ΔT_steady·(1 − e^(−t/τ)), τ = C/K.
        //
        // The explicit exchange this replaces behaved as C + g·A·Δt: with g·A = 0.30 W/K
        // here, the block at t = τ would read 0.52 of its steady rise at Δt = τ/5 and
        // 0.60 at τ/20, against 0.632.
        var environment = Stream(0.01);
        var (steadyInput, bases) = HeatedBlock();
        var steady = ConjugateHeatStudy.Run(steadyInput, bases, environment, Grid(0.01, 12));
        double steadyRise = MeanRise(steady.Thermal);
        double tau = Capacity * steadyRise / Power;
        Assert.True(tau > 50 * 150, $"the fixture must be quasi-steady on the fluid side (τ = {tau:G4} s)");

        double RiseAtTau(int steps)
        {
            var (input, nb) = HeatedBlock(new TransientThermalSettings
            {
                InitialTemperature = Ambient,
                Duration = tau,
                TimeStep = tau / steps
            });
            return MeanRise(ConjugateHeatStudy.Run(input, nb, environment, Grid(0.01, 12)).Thermal)
                   / steadyRise;
        }

        // Backward Euler's own answer for the lumped equation, 1 − (1 + Δt/τ)^(−n): what an
        // exactly coupled step produces. Both step sizes must land on it.
        double coarse = RiseAtTau(5);
        double fine = RiseAtTau(20);
        double coarseEuler = 1 - Math.Pow(1 + 1.0 / 5, -5);
        double fineEuler = 1 - Math.Pow(1 + 1.0 / 20, -20);
        Assert.True(Math.Abs(coarse - coarseEuler) <= 0.015 * coarseEuler,
            $"Δt = τ/5: rise fraction {coarse:F4} vs backward Euler {coarseEuler:F4}");
        Assert.True(Math.Abs(fine - fineEuler) <= 0.015 * fineEuler,
            $"Δt = τ/20: rise fraction {fine:F4} vs backward Euler {fineEuler:F4}");

        // The plan's gate: within 3 % of the exponential itself at the finer step.
        double exact = 1 - Math.Exp(-1);
        Assert.True(Math.Abs(fine - exact) <= 0.03 * exact,
            $"Δt = τ/20: rise fraction {fine:F4} vs 1 − 1/e = {exact:F4}");
    }

    // ---------------------------------------------------------------- TH-04: steady

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    public void SteadyExchange_TheStreamCarriesWhatTheBlockDissipates(int cellsAcross)
    {
        // The solid's own film balance holds for ANY film it is handed, converged or not,
        // so it cannot gate the outer loop. The stream's enthalpy ledger can: it is the
        // fluid's side of the same heat, computed from the outlet temperatures alone. On
        // the finer grid the half-cell conductance g doubles while the real film does not,
        // which is where the plain handoff slowed down and stopped short.
        var (input, bases) = HeatedBlock();
        var result = ConjugateHeatStudy.Run(input, bases, Stream(0.01), Grid(0.01, cellsAcross));

        double carried = result.Outlets!.HeatRemoved;
        Assert.True(Math.Abs(carried - Power) <= 0.01 * Power,
            $"{cellsAcross} cells: the stream carries {carried:F4} W of the {Power} W dissipated");
        Assert.Contains(result.Thermal.Log, line => line.Contains("Conjugate exchange settled"));
    }

    // ---------------------------------------------------------------- TH-07: residual

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    public void DefaultTolerance_MeansTheSameThing_OnAFineGrid(int cells)
    {
        // Pure conduction across a sealed gap: the slowest mode has τ = L²/(π²α), the
        // pseudo-time step is 0.5·h²/α, so τ/Δt = 2N²/π² — 20 at N = 10, 324 at N = 40.
        // A per-STEP change test at the default 1e-5 stopped with tolerance·τ/Δt of the
        // span still to go (3e-3 at N = 40). Per flow time (L²/α here) what is left is
        // tolerance/π² on any grid.
        var grid = new CartesianGrid(cells, 2, 1, new Vector3D(0, 0, 0), 1.0 / cells);
        var domain = new VoxelizedDomain(grid, Array.Empty<WallFace>(), grid.CellCount,
            Array.Empty<int>(), Array.Empty<string>());
        var settings = new CfdSettings { MaxSteps = 200000 };     // the DEFAULT tolerance
        var thermal = new FlowThermalOptions
        {
            AmbientTemperature = 0.5,
            XMinTemperature = 1.0,
            XMaxTemperature = 0.0
        };

        var flow = new IncompressibleFlowSolver(domain, settings,
            new FluidState(1, 1, 1, 1, 0), thermal: thermal).SolveSteady();

        double maxError = 0;
        for (int i = 0; i < cells; i++)
        {
            double exact = 1.0 - (i + 0.5) / cells;
            maxError = Math.Max(maxError, Math.Abs(flow.Temperature![grid.CellIndex(i, 0, 0)] - exact));
        }
        Assert.True(maxError <= settings.SteadyTolerance,
            $"N = {cells}: {maxError:E2} of the span left at the default tolerance " +
            $"({flow.Steps} steps)");
    }

    // ---------------------------------------------------------------- the acceleration

    [Fact]
    public void AndersonMixer_SolvesASlowLinearExchange_InAHandfulOfPasses()
    {
        // x ← A·x + b with contraction factors 0.97, 0.6 and 0 — the conjugate handoff's
        // shape (one slow mode at 1 − h_eff/g, faster ones beside it). The plain
        // iteration needs ln(1e-9)/ln(0.97) ≈ 680 passes.
        double[] rates = { 0.97, 0.6, 0.0 };
        double[] b = { 3.0, -2.0, 5.0 };
        var exact = rates.Select((r, i) => b[i] / (1 - r)).ToArray();

        var mixer = new AndersonMixer(depth: 4, beta: 0.5);
        var x = new double[3];
        int passes = 0;
        double error = double.PositiveInfinity;
        while (error > 1e-9 && passes < 50)
        {
            passes++;
            var f = new double[3];
            for (int i = 0; i < 3; i++) f[i] = rates[i] * x[i] + b[i] - x[i];
            x = mixer.Next(x, f);
            error = x.Select((v, i) => Math.Abs(v - exact[i])).Max();
        }
        Assert.True(passes <= 8, $"took {passes} passes (error {error:E2})");

        // With no history the first step is the damped plain one.
        mixer.Reset();
        var first = mixer.Next(new double[] { 1, 1, 1 }, new double[] { 2, 0, -2 });
        Assert.Equal(new double[] { 2, 1, 0 }, first);
    }

    // ---------------------------------------------------------------- TH-15: wetted faces

    [Fact]
    public void AFaceMeshedFinerThanTheGrid_IsWettedAsAWhole_AndLeavesTheSurroundings()
    {
        // A 0.5 m box with 4 mesh cells per edge puts TWO triangles on every 0.125 m voxel
        // face, and a wall face maps to ONE nearest centroid — so a good share of the
        // skin's triangles receive no wall face at all.
        var material = StructuredBoxMesh.Conductor("block", 200);
        var mesh = StructuredBoxMesh.Build(0.25, 0.75, 0.25, 0.75, 0.25, 0.75, 4, 4, 4);
        var resolved = new CfdSettings.ResolvedGrid(
            new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1)), 0.125, 8, 8, 8,
            Array.Empty<string>());
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, resolved);

        var mapped = domain.WallFaces.Select(w => w.BoundaryTriangle).ToHashSet();
        int unmapped = mesh.BoundaryTriangles.Count - mapped.Count;
        Assert.True(unmapped > mesh.BoundaryTriangles.Count / 4,
            $"premise: {unmapped} of {mesh.BoundaryTriangles.Count} triangles have no wall face");

        // Every one of the six faces is nonetheless wetted, as a face.
        var wetted = ConjugateHeatStudy.WettedFaceIds(domain, mesh);
        Assert.Equal(6, wetted.Count);

        var input = new SolveInput
        {
            Mesh = mesh,
            Material = material,
            BoundaryConditions = Array.Empty<BoundaryCondition>(),
            Environment = new EnvironmentSettings
            {
                Medium = MediumKind.StillFluid,
                AmbientTemperature = Ambient
            }
        };
        // All six resolved: the surroundings add nothing — not even on the unmapped triangles.
        Assert.Null(EnvironmentBoundaryModel.Build(input, new List<string>(), wetted));

        // One face resolved: it has no panel, and no triangle of it takes a film.
        var bore = new HashSet<int> { StructuredBoxMesh.FaceZMax };
        var model = EnvironmentBoundaryModel.Build(input, new List<string>(), bore)!;
        Assert.Equal(5, model.Panels.Count);
        Assert.DoesNotContain(model.Panels, p => p.FaceId == StructuredBoxMesh.FaceZMax);
        var nodal = Enumerable.Repeat(Ambient + 20, mesh.NodeCount).ToArray();
        var film = model.Evaluate(nodal);
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
            Assert.Equal(mesh.BoundaryTriangles[t].FaceId != StructuredBoxMesh.FaceZMax,
                film.IsWetted(t));

        // A stray wall face does not make a face wetted: one voxel face against a 0.25 m²
        // face covers a sixteenth of it.
        var stray = domain with
        {
            WallFaces = domain.WallFaces.Take(1).ToList()
        };
        Assert.Empty(ConjugateHeatStudy.WettedFaceIds(stray, mesh));
    }
}
