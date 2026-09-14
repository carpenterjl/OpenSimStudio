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
    /// <param name="Outlets">The open-boundary mass and enthalpy ledger — the mixing-cup
    /// outlet temperature a heat-exchanger study is actually about. Null when the flow
    /// carried no energy equation.</param>
    public sealed record Result(SolveOutput Thermal, FlowSolution Flow, VoxelizedDomain Domain,
        FlowOutletReport? Outlets = null);

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
        // The CFD working fluid. A NAMED fluid on the settings marks an internal circuit
        // (water through a copper block) whose surroundings are a different fluid
        // entirely; null keeps the external-flow meaning, where the CFD IS the environment.
        var fluidProps = cfd.ResolveFluid() ?? environment.ResolveFluid()!;

        // ---- The surroundings, when the CFD does not resolve them. An internal circuit
        //      leaves the block outer skin exposed to whatever the environment says it
        //      sits in, and that leg is still the Stage 1 correlation film (convection in
        //      the ENVIRONMENT fluid + factored radiation). When the CFD resolves the
        //      surroundings themselves this stays null and only radiation joins the film,
        //      which is the pre-existing path, byte for byte.
        EnvironmentBoundaryModel? surroundings = null;
        if (!cfd.ResolvesSurroundings)
        {
            var probe = solidInput with { Environment = environment };
            EnvironmentBoundaryModel.ValidateMaterials(probe);
            surroundings = EnvironmentBoundaryModel.Build(probe, log);
            log.Add(surroundings is null
                ? "Surroundings: every exterior face is claimed or wetted — the correlation " +
                  "film adds nothing."
                : $"Surroundings: {environment.Describe()} on every triangle the flow does not " +
                  "wet (the CFD film wins wherever it is defined).");
        }

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

        // ---- Fluid properties at the INFLOW state, whatever the fluid-selection mode.
        //      The solver freezes ν, ρ, α, β at construction, so one temperature stands
        //      for the whole fluid, and the honest one is the stream's own — never the
        //      surroundings' ambient, which for a water circuit in air is a different
        //      fluid at a different state. The Reynolds guard reads this same state.
        //      Re-evaluating at the film temperature per outer iteration means
        //      rebuilding the solver: a named refinement, stated rather than skipped.
        var inflow = InflowState.PropertyTemperature(domain.Grid, cfd, thermalOptions);
        var fluid = fluidProps.AtTemperature(inflow.Temperature);
        log.Add($"Conjugate heat: {fluidProps.Name} properties at inflow T = " +
                $"{inflow.Temperature:F2} K ({inflow.Describe()}): " +
                $"ν = {fluid.KinematicViscosity:G3} m²/s, α = {fluid.ThermalDiffusivity:G3} m²/s, " +
                $"ρ = {fluid.Density:G4} kg/m³; the Reynolds guard reads the same state; " +
                "a film-temperature update per outer iteration is a named refinement.");
        if (!fluidProps.Covers(inflow.Temperature))
        {
            var (tMin, tMax) = fluidProps.TemperatureRange;
            log.Add($"WARNING: {inflow.Temperature:F2} K lies outside the {fluidProps.Name} " +
                    $"property table ({tMin:F0}–{tMax:F0} K); the end-row properties are used.");
        }

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
            // ---- Frozen-MOMENTUM, time-accurate ENERGY transient.
            //
            // The velocity field is solved once and held: momentum settles in L/U, which
            // is short against the march (checked and logged below). The fluid
            // TEMPERATURE is NOT held — a thermal front travelling down a channel is the
            // phenomenon itself, and a film frozen at t = 0 would hold every wall at the
            // inlet-time state. So the fluid energy equation marches in lockstep with the
            // solid: each solid step advances the fluid by the same interval on the frozen
            // velocities, then hands the solid the film that fluid state implies. The
            // exchange is EXPLICIT (the fluid sees the solid at the step start), first
            // order in the step — the same order as the backward-Euler solid march.
            var settings = solidInput.TransientThermal!;
            progress?.Report(new SolverProgress("Solving the flow field", 0.1));
            // Isothermal: at t = 0 the fluid is uniform, so the buoyancy force is exactly
            // zero and this IS the flow at the initial state the momentum field is frozen
            // at. Marching the energy equation here would freeze the momentum at the final
            // temperature state instead — and on a heated passage there may be no steady
            // coupled state to march to at all.
            var steadyFlow = flowSolver.SolveSteady(wallT, cancellationToken,
                (step, residual) => progress?.Report(new SolverProgress(
                    $"Flow march: step {step}, residual {residual:G2}", 0.1)),
                marchEnergy: false);
            LogFrozenFlowValidity(log, environment, domain, steadyFlow, solidInput);

            // Restart the fluid at the assembly initial temperature: before the stream
            // arrives, the passage holds fluid at the same state as the metal around it.
            flowSolver.ResetTemperature(startWallT);
            var latestFlow = flowSolver.Snapshot(steadyFlow.Steps, steadyFlow.Residual);
            double stepSeconds = settings.TimeStep;
            int subSteps = 0;
            var outletTrace = new List<string>();
            int quasiSteadySteps = 0;

            SurfaceFilmModel Schedule(int step, double time, IReadOnlyList<double> nodal)
            {
                if (step > 0)
                {
                    for (int idx = 0; idx < wallT.Length; idx++)
                    {
                        var wallTri = mesh.BoundaryTriangles[domain.WallFaces[idx].BoundaryTriangle];
                        wallT[idx] = (nodal[wallTri.A] + nodal[wallTri.B] + nodal[wallTri.C]) / 3.0;
                    }
                    var march = flowSolver.AdvanceEnergy(stepSeconds, wallT, cancellationToken);
                    subSteps += march.Steps;
                    if (march.Settled) quasiSteadySteps++;
                    latestFlow = flowSolver.Snapshot(steadyFlow.Steps, steadyFlow.Residual);
                }
                // The outlet temperature is what a heat-exchanger study reports, and it is
                // a TIME SERIES here — one line per step, so the arrival of the front is
                // visible in the log rather than only in the final frame.
                var ledger = FlowOutletReporter.Build(latestFlow, cfd, fluid.Density, fluid.SpecificHeat);
                var port = ledger.Outlets.OrderBy(x => x.MassFlow).FirstOrDefault();
                if (port is not null)
                    outletTrace.Add($"  t = {time,8:F3} s: outlet mixing-cup T = " +
                                    $"{port.MixedTemperature:F2} K");

                return BuildFilm(domain, latestFlow, fluid.ThermalConductivity, mesh, ambient,
                    claimed, environment, solidInput, nodeBases, TriangleMeans(mesh, nodal),
                    surroundings, nodal);
            }

            progress?.Report(new SolverProgress("Coupled transient march", 0.3));
            var thermal = new TransientThermalSolver().Solve(
                solidInput with { PrescribedFilmSchedule = Schedule }, progress, cancellationToken);
            if (surroundings is not null) log.AddRange(surroundings.DrainNotes());
            log.Add($"Conjugate transient: the fluid energy equation marched {subSteps:N0} CFL " +
                    "sub-steps on the frozen velocity field, one film handed to the solid per " +
                    "solid time step (explicit partitioned exchange, first order in the step).");
            if (quasiSteadySteps > 0)
                log.Add($"The fluid reached its steady state inside {quasiSteadySteps} of the solid " +
                        "steps (the step is long against the fluid residence time), so those steps " +
                        "stopped marching it early — the remaining interval changes nothing.");
            log.Add("Outlet temperature history:");
            log.AddRange(outletTrace);
            return Finish(thermal, latestFlow, domain, cfd, fluid, log);
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
            var nodalPrev = NodalTemperatures(solidOutput) ?? UniformNodal(mesh, startWallT);
            var film = BuildFilm(domain, flow, fluid.ThermalConductivity, mesh, ambient,
                claimed, environment, solidInput, nodeBases,
                surfaceT ?? UniformSurface(mesh, startWallT), surroundings, nodalPrev);

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
        if (surroundings is not null) log.AddRange(surroundings.DrainNotes());
        return Finish(solidOutput!, lastFlow!, domain, cfd, fluid, log);
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
        IReadOnlyList<int> nodeBases, double[] surfaceTemperature,
        EnvironmentBoundaryModel? surroundings, IReadOnlyList<double> nodalTemperature)
    {
        var film = WallFluxExtractor.Extract(domain, flow, fluidConductivity, mesh, ambient,
            claimed);

        if (surroundings is not null)
        {
            // Composition rule: the CFD film wins per TRIANGLE wherever it is defined
            // (that is the resolved answer), and the correlation film carries the rest.
            // NaN already means "not wetted" on one side and "user-claimed" on the other,
            // so the merge needs no extra bookkeeping.
            var outsideFilm = surroundings.Evaluate(nodalTemperature);
            var h = film.TriangleFilmCoefficient.ToArray();
            var tRef = film.TriangleReferenceTemperature!.ToArray();
            int carried = 0;
            for (int t = 0; t < h.Length; t++)
            {
                if (!double.IsNaN(h[t])) continue;
                double outside = outsideFilm.TriangleFilmCoefficient[t];
                if (double.IsNaN(outside)) continue;
                h[t] = outside;
                tRef[t] = ambient;
                carried++;
            }
            return film with
            {
                TriangleFilmCoefficient = h,
                TriangleReferenceTemperature = tRef,
                Origin = film.Origin + $" + Stage 1 surroundings on {carried} unwetted triangles"
            };
        }

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
        VoxelizedDomain domain, CfdSettings cfd, FluidState fluid, List<string> log)
    {
        log.AddRange(flow.Notes);
        var summary = new Dictionary<string, double>(
            thermal.Summary ?? new Dictionary<string, double>())
        {
            ["Peak fluid speed (m/s)"] = flow.MaxSpeed(),
            ["Flow steps"] = flow.Steps,
            ["Max flow divergence (1/s)"] = flow.MaxDivergence
        };

        FlowOutletReport? outlets = null;
        if (flow.Temperature is not null)
        {
            outlets = FlowOutletReporter.Build(flow, cfd, fluid.Density, fluid.SpecificHeat);
            log.AddRange(outlets.Describe());
            // "The outlet" of a report is the port the most mass leaves through; ports
            // are signed INTO the domain, so that is the most negative mass flow.
            var outlet = outlets.Outlets.OrderBy(p => p.MassFlow).FirstOrDefault();
            if (outlet is not null)
            {
                summary["Outlet temperature (K)"] = outlet.MixedTemperature;
                summary["Outlet mass flow (kg/s)"] = Math.Abs(outlet.MassFlow);
            }
            summary["Heat carried out by the stream (W)"] = outlets.HeatRemoved;
        }

        return new Result(thermal with
        {
            Log = log.Concat(thermal.Log).ToList(),
            Summary = summary
        }, flow, domain, outlets);
    }

    /// <summary>Every node at one temperature — the correlation film input before any
    /// solid solve has run.</summary>
    private static double[] UniformNodal(FeMesh mesh, double value)
    {
        var nodal = new double[mesh.NodeCount];
        Array.Fill(nodal, value);
        return nodal;
    }

    private static IReadOnlyList<double>? NodalTemperatures(SolveOutput? output) =>
        output is null ? null
            : ((Core.Results.NodalScalarField)output.Fields.First(f => f.Name == "Temperature")).Values;

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

    /// <summary>Per-boundary-triangle mean of a nodal field.</summary>
    private static double[] TriangleMeans(FeMesh mesh, IReadOnlyList<double> nodal)
    {
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
