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
/// Steady: an outer loop exchanges solid surface temperatures against the fluid's wall
/// film. The film's reference is the fluid cell NEXT to the wall, which follows the wall
/// itself, so the plain handoff closes only h_eff/g of the gap per pass (h_eff the real
/// film, g = 2k_f/h the half-cell conductance): a few percent on a grid that resolves
/// the boundary layer. The handoff is therefore Anderson-accelerated
/// (<see cref="AndersonMixer"/>), and the loop stops on two things together — the size of
/// the accelerated step, which estimates the remaining error, and the heat imbalance
/// between what the solid gives the film and what the fluid was told the walls give.
/// Radiation to ambient, when the environment asks for it, joins the film per iteration
/// through the factored coefficient evaluated per triangle.
/// </para>
/// <para>
/// Transient: FROZEN-MOMENTUM mode, a stated v1 decision — the velocity field is solved
/// once (isothermal, at the initial state) and held, while the fluid ENERGY equation
/// marches in lockstep with the solid. The exchange is IMPLICIT within each step: the
/// fluid interval is repeated with the wall temperatures the solid step produced until
/// the two agree, so the step is backward Euler for the coupled pair. (Handing the solid
/// a film built from the start-of-step wall instead made it behave as if its heat
/// capacity were C + g·A·Δt — a third too slow for an FR4 board on 1 mm cells at 10 s
/// steps, and worse on a finer grid.) Honest when the flow settles much faster than the
/// solid warms (t_flow = L/U ≪ τ_thermal, checked and logged); a momentum field that
/// follows the changing buoyancy is a named deferral.
/// </para>
/// </summary>
public static class ConjugateHeatStudy
{
    /// <summary>Outer-loop cap; an accelerated conjugate exchange that has not settled by
    /// then is not converging at all.</summary>
    public const int MaxOuterIterations = 30;

    /// <summary>Under-relaxation of the FIRST steady wall-temperature handoff, before the
    /// acceleration has any history (the plain handoff can ring when the film is strong).</summary>
    public const double Relaxation = 0.5;

    /// <summary>How many past handoffs the acceleration combines.</summary>
    public const int MixingDepth = 4;

    /// <summary>Steady stop: the heat the solid hands the film and the heat the fluid was
    /// told the walls give must agree to this fraction of the exchange.</summary>
    public const double FluxTolerance = 5e-3;

    /// <summary>Transient stop, per step: the wall temperatures of the coupled step are
    /// found to this fraction of how far the step moved them.</summary>
    public const double StepCouplingTolerance = 2e-3;

    /// <summary>Cap on coupled solves of one transient step.</summary>
    public const int MaxStepIterations = 30;

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
                "which handles radiation-only exchange.");

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
        //      It is built AFTER the voxelization, because which faces the flow wets is a
        //      property of the GEOMETRIC FACE, not of the triangle: a wall face is mapped
        //      to the nearest triangle centroid, so on a face meshed finer than the grid
        //      some triangles receive no wall face at all. Read per triangle, those took
        //      the outside-air film inside a water passage, and the bore's area entered
        //      the outside panels' lengths.
        if (!cfd.ResolvesSurroundings)
            EnvironmentBoundaryModel.ValidateMaterials(solidInput with { Environment = environment });

        // ---- Voxelize.
        progress?.Report(new SolverProgress("Voxelizing the fluid domain", 0.02));
        var bounds = Aabb.FromPoints(mesh.Nodes);
        var resolved = cfd.ResolveGrid(bounds, environment.FlowVelocity);
        var domain = Voxelizer.Voxelize(mesh, nodeBases, resolved, maxDegreeOfParallelism,
            cancellationToken);
        log.AddRange(domain.Notes);

        EnvironmentBoundaryModel? surroundings = null;
        if (!cfd.ResolvesSurroundings)
        {
            var wettedFaces = WettedFaceIds(domain, mesh);
            surroundings = EnvironmentBoundaryModel.Build(
                solidInput with { Environment = environment }, log, wettedFaces);
            log.Add(surroundings is null
                ? "Surroundings: every exterior face is claimed or wetted — the correlation " +
                  "film adds nothing."
                : $"Surroundings: {environment.Describe()} on every geometric face the flow does " +
                  $"not wet ({wettedFaces.Count} wetted face(s) belong to the CFD film alone).");
        }

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
            // exchange is IMPLICIT: the first pass of a step shows the fluid the wall at
            // the step start; the solid's answer then corrects the wall the fluid is
            // shown, the fluid interval is repeated from its saved start state, and so on
            // until the wall the fluid saw is the wall the solid produced. What is left is
            // the backward-Euler error of the coupled pair, first order in the step with
            // the PHYSICAL constant h_eff·A/C — not g·A/C, which grows as the grid refines.
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

            // State of the step being coupled.
            int coupledStep = 0;
            int stepPasses = 0;
            long totalPasses = 0;
            int worstPasses = 0;
            bool marchSettled = false;
            double[]? fluidAtStepStart = null;
            var wallAtStepStart = new double[wallT.Length];
            var solidWall = new double[wallT.Length];
            var mismatch = new double[wallT.Length];
            var mixer = new AndersonMixer(MixingDepth, 1.0);

            void TraceOutlet(double time)
            {
                // The outlet temperature is what a heat-exchanger study reports, and it is
                // a TIME SERIES here — one line per step, so the arrival of the front is
                // visible in the log rather than only in the final frame.
                var ledger = FlowOutletReporter.Build(latestFlow, cfd, fluid.Density, fluid.SpecificHeat);
                var port = ledger.Outlets.OrderBy(x => x.MassFlow).FirstOrDefault();
                if (port is not null)
                    outletTrace.Add($"  t = {time,8:F3} s: outlet mixing-cup T = " +
                                    $"{port.MixedTemperature:F2} K");
            }

            SurfaceFilmModel Schedule(int step, double time, IReadOnlyList<double> nodal)
            {
                if (step == 0)
                {
                    TraceOutlet(time);
                }
                else
                {
                    if (step != coupledStep)
                    {
                        // First pass of a new step: the fluid is shown the wall as the last
                        // step left it, and its own state is kept to come back to.
                        coupledStep = step;
                        stepPasses = 0;
                        mixer.Reset();
                        fluidAtStepStart = flowSolver.SaveTemperature();
                        WallTemperatures(mesh, domain, nodal, wallT);
                        Array.Copy(wallT, wallAtStepStart, wallT.Length);
                    }
                    else
                    {
                        // A repeat: same interval, from the same fluid state, with the wall
                        // temperatures StepAccepted corrected.
                        flowSolver.RestoreTemperature(fluidAtStepStart!);
                    }
                    var march = flowSolver.AdvanceEnergy(stepSeconds, wallT, cancellationToken);
                    subSteps += march.Steps;
                    marchSettled = march.Settled;
                    latestFlow = flowSolver.Snapshot(steadyFlow.Steps, steadyFlow.Residual);
                }

                return BuildFilm(domain, latestFlow, fluid.ThermalConductivity, mesh, ambient,
                    claimed, environment, solidInput, nodeBases, TriangleMeans(mesh, nodal),
                    surroundings, nodal);
            }

            bool StepAccepted(int step, IReadOnlyList<double> nodal)
            {
                stepPasses++;
                totalPasses++;
                WallTemperatures(mesh, domain, nodal, solidWall);
                double moved = 0;
                for (int idx = 0; idx < wallT.Length; idx++)
                {
                    mismatch[idx] = solidWall[idx] - wallT[idx];
                    moved = Math.Max(moved, Math.Abs(solidWall[idx] - wallAtStepStart[idx]));
                }
                // The accelerated step estimates how far the wall the fluid saw still is
                // from the coupled answer; the bare mismatch under-reads that by 1/(1 − ρ).
                var next = mixer.Next(wallT, mismatch);
                double correction = 0;
                for (int idx = 0; idx < wallT.Length; idx++)
                    correction = Math.Max(correction, Math.Abs(next[idx] - wallT[idx]));

                if (correction <= Math.Max(1e-5, StepCouplingTolerance * moved))
                {
                    worstPasses = Math.Max(worstPasses, stepPasses);
                    if (marchSettled) quasiSteadySteps++;
                    TraceOutlet(step * stepSeconds);
                    return true;
                }
                if (stepPasses >= MaxStepIterations)
                    throw new InvalidOperationException(
                        $"Time step {step}: the fluid and the solid did not agree on the wall " +
                        $"temperatures after {stepPasses} coupled solves (still {correction:G3} K " +
                        "apart). Reduce the time step.");
                Array.Copy(next, wallT, wallT.Length);
                return false;
            }

            progress?.Report(new SolverProgress("Coupled transient march", 0.3));
            var thermal = new TransientThermalSolver().Solve(
                solidInput with
                {
                    PrescribedFilmSchedule = Schedule,
                    PrescribedFilmStepAccepted = StepAccepted
                }, progress, cancellationToken);
            if (surroundings is not null) log.AddRange(surroundings.DrainNotes());
            log.Add($"Conjugate transient: the fluid energy equation marched {subSteps:N0} CFL " +
                    "sub-steps on the frozen velocity field. The exchange is implicit: each " +
                    "solid step was repeated with the fluid until the wall temperatures agreed " +
                    $"to {StepCouplingTolerance:P1} of the step's change ({totalPasses} coupled " +
                    $"solves in all, at most {worstPasses} in one step); the march is first " +
                    "order in the time step (backward Euler).");
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
        double imbalance = double.PositiveInfinity;
        var steadyMixer = new AndersonMixer(MixingDepth, Relaxation);
        var handoff = new double[wallT.Length];
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

            // The handoff and the outer convergence test. The mismatch is what the solid
            // produced against what the fluid was shown; every wall face carries the same
            // conductance g·h², so the heat imbalance between the two legs is the summed
            // mismatch against the summed wall-to-cell difference.
            double mismatchSum = 0, exchangeSum = 0, mismatchMax = 0;
            for (int idx = 0; idx < wallT.Length; idx++)
            {
                var wf = domain.WallFaces[idx];
                var tri = mesh.BoundaryTriangles[wf.BoundaryTriangle];
                double solidT = (nodalT[tri.A] + nodalT[tri.B] + nodalT[tri.C]) / 3.0;
                handoff[idx] = solidT - wallT[idx];
                mismatchSum += Math.Abs(handoff[idx]);
                mismatchMax = Math.Max(mismatchMax, Math.Abs(handoff[idx]));
                exchangeSum += Math.Abs(solidT - flow.Temperature![wf.FluidCell]);
            }
            imbalance = mismatchMax < 1e-9 || !(exchangeSum > 0) ? 0 : mismatchSum / exchangeSum;

            // The accelerated step estimates the remaining error (the plain mismatch
            // under-reads it by g/h_eff, which is how the loop used to stop 2–10 % low).
            var next = steadyMixer.Next(wallT, handoff);
            change = 0;
            for (int idx = 0; idx < wallT.Length; idx++)
                change = Math.Max(change, Math.Abs(next[idx] - wallT[idx]));

            double threshold = Math.Max(0.01, 1e-3 * MaxExcursion(wallT, ambient));
            if (outer >= 2 && change < threshold && imbalance < FluxTolerance) break;
            if (outer >= MaxOuterIterations)
                throw new InvalidOperationException(
                    $"The conjugate exchange did not settle: after {outer} outer iterations the " +
                    $"wall temperatures still move by {change:G3} K and the solid and fluid " +
                    $"heat rates differ by {imbalance:P2}. This usually means a strong " +
                    "film against a weak conduction path — refine the CFD grid or check the " +
                    "solid's conductivity and contacts.");
            Array.Copy(next, wallT, wallT.Length);
        }

        log.Add($"Conjugate exchange settled in {outer} outer iterations " +
                $"(last wall-temperature correction {change:G3} K; the solid and fluid heat " +
                $"rates agree to {imbalance:P2}; Anderson-accelerated handoff, depth {MixingDepth}).");
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
            // Composition rule: the CFD film wins wherever it is defined (that is the
            // resolved answer), and the correlation film carries the rest. The
            // surroundings model was built without the wetted GEOMETRIC faces, so a
            // triangle of a passage wall that no wall face mapped to stays NaN on both
            // sides — it exchanges through its neighbours — and NaN already means
            // "user-claimed" as well, so the merge needs no extra bookkeeping.
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
        // Skin inside a body-to-body contact faces the other body, not the surroundings:
        // it radiates nothing to ambient (the same rule the Stage 1 panels apply).
        var exposed = ContactInterface.ExposedFractions(mesh.BoundaryTriangles.Count,
            solidInput.ThermalContacts);
        int radiating = 0;
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            var tri = mesh.BoundaryTriangles[t];
            if (claimed.Contains(tri.FaceId)) continue;
            if (!(exposed[t] > 0)) continue;

            int body = BodyOfNode(nodeBases, tri.A);
            var material = solidInput.RegionMaterials?.GetValueOrDefault(body) ?? solidInput.Material;
            double? emissivity = material.Emissivity;
            if (emissivity is null)
                throw new InvalidOperationException(
                    $"Radiation is on but material '{material.Name}' has no emissivity. " +
                    "Set one (or switch radiation off) — a silent default would fake the balance.");
            if (emissivity.Value <= 0) continue;

            double ts = Math.Max(surfaceTemperature[t], 1.0);
            double hr = exposed[t] * ConvectionCorrelations.RadiativeFilmCoefficient(
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

    /// <summary>The geometric faces the resolved flow wets: those whose mapped wall faces
    /// cover at least half the face's own area. (The staircase covers a wetted face
    /// entirely, however fine its triangles; a stray wall face that found its nearest
    /// centroid across an edge — the end face around a bore's mouth — covers a sliver and
    /// does not make that face a wetted one.)</summary>
    internal static IReadOnlySet<int> WettedFaceIds(VoxelizedDomain domain, FeMesh mesh)
    {
        double faceArea = domain.Grid.H * domain.Grid.H;
        var covered = new Dictionary<int, double>();
        foreach (var wf in domain.WallFaces)
        {
            int id = mesh.BoundaryTriangles[wf.BoundaryTriangle].FaceId;
            covered[id] = covered.GetValueOrDefault(id) + faceArea;
        }
        var area = new Dictionary<int, double>();
        foreach (var tri in mesh.BoundaryTriangles)
            if (covered.ContainsKey(tri.FaceId))
                area[tri.FaceId] = area.GetValueOrDefault(tri.FaceId)
                                   + WallFluxExtractor.TriangleArea(mesh, tri);
        var faces = new HashSet<int>();
        foreach (var (id, wet) in covered)
            if (wet >= 0.5 * area[id]) faces.Add(id);
        return faces;
    }

    /// <summary>The solid surface temperature each wall face sees: the mean of its
    /// boundary triangle's nodes.</summary>
    private static void WallTemperatures(FeMesh mesh, VoxelizedDomain domain,
        IReadOnlyList<double> nodal, double[] target)
    {
        for (int idx = 0; idx < target.Length; idx++)
        {
            var tri = mesh.BoundaryTriangles[domain.WallFaces[idx].BoundaryTriangle];
            target[idx] = (nodal[tri.A] + nodal[tri.B] + nodal[tri.C]) / 3.0;
        }
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
