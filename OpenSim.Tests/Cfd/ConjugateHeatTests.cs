using OpenSim.Cfd;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers.Environment;
using OpenSim.Tests.Solvers;

namespace OpenSim.Tests.Cfd;

public class ConjugateHeatTests
{
    // ---------------------------------------------------------------- extractor identity

    [Fact]
    public void WallFluxExtractor_ReproducesTheSummedFaceFlux_Identically()
    {
        // The film form h_tri·A_tri·(T_w − T̄) with conductance-weighted T̄ must equal
        // Σ g·a·(T_w − T_cell) over the triangle's wall faces — an algebraic identity,
        // gated at machine precision.
        var mesh = StructuredBoxMesh.Build(0.25, 0.75, 0.25, 0.75, 0.25, 0.75, 4, 4, 4);
        var resolved = new CfdSettings.ResolvedGrid(
            new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1)), 0.125, 8, 8, 8,
            Array.Empty<string>());
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, resolved);

        // A fabricated fluid temperature field with per-cell variation.
        var temperature = domain.Grid.AllocateCellField();
        for (int c = 0; c < temperature.Length; c++)
            temperature[c] = 300 + 0.1 * c % 17;
        var flow = new FlowSolution(domain.Grid,
            domain.Grid.AllocateUField(), domain.Grid.AllocateVField(), domain.Grid.AllocateWField(),
            domain.Grid.AllocateCellField(), 1, 0, 0, 0, 0,
            Array.Empty<string>(), temperature);

        const double kf = 0.03;
        var film = WallFluxExtractor.Extract(domain, flow, kf, mesh, 293);

        double g = kf / (domain.Grid.H / 2);
        double faceArea = domain.Grid.H * domain.Grid.H;
        // Group the face flux per triangle and compare against the film's flux at an
        // arbitrary wall temperature per triangle.
        var faceFlux = new Dictionary<int, double>();
        var faceCond = new Dictionary<int, double>();
        foreach (var wf in domain.WallFaces)
        {
            double tw = 350 + wf.BoundaryTriangle % 7;   // arbitrary but deterministic
            faceFlux[wf.BoundaryTriangle] = faceFlux.GetValueOrDefault(wf.BoundaryTriangle)
                + g * faceArea * (tw - temperature[wf.FluidCell]);
            faceCond[wf.BoundaryTriangle] = faceCond.GetValueOrDefault(wf.BoundaryTriangle) + g * faceArea;
        }
        foreach (var (tri, expected) in faceFlux)
        {
            double tw = 350 + tri % 7;
            double area = WallFluxExtractor.TriangleArea(mesh, mesh.BoundaryTriangles[tri]);
            double filmFlux = film.TriangleFilmCoefficient[tri] * area
                              * (tw - film.ReferenceTemperatureOf(tri));
            Assert.True(Math.Abs(filmFlux - expected) <= 1e-12 * Math.Abs(expected) + 1e-12,
                $"triangle {tri}: film {filmFlux:E6} vs faces {expected:E6}");
        }

        // Triangles without wall faces are NOT wetted.
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
            if (!faceCond.ContainsKey(t))
                Assert.False(film.IsWetted(t));
    }

    [Fact]
    public void WallFluxExtractor_ExcludesUserClaimedFaces()
    {
        var mesh = StructuredBoxMesh.Build(0.25, 0.75, 0.25, 0.75, 0.25, 0.75, 2, 2, 2);
        var resolved = new CfdSettings.ResolvedGrid(
            new Aabb(new Vector3D(0, 0, 0), new Vector3D(1, 1, 1)), 0.125, 8, 8, 8,
            Array.Empty<string>());
        var domain = Voxelizer.Voxelize(mesh, new[] { 0 }, resolved);
        var flow = new FlowSolution(domain.Grid,
            domain.Grid.AllocateUField(), domain.Grid.AllocateVField(), domain.Grid.AllocateWField(),
            domain.Grid.AllocateCellField(), 1, 0, 0, 0, 0, Array.Empty<string>(),
            domain.Grid.AllocateCellField());

        var film = WallFluxExtractor.Extract(domain, flow, 0.03, mesh, 293,
            new HashSet<int> { StructuredBoxMesh.FaceZMax });

        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
            if (mesh.BoundaryTriangles[t].FaceId == StructuredBoxMesh.FaceZMax)
                Assert.False(film.IsWetted(t));   // the user owns that face
        Assert.True(film.WettedCount > 0);
    }

    // ---------------------------------------------------------------- the conjugate solve

    private static (SolveInput Input, IReadOnlyList<int> NodeBases) HeatedBoxInput(
        double power, TransientThermalSettings? transient = null,
        double? emissivity = null)
    {
        var material = StructuredBoxMesh.Conductor("aluminium-ish", 200) with
        {
            Emissivity = emissivity
        };
        var body = StructuredBoxMesh.Box("block", 0.4, 0.6, 0.15, 0.35, 0.15, 0.35,
            2, 2, 2, material);
        var assembled = FeMeshAssembler.Assemble(new[] { body });
        double volume = assembled.Mesh.TotalVolume();
        var source = Enumerable.Repeat(power / volume, assembled.Mesh.ElementCount).ToArray();
        var input = new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = material,
            BoundaryConditions = assembled.BoundaryConditions,
            RegionMaterials = assembled.RegionMaterials,
            ElementHeatSource = source,
            TransientThermal = transient
        };
        return (input, assembled.NodeBases);
    }

    private static EnvironmentSettings Channel(double speed, bool radiation = false) => new()
    {
        Medium = MediumKind.MovingFluid,
        AmbientTemperature = 293.15,
        FlowVelocity = new Vector3D(speed, 0, 0),
        CustomFluid = FluidProperties.Constant("test air",
            density: 1.2, dynamicViscosity: 1.8e-5, thermalConductivity: 0.026,
            specificHeat: 1005, thermalExpansion: 3.4e-3),
        IncludeRadiation = radiation,
        Gravity = new Vector3D(0, 0, 0)   // forced convection only: no buoyancy leg
    };

    private static CfdSettings ChannelGrid(double speed) =>
        CfdSettings.ForExternalFlow(new Vector3D(speed, 0, 0)) with
        {
            DomainBox = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1.5, 0.5, 0.5)),
            CellSize = 0.5 / 12,
            SteadyTolerance = 1e-6,
            MaxSteps = 30000
        };

    [Fact]
    public void HeatedBlockInAChannel_BalancesPower_AndLandsNearTheCorrelation()
    {
        // A conductive block dissipating 5 W in a 0.01 m/s creeping stream (chosen so the thermal boundary layer spans several grid cells — at higher speeds the staircase film is conduction-capped below any correlation, a stated resolution limit). Two gates of very
        // different sharpness, on purpose: the FILM ENERGY BALANCE (the film flux at the
        // converged surface temperatures must carry exactly the dissipated power — the
        // steady identity) and the CORRELATION SANITY BAND (±30% against the flat-plate
        // correlation, which is all a staircase-voxel flow at this grid honestly owes;
        // the precision lives in the identity, the physics plausibility in the band).
        const double power = 5.0;
        var (input, nodeBases) = HeatedBoxInput(power);
        var environment = Channel(0.01);

        var result = ConjugateHeatStudy.Run(input, nodeBases, environment, ChannelGrid(0.01));

        var temps = ((NodalScalarField)result.Thermal.Fields
            .First(f => f.Name == "Temperature")).Values;
        double meanSurfaceT = temps.Average();
        Assert.True(meanSurfaceT > environment.AmbientTemperature + 1,
            $"the block must run warm (got {meanSurfaceT:F2} K)");

        // Film energy balance: Σ h·A·(T_s − T_ref) over the wetted skin ≡ the power.
        var mesh = input.Mesh;
        double filmFlux = 0;
        var film = ExtractFinalFilm(result, mesh, environment);
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            if (!film.IsWetted(t)) continue;
            var tri = mesh.BoundaryTriangles[t];
            double ts = (temps[tri.A] + temps[tri.B] + temps[tri.C]) / 3.0;
            filmFlux += film.TriangleFilmCoefficient[t]
                        * WallFluxExtractor.TriangleArea(mesh, tri)
                        * (ts - film.ReferenceTemperatureOf(t));
        }
        Assert.True(Math.Abs(filmFlux - power) <= 0.02 * power,
            $"film carries {filmFlux:F3} W of the {power} W dissipated");

        // Correlation sanity: mean h vs the forced flat-plate form at the block length.
        // Measured against the film's own LOCAL fluid references, not the ambient — the
        // channel preheats the stream around the block (a real effect the unbounded-
        // stream correlation does not model), and folding that into h would compare two
        // different definitions.
        double area = WettedArea(mesh, film);
        double refMean = FilmReferenceMean(mesh, film);
        double hMean = power / (area * (SurfaceMean(mesh, film, temps) - refMean));
        var fluid = environment.ResolveFluid()!.AtTemperature(environment.AmbientTemperature);
        double L = 0.2;
        double re = 0.01 * L / fluid.KinematicViscosity;
        double nu = ConvectionCorrelations.ForcedFlatPlate(re, fluid.Prandtl);
        double hCorr = nu * fluid.ThermalConductivity / L;
        Assert.InRange(hMean / hCorr, 0.7, 1.3);
    }

    [Fact]
    public void TransientRunToSteady_LandsOnTheSteadyConjugateAnswer()
    {
        // Held for many time constants, the transient must land ON the steady conjugate
        // loop. It is a much sharper statement than the frozen-film version this replaces:
        // that one could only assert a SIGNED BIAS (the film kept cold-fluid reference
        // temperatures, so it under-predicted the rise by ~30%), because the fluid
        // temperature never moved. Now the fluid energy equation marches with the solid,
        // so the two formulations converge to the same fixed point and the gate says so.
        const double power = 5.0;
        var environment = Channel(0.01);

        var (steadyInput, nodeBases) = HeatedBoxInput(power);
        var steady = ConjugateHeatStudy.Run(steadyInput, nodeBases, environment, ChannelGrid(0.01));
        var steadyT = ((NodalScalarField)steady.Thermal.Fields
            .First(f => f.Name == "Temperature")).Values;

        var (transientInput, nodeBases2) = HeatedBoxInput(power,
            new TransientThermalSettings
            {
                InitialTemperature = 293.15,
                Duration = 2e5,          // ≫ the block's lumped time constant
                // Few, long steps: backward Euler is unconditionally stable and only the
                // ENDPOINT is under test. With a time-accurate fluid the step length also
                // stops mattering to the fluid cost — each step marches at most four
                // transits, so fewer steps is strictly less work for the same answer.
                TimeStep = 1e4
            });
        var transient = ConjugateHeatStudy.Run(transientInput, nodeBases2, environment,
            ChannelGrid(0.01));
        var finalT = ((NodalScalarField)transient.Thermal.Fields
            .First(f => f.Name == "Temperature")).Values;

        double steadyRise = steadyT.Average() - 293.15;
        double finalRise = finalT.Average() - 293.15;
        // Two independent formulations of the same fixed point: the steady loop exchanges
        // under-relaxed wall temperatures until they stop moving; the transient integrates
        // there. They are not the same arithmetic, so the residual is the coupling's own
        // convergence, not round-off — but it is a BAND AROUND ONE, no longer a bias.
        Assert.InRange(finalRise / steadyRise, 0.9, 1.1);
    }

    [Fact]
    public void RadiationOn_LowersTheSurfaceTemperature_AndNullEmissivityFails()
    {
        const double power = 5.0;
        var (plain, nodeBases) = HeatedBoxInput(power);
        var (radiating, nodeBasesR) = HeatedBoxInput(power, emissivity: 0.85);

        var without = ConjugateHeatStudy.Run(plain, nodeBases, Channel(0.01), ChannelGrid(0.01));
        var with_ = ConjugateHeatStudy.Run(radiating, nodeBasesR, Channel(0.01, radiation: true),
            ChannelGrid(0.01));

        double tPlain = ((NodalScalarField)without.Thermal.Fields
            .First(f => f.Name == "Temperature")).Values.Average();
        double tRad = ((NodalScalarField)with_.Thermal.Fields
            .First(f => f.Name == "Temperature")).Values.Average();
        Assert.True(tRad < tPlain,
            $"radiation adds a heat path: {tRad:F2} K should be below {tPlain:F2} K");

        // Radiation with no emissivity: a typed failure naming the material, never 0.9.
        var (noEps, nb) = HeatedBoxInput(power);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConjugateHeatStudy.Run(noEps, nb, Channel(0.01, radiation: true), ChannelGrid(0.01)));
        Assert.Contains("emissivity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VacuumEnvironment_IsATypedFailure()
    {
        var (input, nodeBases) = HeatedBoxInput(1.0);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConjugateHeatStudy.Run(input, nodeBases,
                new EnvironmentSettings { Medium = MediumKind.Vacuum }, ChannelGrid(0.01)));
        Assert.Contains("vacuum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Rebuilds the film the last solid solve consumed, from the study's own
    /// outputs — the same extraction path, applied to the returned flow.</summary>
    private static SurfaceFilmModel ExtractFinalFilm(ConjugateHeatStudy.Result result,
        FeMesh mesh, EnvironmentSettings environment)
    {
        var fluid = environment.ResolveFluid()!.AtTemperature(environment.AmbientTemperature);
        return WallFluxExtractor.Extract(result.Domain, result.Flow,
            fluid.ThermalConductivity, mesh, environment.AmbientTemperature);
    }

    private static double WettedArea(FeMesh mesh, SurfaceFilmModel film)
    {
        double area = 0;
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
            if (film.IsWetted(t))
                area += WallFluxExtractor.TriangleArea(mesh, mesh.BoundaryTriangles[t]);
        return area;
    }

    /// <summary>Area-weighted mean of the film's local reference temperatures.</summary>
    private static double FilmReferenceMean(FeMesh mesh, SurfaceFilmModel film)
    {
        double sum = 0, area = 0;
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            if (!film.IsWetted(t)) continue;
            double a = WallFluxExtractor.TriangleArea(mesh, mesh.BoundaryTriangles[t]);
            sum += a * film.ReferenceTemperatureOf(t);
            area += a;
        }
        return sum / area;
    }

    private static double SurfaceMean(FeMesh mesh, SurfaceFilmModel film, IReadOnlyList<double> temps)
    {
        double sum = 0, area = 0;
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            if (!film.IsWetted(t)) continue;
            var tri = mesh.BoundaryTriangles[t];
            double a = WallFluxExtractor.TriangleArea(mesh, tri);
            sum += a * (temps[tri.A] + temps[tri.B] + temps[tri.C]) / 3.0;
            area += a;
        }
        return sum / area;
    }
}
