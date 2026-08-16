using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Solvers;
using OpenSim.Solvers.Environment;

namespace OpenSim.Cfd;

/// <summary>
/// Conjugate heat transfer: the FE solid conduction solve coupled to the CFD flow +
/// fluid-energy solve through the <see cref="SurfaceFilmModel"/> seam. A partitioned
/// composition on the JouleHeatingStudy precedent — the solid leg is the EXISTING
/// thermal solver consuming a <see cref="SolveInput.PrescribedFilm"/>, the fluid leg the
/// <see cref="IncompressibleFlowSolver"/>; a monolithic solid+fluid system was rejected
/// (it would destroy the reduce-once SPD structure and mix discretizations).
/// <para>
/// Steady: an outer loop exchanges solid surface temperatures (under-relaxed at ½ —
/// the plain handoff can ring when the film is strong) against the fluid's wall film
/// until the wall temperatures settle; radiation to ambient, when the environment asks
/// for it, joins the film per iteration through the factored coefficient, so the
/// converged state satisfies the exact nonlinear exchange.
/// </para>
/// <para>
/// Transient: FROZEN-FLOW mode, a stated v1 decision — the flow and its film are solved
/// once at the initial wall temperatures and held while the solid marches through time.
/// Honest when the flow settles much faster than the solid warms (t_flow = L/U ≪
/// τ_thermal, checked and logged); time-accurate conjugate stepping is a named deferral.
/// </para>
/// </summary>
public static class ConjugateHeatStudy
{
    /// <summary>Outer-loop cap; a conjugate exchange that has not settled by then is
    /// oscillating, and under-relaxation or a coarser coupling is the fix.</summary>
    public const int MaxOuterIterations = 30;

    /// <summary>Under-relaxation of the wall-temperature handoff.</summary>
    public const double Relaxation = 0.5;

    /// <summary>Everything a conjugate solve produces: the solid-side results (the
    /// standard output every results consumer knows), the resolved flow field for the
    /// flow visualizations, and the voxelized domain the two exchange across.</summary>
    public sealed record Result(SolveOutput Thermal, FlowSolution Flow, VoxelizedDomain Domain);

    /// <param name="solidInput">The merged-assembly thermal input (mesh, materials, BCs,
    /// contacts, heat sources; <see cref="SolveInput.TransientThermal"/> selects the
    /// transient mode). Its Environment must be null — the environment is given here.</param>
    /// <param name="nodeBases">First merged node index of each body (the assembly order).</param>
    /// <param name="environment">Ambient, fluid, gravity, radiation switch.</param>
    /// <param name="cfd">Domain, grid and boundary policy for the flow leg (face kinds
    /// already configured, e.g. via <see cref="CfdSettings.ForExternalFlow"/>).</param>
    public static Result Run(SolveInput solidInput, IReadOnlyList<int> nodeBases,
        EnvironmentSettings environment, CfdSettings cfd,
        IProgress<SolverProgress>? progress = null,
        int maxDegreeOfParallelism = -1,
        CancellationToken cancellationToken = default)
    {
        if (solidInput.Environment is not null)
            throw new InvalidOperationException(
                "The conjugate study supplies the environment itself; leave SolveInput.Environment null.");
        if (environment.Medium == MediumKind.Vacuum)
            throw new InvalidOperationException(
                "A vacuum has no fluid to resolve — use the Stage 1 environment solve, " +
                "which handles radiation-only exchange exactly.");

        var log = new List<string>();
        var mesh = solidInput.Mesh;
        double ambient = environment.AmbientTemperature;
        var fluidProps = environment.ResolveFluid()!;
        // Film-state properties at the ambient: the conjugate loop could re-evaluate at
        // the film temperature per iteration, but property drift is a few percent over
        // tens of kelvin — a named refinement, stated here rather than silently skipped.
        var fluid = fluidProps.AtTemperature(ambient);
        log.Add($"Conjugate heat: {fluidProps.Name} at {ambient:F2} K " +
                $"(ν = {fluid.KinematicViscosity:G3} m²/s, α = {fluid.ThermalDiffusivity:G3} m²/s, " +
                "properties at ambient — film-temperature update is a named refinement).");

        // ---- Voxelize.
        progress?.Report(new SolverProgress("Voxelizing the fluid domain", 0.02));
        var bounds = Aabb.FromPoints(mesh.Nodes);
        var resolved = cfd.ResolveGrid(bounds, environment.FlowVelocity);
        var domain = Voxelizer.Voxelize(mesh, nodeBases, resolved, maxDegreeOfParallelism,
            cancellationToken);
        log.AddRange(domain.Notes);

        // ---- Fluid solver (kept alive across outer iterations: the previous flow is the
        //      warm start for the next, which is most of the outer loop's speed).
        var thermalOptions = new FlowThermalOptions
        {
            AmbientTemperature = ambient,
            Gravity = environment.Gravity
        };
        var flowSolver = new IncompressibleFlowSolver(domain, cfd, fluid,
            thermal: thermalOptions, maxDegreeOfParallelism: maxDegreeOfParallelism);

        var claimed = ClaimedFaceIds(solidInput);
        bool transient = solidInput.TransientThermal is not null;

        // Wall temperatures the fluid sees, per wall face — start at the initial solid
        // temperature (transient) or the ambient (steady).
        double startWallT = transient ? solidInput.TransientThermal!.InitialTemperature : ambient;
        var wallT = new double[domain.WallFaces.Count];
        Array.Fill(wallT, startWallT);

        if (transient)
        {
            // ---- Frozen-flow transient: one flow solve, one film, one solid march.
            progress?.Report(new SolverProgress("Solving the flow field (frozen-flow)", 0.1));
            var flow = flowSolver.SolveSteady(wallT, cancellationToken,
                (step, residual) => progress?.Report(new SolverProgress(
                    $"Flow march: step {step}, residual {residual:G2}", 0.1)));
            var film = BuildFilm(domain, flow, fluid.ThermalConductivity, mesh, ambient,
                claimed, environment, solidInput, nodeBases, SurfaceTemperatures(mesh, wallT, domain));
            LogFrozenFlowValidity(log, environment, domain, flow, solidInput);

            progress?.Report(new SolverProgress("Solid transient march", 0.3));
            var thermal = new TransientThermalSolver().Solve(
                solidInput with { PrescribedFilm = film }, progress, cancellationToken);
            return Finish(thermal, flow, domain, log);
        }

        // ---- Steady conjugate loop.
        SolveOutput? solidOutput = null;
        FlowSolution? lastFlow = null;
        double change = double.PositiveInfinity;
        int outer = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outer++;
            double fraction = Math.Min(0.9, 0.05 + 0.85 * outer / 10.0);
            progress?.Report(new SolverProgress($"Conjugate iteration {outer}", fraction));

            int iteration = outer;
            var flow = flowSolver.SolveSteady(wallT, cancellationToken,
                (step, residual) => progress?.Report(new SolverProgress(
                    $"Conjugate iteration {iteration} — flow step {step}, residual {residual:G2}",
                    fraction)));
            lastFlow = flow;
            var surfaceT = TriangleMeanTemperatures(mesh, solidOutput);
            var film = BuildFilm(domain, flow, fluid.ThermalConductivity, mesh, ambient,
                claimed, environment, solidInput, nodeBases,
                surfaceT ?? UniformSurface(mesh, startWallT));

            solidOutput = new HeatConductionSolver().Solve(
                solidInput with { PrescribedFilm = film }, null, cancellationToken);
            var nodalT = ((Core.Results.NodalScalarField)solidOutput.Fields
                .First(f => f.Name == "Temperature")).Values;

            // Under-relaxed wall-temperature handoff, and the outer convergence test.
            change = 0;
            foreach (var (wf, idx) in domain.WallFaces.Select((w, i) => (w, i)))
            {
                var tri = mesh.BoundaryTriangles[wf.BoundaryTriangle];
                double solidT = (nodalT[tri.A] + nodalT[tri.B] + nodalT[tri.C]) / 3.0;
                double next = wallT[idx] + Relaxation * (solidT - wallT[idx]);
                change = Math.Max(change, Math.Abs(next - wallT[idx]));
                wallT[idx] = next;
            }

            double threshold = Math.Max(0.01, 1e-3 * MaxExcursion(wallT, ambient));
            if (outer >= 2 && change < threshold) break;
            if (outer >= MaxOuterIterations)
                throw new InvalidOperationException(
                    $"The conjugate exchange did not settle: after {outer} outer iterations the " +
                    $"wall temperatures still move by {change:G3} K. This usually means a strong " +
                    "film against a weak conduction path — refine the CFD grid or check the " +
                    "solid's conductivity and contacts.");
        }

        log.Add($"Conjugate exchange settled in {outer} outer iterations " +
                $"(last wall-temperature change {change:G3} K, relaxation {Relaxation}).");
        return Finish(solidOutput!, lastFlow!, domain, log);
    }

    // ================================================================ film composition

    /// <summary>
    /// The complete film one solid solve consumes: the CFD wall conduction toward the
    /// local fluid temperature, PLUS — when the environment radiates — the factored
    /// radiation coefficient toward the ambient, blended per triangle as additive Robin
    /// conductances (h_tot = h_cfd + h_r, reference at the conductance-weighted mix).
    /// </summary>
    private static SurfaceFilmModel BuildFilm(VoxelizedDomain domain, FlowSolution flow,
        double fluidConductivity, FeMesh mesh, double ambient,
        IReadOnlySet<int> claimed, EnvironmentSettings environment, SolveInput solidInput,
        IReadOnlyList<int> nodeBases, double[] surfaceTemperature)
    {
        var film = WallFluxExtractor.Extract(domain, flow, fluidConductivity, mesh, ambient,
            claimed);
        if (!environment.IncludeRadiation) return film;

        var coefficients = film.TriangleFilmCoefficient.ToArray();
        var references = film.TriangleReferenceTemperature!.ToArray();
        int radiating = 0;
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            var tri = mesh.BoundaryTriangles[t];
            if (claimed.Contains(tri.FaceId)) continue;

            int body = BodyOfNode(nodeBases, tri.A);
            var material = solidInput.RegionMaterials?.GetValueOrDefault(body) ?? solidInput.Material;
            double? emissivity = material.Emissivity;
            if (emissivity is null)
                throw new InvalidOperationException(
                    $"Radiation is on but material '{material.Name}' has no emissivity. " +
                    "Set one (or switch radiation off) — a silent default would fake the balance.");
            if (emissivity.Value <= 0) continue;

            double ts = Math.Max(surfaceTemperature[t], 1.0);
            double hr = ConvectionCorrelations.RadiativeFilmCoefficient(
                emissivity.Value, ts, ambient);
            radiating++;
            if (double.IsNaN(coefficients[t]))
            {
                coefficients[t] = hr;
                references[t] = ambient;
            }
            else
            {
                double hc = coefficients[t];
                coefficients[t] = hc + hr;
                references[t] = (hc * references[t] + hr * ambient) / (hc + hr);
            }
        }

        return film with
        {
            TriangleFilmCoefficient = coefficients,
            TriangleReferenceTemperature = references,
            Origin = film.Origin + $" + factored radiation on {radiating} triangles"
        };
    }

    // ================================================================ helpers

    private static Result Finish(SolveOutput thermal, FlowSolution flow,
        VoxelizedDomain domain, List<string> log)
    {
        log.AddRange(flow.Notes);
        var summary = new Dictionary<string, double>(
            thermal.Summary ?? new Dictionary<string, double>())
        {
            ["Peak fluid speed (m/s)"] = flow.MaxSpeed(),
            ["Flow steps"] = flow.Steps,
            ["Max flow divergence (1/s)"] = flow.MaxDivergence
        };
        return new Result(thermal with
        {
            Log = log.Concat(thermal.Log).ToList(),
            Summary = summary
        }, flow, domain);
    }

    private static IReadOnlySet<int> ClaimedFaceIds(SolveInput input)
    {
        var claimed = new HashSet<int>();
        foreach (var bc in input.BoundaryConditions)
            if (bc is FixedTemperature or HeatFlux or Convection)
                claimed.UnionWith(bc.FaceIds);
        return claimed;
    }

    /// <summary>Per-boundary-triangle mean temperature from a solid solve; null before
    /// the first solve.</summary>
    private static double[]? TriangleMeanTemperatures(FeMesh mesh, SolveOutput? output)
    {
        if (output is null) return null;
        var nodal = ((Core.Results.NodalScalarField)output.Fields
            .First(f => f.Name == "Temperature")).Values;
        var result = new double[mesh.BoundaryTriangles.Count];
        for (int t = 0; t < result.Length; t++)
        {
            var tri = mesh.BoundaryTriangles[t];
            result[t] = (nodal[tri.A] + nodal[tri.B] + nodal[tri.C]) / 3.0;
        }
        return result;
    }

    private static double[] UniformSurface(FeMesh mesh, double value)
    {
        var result = new double[mesh.BoundaryTriangles.Count];
        Array.Fill(result, value);
        return result;
    }

    /// <summary>Triangle temperatures implied by the frozen wall-face handoff (uniform
    /// start): every triangle at the start temperature.</summary>
    private static double[] SurfaceTemperatures(FeMesh mesh, double[] wallT,
        VoxelizedDomain domain)
    {
        // The frozen-flow film is built before any solid solve, so the surface is at the
        // uniform start temperature the wall array was filled with.
        return UniformSurface(mesh, wallT.Length > 0 ? wallT[0] :
            throw new InvalidOperationException(
                "No wall faces — the bodies are invisible to the grid; refine the CFD cells."));
    }

    private static void LogFrozenFlowValidity(List<string> log, EnvironmentSettings environment,
        VoxelizedDomain domain, FlowSolution flow, SolveInput solidInput)
    {
        double u = Math.Max(flow.MaxSpeed(), 1e-9);
        double l = domain.Grid.H * Math.Max(domain.Grid.Nx,
            Math.Max(domain.Grid.Ny, domain.Grid.Nz));
        double tFlow = l / u;
        double duration = solidInput.TransientThermal!.Duration;
        log.Add($"Frozen-flow transient: flow settle time ≈ {tFlow:G3} s vs solid march " +
                $"{duration:G3} s — " +
                (tFlow < 0.1 * duration
                    ? "the frozen-film assumption is comfortable."
                    : "WARNING: the flow timescale is not small against the march; " +
                      "the frozen film underrepresents early-time coupling."));
    }

    private static int BodyOfNode(IReadOnlyList<int> nodeBases, int node)
    {
        for (int b = nodeBases.Count - 1; b >= 0; b--)
            if (node >= nodeBases[b]) return b;
        return 0;
    }

    /// <summary>The largest wall-temperature excursion from ambient [K] — the scale the
    /// outer convergence threshold is relative to.</summary>
    private static double MaxExcursion(double[] wallT, double ambient)
    {
        double max = 0;
        foreach (double t in wallT)
            max = Math.Max(max, Math.Abs(t - ambient));
        return max;
    }
}
