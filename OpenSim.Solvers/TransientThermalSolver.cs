using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers.Environment;

namespace OpenSim.Solvers;

/// <summary>
/// Transient heat conduction over TET4 elements: ρc_p·∂T/∂t = ∇·(k∇T) + q, integrated
/// with backward Euler. BE is unconditionally stable and damps every mode — a step change
/// (the dominant use case) never produces the ringing Crank–Nicolson shows on stiff modes
/// at practical step sizes; its O(Δt) truncation is controlled by the step size instead.
/// It is MONOTONE only with a suitable capacity matrix: with the consistent one a step
/// below about h²/(6α) makes the node next to a heated one move the wrong way first, so
/// the capacity is lumped there (<see cref="ThermalCapacity"/>, automatic by default and
/// logged). Each step solves the SPD system
/// (M/Δt + K)·Tⁿ = (M/Δt)·Tⁿ⁻¹ + f with the shared Jacobi-CG, reduced once and
/// warm-started from the previous step. No Dirichlet/Robin condition is required:
/// M/Δt regularizes the matrix, so a purely adiabatic heating ramp is well-posed.
/// </summary>
public sealed class TransientThermalSolver : ISolver
{
    /// <summary>Hard cap on stored result frames — beyond this, memory (every frame
    /// keeps full nodal temperature + flux) outweighs any scrubbing benefit.</summary>
    private const int MaxStoredFrames = 500;

    public string Name => "Transient heat conduction (thermal)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (input.Mesh.IsQuadratic)
            throw new InvalidOperationException(
                "The transient thermal solver supports linear tetrahedral (TET4) meshes only; " +
                "re-generate the mesh with linear tetrahedral elements.");

        var settings = input.TransientThermal ?? throw new InvalidOperationException(
            "Transient thermal settings (initial temperature, duration, time step) are missing.");
        if (settings.TimeStep <= 0)
            throw new InvalidOperationException("The time step must be positive.");
        if (settings.Duration < settings.TimeStep)
            throw new InvalidOperationException("The duration must be at least one time step.");
        if (settings.InitialTemperature <= 0)
            throw new InvalidOperationException("The initial temperature must be positive (absolute kelvin).");
        if (settings.OutputStride < 0)
            throw new InvalidOperationException("The output stride cannot be negative.");
        if (settings.InitialState is { } start)
        {
            if (start.NodalTemperature.Count != input.Mesh.NodeCount)
                throw new InvalidOperationException(
                    $"The initial state has {start.NodalTemperature.Count} temperatures but the mesh has {input.Mesh.NodeCount} nodes.");
            foreach (double kelvin in start.NodalTemperature)
                if (!(kelvin > 0) || double.IsInfinity(kelvin))
                    throw new InvalidOperationException("Every initial temperature must be positive and finite (absolute kelvin).");
        }
        if (settings.Integrator != TimeIntegrator.BackwardEuler)
        {
            if (settings.Integrator == TimeIntegrator.Adaptive && !(settings.Tolerance > 0))
                throw new InvalidOperationException("The adaptive integrator's tolerance must be positive.");
            string? nonlinear = input.Environment is not null ? "an environment"
                : input.PrescribedFilmSchedule is not null ? "a film schedule"
                : input.ElementHeatSourceSchedule is not null ? "a heat-source schedule"
                : input.SurfaceRadiation is { Count: > 0 } ? "surface-to-surface radiation"
                : null;
            if (nonlinear is not null)
                throw new InvalidOperationException(
                    $"The {(settings.Integrator == TimeIntegrator.Adaptive ? "adaptive" : "TR-BDF2")} integrator takes linear models only, " +
                    $"and this one has {nonlinear}. Use backward Euler.");
        }
        settings.PowerProfile?.Validate();
        if (input.ElementHeatSourceSchedule is not null && settings.PowerProfile is { Kind: not PowerProfileKind.Constant })
            throw new InvalidOperationException(
                "A heat-source schedule sets the source of every step itself; it does not take a power profile.");

        var (steps, stride) = PlanSteps(settings);
        int stored = 2 + (steps - 1) / stride;   // initial state + strided steps + final
        if (stored > MaxStoredFrames)
            throw new InvalidOperationException(
                $"This run would store {stored} result frames ({steps} steps at stride {stride}); " +
                $"the limit is {MaxStoredFrames}. Increase OutputStride (or the time step).");

        input.Material.ValidateThermalTransient();
        if (input.RegionMaterials is not null)
            foreach (var material in input.RegionMaterials.Values)
                material.ValidateThermalTransient();

        if (input.ElementHeatSource is not null && input.ElementHeatSource.Count != input.Mesh.ElementCount)
            throw new InvalidOperationException(
                $"ElementHeatSource has {input.ElementHeatSource.Count} entries but the mesh has " +
                $"{input.Mesh.ElementCount} elements.");

        foreach (var bc in input.BoundaryConditions)
        {
            if (bc is not (FixedTemperature or HeatFlux or Convection))
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' ({bc.GetType().Name}) does not apply to a thermal solve. " +
                    "Use fixed temperatures, heat fluxes, or convection.");
            BoundaryScope.Validate(bc, input.Mesh);
            if (bc is Convection c) ConvectionTerms.Validate(c);
        }

        ScalarSolverHelpers.ValidateConductivityTensor(input);
        EnvironmentBoundaryModel.ValidateMaterials(input);
        HeatConductionSolver.ValidatePrescribedFilm(input);
        SurfaceRadiationExchange.Validate(input);
        ThermalNetworkTerms.Validate(input);

        // No anchoring requirement here, unlike the steady solver: M/Δt regularizes every
        // step, so a body that only stores and receives heat is perfectly well-posed.
        if (input.ThermalContacts is not null)
            foreach (var contact in input.ThermalContacts)
                contact.Validate(input.Mesh.NodeCount);
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;
        var settings = input.TransientThermal!;
        double dt = settings.TimeStep;
        var (steps, stride) = PlanSteps(settings);

        progress?.Report(new SolverProgress("Assembling matrices", 0.02));
        var assembler = ScalarDiffusionAssembler.Thermal(input);
        var robin = new List<ScalarDiffusionAssembler.RobinTerm>();
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
            foreach (var (t, h) in ConvectionTerms.Triangles(mesh, convection))
                robin.Add(new ScalarDiffusionAssembler.RobinTerm(t, h));
        // Contacts enter the conduction matrix only: an interface exchanges heat, it stores none.
        var conduction = assembler.AssembleStiffness(robin, input.ThermalContacts, cancellationToken);
        // Lumped networks (parts with their capacities, an enclosure's air) are unknowns
        // after the mesh's nodes, in the conduction and the capacity matrix alike.
        var networks = ThermalNetworkTerms.Build(input, log);
        if (networks is not null) conduction = networks.Augment(conduction);
        int dofs = conduction.RowCount;
        Func<int, double> capacity = el =>
        {
            var m = input.MaterialOf(el);
            return m.Density * m.SpecificHeat!.Value;
        };
        // Which capacity matrix: the consistent one is not monotone below Δt ≈ h²/(6α).
        var (monotoneStep, tightest) = MonotoneTimeStep(input);
        // TR-BDF2's stages solve with M/(d·h) + K, d = 1 − 1/√2; the adaptive steps are not
        // known in advance and start small, so they count as below the bound.
        double effectiveStep = settings.Integrator switch
        {
            TimeIntegrator.TrBdf2 => TrBdf2Implicit * dt,
            TimeIntegrator.Adaptive => 0,
            _ => dt
        };
        bool belowBound = effectiveStep < monotoneStep;
        bool lumped = settings.Capacity == ThermalCapacity.Lumped
                      || (settings.Capacity == ThermalCapacity.Automatic && belowBound);
        var mass = lumped
            ? assembler.AssembleLumpedMass(capacity, cancellationToken)
            : assembler.AssembleMass(capacity, cancellationToken);
        if (networks is not null) mass = networks.AugmentMass(mass);
        if (lumped && settings.Capacity == ThermalCapacity.Automatic)
            log.Add(settings.Integrator == TimeIntegrator.Adaptive
                ? "Heat capacity: LUMPED. The adaptive steps are not known in advance and start small, and below " +
                  $"{monotoneStep:g3} s (h²/6α of the {tightest}) the consistent capacity matrix lets a node next to a " +
                  "heated one move the wrong way first."
                : $"Heat capacity: LUMPED. The {(settings.Integrator == TimeIntegrator.TrBdf2 ? "TR-BDF2 stage step" : "time step")} " +
                  $"{effectiveStep:g3} s is below {monotoneStep:g3} s " +
                  $"(h²/6α of the {tightest}), where the consistent capacity matrix lets a node " +
                  "next to a heated one move the wrong way first.");
        else if (lumped)
            log.Add("Heat capacity: lumped (as set).");
        else if (belowBound)
            log.Add($"WARNING: heat capacity is the consistent matrix (as set) and the time step " +
                    $"{effectiveStep:g3} s is below {monotoneStep:g3} s (h²/6α of the {tightest}): nodal " +
                    "temperatures next to a sudden change can overshoot their bounds by about 1 % " +
                    "of the change. Use the lumped capacity or a longer step.");

        // System matrix of one backward-Euler step: A = M/Δt + K (SPD; the capacity term
        // regularizes it even without any Dirichlet/Robin condition).
        var systemBuilder = new SparseMatrixBuilder(dofs, dofs);
        AddScaled(systemBuilder, mass, 1.0 / dt);
        AddScaled(systemBuilder, conduction, 1.0);
        var system = systemBuilder.Build();
        log.Add($"Assembled {system.RowCount} DOF system, {system.NonZeroCount} non-zeros" +
                (robin.Count > 0 ? $" including {robin.Count} convective surface triangles." : "."));
        HeatConductionSolver.LogContacts(input, log);

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.05));
        // A source schedule owns the volumetric source: the fixed loads leave it out, and each
        // pass adds the schedule's own.
        var sourceSchedule = input.ElementHeatSourceSchedule;
        var constantLoads = sourceSchedule is null
            ? ScalarSolverHelpers.AssembleThermalLoads(input, log)
            : ScalarSolverHelpers.AssembleThermalLoads(input with { ElementHeatSource = null }, log);
        if (sourceSchedule is not null)
            log.Add("Heat-source schedule: the volumetric source is re-evaluated every pass" +
                    (input.HeatSourceStepAccepted is null ? "." : ", and each step is repeated until the schedule's owner accepts it."));

        // A power profile scales the heat flows and the volumetric source step by step; the
        // convection conditions' ambient terms are not sources and stay as they are. Each
        // step takes the profile's MEAN over the step, so the energy put in is the
        // profile's own even when a pulse edge falls inside a step. A network node's power
        // follows its own profile when it has one, the solve's otherwise.
        var profile = settings.PowerProfile is { Kind: not PowerProfileKind.Constant } varying ? varying : null;
        double[]? sourceLoads = null, ambientLoads = null, stepLoads = null;
        if (profile is not null)
        {
            (sourceLoads, ambientLoads) = ScalarSolverHelpers.AssembleThermalLoadParts(input, new List<string>());
            log.Add($"Power profile: {profile.Describe()}; each step uses its mean over the step.");
            if (dt > profile.ShortestInterval())
                log.Add($"WARNING: the time step {dt:g3} s is longer than the profile's shortest interval " +
                        $"({profile.ShortestInterval():g3} s): the energy is right but the ripple within a " +
                        "pulse is not resolved. Use a step several times shorter than the pulse.");
        }
        bool varyingPowers = false;
        if (networks is not null)
        {
            constantLoads = networks.AugmentLoads(constantLoads, null);
            if (sourceLoads is not null) sourceLoads = networks.AugmentLoads(sourceLoads, null);
            if (ambientLoads is not null) ambientLoads = networks.AugmentLoads(ambientLoads, null);
            varyingPowers = networks.PoweredNodes().Any() && (profile is not null || networks.HasProfiles);
            if (!varyingPowers) networks.AddPowers(constantLoads, _ => 1.0);
            else if (networks.HasProfiles)
                log.Add("Network node powers follow their own profiles, each step at the profile's mean over the step.");
        }
        if (profile is not null || varyingPowers) stepLoads = new double[dofs];
        Func<ThermalNetworkNode, double> ScaleOver(double from, double to) =>
            node => (node.PowerProfile ?? profile)?.Mean(from, to) ?? 1.0;

        // A prescribed film is FIXED over the whole transient (the frozen-flow contract of
        // the conjugate study): fold its Robin terms into the step matrix and loads once,
        // and the plain linear time loop below runs unchanged — no Picard needed.
        // A SCHEDULE means the film changes every step, so it must NOT be folded into the
        // base system here: the base stays film-free and each step folds its own.
        var filmSchedule = input.PrescribedFilmSchedule;
        SurfaceFilmModel? prescribedFilm = input.PrescribedFilm;
        if (filmSchedule is not null)
        {
            log.Add("Prescribed film schedule: the film is re-evaluated every time step " +
                    "(time-accurate conjugate coupling), so the step matrix is refolded per step" +
                    (input.PrescribedFilmStepAccepted is null
                        ? "."
                        : ", and each step is repeated until the schedule's owner accepts it."));
        }
        else if (prescribedFilm is not null)
        {
            system = EnvironmentThermalTerms.WithFilm(system, mesh, prescribedFilm);
            constantLoads = EnvironmentThermalTerms.WithFilmLoads(constantLoads, mesh, prescribedFilm);
            if (ambientLoads is not null)
                ambientLoads = EnvironmentThermalTerms.WithFilmLoads(ambientLoads, mesh, prescribedFilm);
            log.Add($"Prescribed film ({prescribedFilm.Origin}): {prescribedFilm.WettedCount} wetted " +
                    "triangles, held constant over the transient.");
        }
        var prescribed = new Dictionary<int, double>();
        foreach (var temperature in input.BoundaryConditions.OfType<FixedTemperature>())
        {
            var nodes = mesh.GetScopeNodes(temperature);
            foreach (int node in nodes)
                prescribed[node] = temperature.Kelvin;
            log.Add($"Temperature '{temperature.Name}': {temperature.Kelvin:g4} K on {nodes.Count} nodes.");
        }
        networks?.AddPrescribed(prescribed);
        var reduced = ConstrainedSystemSolver.Reduce(system, prescribed, allowUnconstrained: true);

        // Initial state: uniform (network nodes included), or the given field and network
        // nodes, except nodes held at their prescribed values.
        var temperature_ = new double[dofs];
        Array.Fill(temperature_, settings.InitialTemperature);
        if (settings.InitialState is { } start)
        {
            for (int i = 0; i < mesh.NodeCount; i++) temperature_[i] = start.NodalTemperature[i];
            if (start.Networks is { } startNetworks && networks is not null) networks.ApplyStart(temperature_, startNetworks);
            log.Add($"Initial state: the given field, {start.NodalTemperature.Min():g4} … {start.NodalTemperature.Max():g4} K" +
                    (start.Networks is { Count: > 0 } ? ", network nodes as given." : "."));
        }
        foreach (var (node, value) in prescribed)
            temperature_[node] = value;

        var environment = EnvironmentBoundaryModel.Build(input, log);
        var radiation = SurfaceRadiationExchange.Build(input, log, cancellationToken);
        var radiationValues = radiation is null ? null : new double[radiation.Pattern.Count];
        double? AirOf(IReadOnlyList<double> state) => networks?.AirDof is not null ? networks.AirTemperature(state) : null;
        var film = filmSchedule?.Invoke(0, 0.0, temperature_) ?? prescribedFilm
                   ?? environment?.Evaluate(temperature_, ambientTemperature: AirOf(temperature_));
        var frameScale = ScaleOver(0, 0);
        var frames = new List<ResultFrame>
        {
            MakeFrame(0.0, temperature_, mesh, assembler, input, film, networks, frameScale)
        };
        var cg = new ConjugateGradientSolver
        {
            Tolerance = 1e-10,
            MaxIterations = Math.Max(4 * reduced.FreeCount, 1000)
        };

        if (settings.Integrator != TimeIntegrator.BackwardEuler)
        {
            // The linear path (Validate refused every surface model that moves with the
            // temperature): a fixed film is part of the conduction matrix, its loads are
            // already in the constant and ambient loads.
            double[] LoadsOver(double from, double to)
            {
                if (stepLoads is null) return constantLoads;
                var result = new double[dofs];
                if (profile is not null)
                {
                    double scale = profile.Mean(from, to);
                    for (int i = 0; i < dofs; i++) result[i] = ambientLoads![i] + scale * sourceLoads![i];
                }
                else Array.Copy(constantLoads, result, dofs);
                if (varyingPowers) networks!.AddPowers(result, ScaleOver(from, to));
                return result;
            }
            var breakpoints = new List<double>();
            double end = steps * dt;
            if (profile is not null) breakpoints.AddRange(profile.Breakpoints(end));
            if (input.ThermalNetworks is not null)
                foreach (var node in input.ThermalNetworks.SelectMany(n => n.Nodes))
                    if (node.PowerProfile is { } own) breakpoints.AddRange(own.Breakpoints(end));
            var conductionWithFilm = prescribedFilm is null ? conduction : EnvironmentThermalTerms.WithFilm(conduction, mesh, prescribedFilm);
            return SolveHigherOrder(input, settings, log, conductionWithFilm, mass, prescribed, temperature_, frames,
                LoadsOver, ScaleOver, networks, assembler, film, breakpoints, progress, cancellationToken);
        }
        // On the linear path every step solves the SAME reduced matrix, so its Jacobi
        // preconditioner is built once here instead of per step. A film or environment
        // refolds the matrix each pass and gets a fresh one; the numbers are identical
        // either way, only the rebuild of an unchanged diagonal is avoided.
        double[] reducedInvDiag = ConjugateGradientSolver.BuildJacobiPreconditioner(reduced.Reduced);

        // The film paths reduce ONCE too, and rewrite only the entries the film touches. Built
        // lazily: a run with neither a schedule nor an environment must not pay for it. An
        // environment driving toward a network's unknown (an enclosure's air) couples the
        // film to that column, which the slots hold as well.
        FilmUpdatableSystem? updatable = filmSchedule is not null || environment is not null || radiation is not null
            ? FilmUpdatableSystem.Prepare(system, mesh, prescribed, environment is not null ? networks?.AirDof : null, radiation?.Pattern)
            : null;
        var massTimesT = new double[dofs];
        var fullRhs = new double[dofs];
        long totalIterations = 0;
        long nonlinearIterations = 0;
        long couplingIterations = 0;

        for (int n = 1; n <= steps; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mass.Multiply(temperature_, massTimesT);
            double[] loads = constantLoads;
            if (stepLoads is not null)
            {
                if (profile is not null)
                {
                    double scale = profile.Mean((n - 1) * dt, n * dt);
                    for (int i = 0; i < stepLoads.Length; i++)
                        stepLoads[i] = ambientLoads![i] + scale * sourceLoads![i];
                }
                else Array.Copy(constantLoads, stepLoads, dofs);
                frameScale = ScaleOver((n - 1) * dt, n * dt);
                if (varyingPowers) networks!.AddPowers(stepLoads, frameScale);
                loads = stepLoads;
            }

            // Without an environment this loop runs exactly once and reuses the system
            // reduced before the time loop — the linear path, unchanged to the last bit.
            // With one, each pass re-evaluates the surface film coefficients at the latest
            // iterate: the lagged-coefficient fixed point of the backward-Euler step.
            var iterate = temperature_;
            int picard = 0;
            double change = 0;
            var stepLoadsBase = loads;
            while (true)
            {
                picard++;
                loads = stepLoadsBase;
                if (sourceSchedule is not null)
                    loads = WithSource(stepLoadsBase, mesh, sourceSchedule(n, n * dt, iterate));
                ConstrainedSystemSolver.ReducedSystem stepSystem;
                if (filmSchedule is not null)
                {
                    // Linear step, like the fixed film — only the film's own numbers move, so
                    // there is nothing to iterate on: pull this step's film, rewrite the
                    // surface entries, solve once.
                    film = filmSchedule(n, n * dt, iterate);
                    updatable!.Apply(film);
                    stepSystem = updatable.System;
                    var scheduledLoads = EnvironmentThermalTerms.WithFilmLoads(loads, mesh, film);
                    for (int i = 0; i < fullRhs.Length; i++)
                        fullRhs[i] = massTimesT[i] / dt + scheduledLoads[i];
                }
                else if (environment is null && radiation is null)
                {
                    stepSystem = reduced;
                    for (int i = 0; i < fullRhs.Length; i++)
                        fullRhs[i] = massTimesT[i] / dt + loads[i];
                }
                else
                {
                    // The environment's film and the radiation exchange, both linearized at
                    // the latest iterate.
                    double? air = AirOf(iterate);
                    film = environment?.Evaluate(iterate, tangent: true, ambientTemperature: air);
                    var filmLoads = film is null ? (double[])loads.Clone()
                        : networks is null
                            ? EnvironmentThermalTerms.WithFilmLoads(loads, mesh, film)
                            : networks.WithFilmLoads(loads, film, air ?? 0);
                    radiation?.Linearize(iterate, radiationValues!, filmLoads);
                    updatable!.Apply(film, radiationValues);
                    stepSystem = updatable.System;
                    for (int i = 0; i < fullRhs.Length; i++)
                        fullRhs[i] = massTimesT[i] / dt + filmLoads[i];
                }

                var rhs = stepSystem.ReduceLoads(fullRhs);
                var free = stepSystem.Restrict(iterate);   // warm start from the previous step
                double[] invDiag = ReferenceEquals(stepSystem, reduced)
                    ? reducedInvDiag
                    : ConjugateGradientSolver.BuildJacobiPreconditioner(stepSystem.Reduced);
                var iterations = cg.Solve(stepSystem.Reduced, rhs, free, invDiag, cancellationToken);
                if (!iterations.Converged)
                    throw new InvalidOperationException(
                        $"Time step {n} did not converge after {iterations.Iterations} CG iterations " +
                        $"(residual {iterations.ResidualNorm:g3}). Check materials and mesh quality.");
                totalIterations += iterations.Iterations;
                var next = stepSystem.Expand(free);

                bool sourceAccepted = true;
                if (sourceSchedule is not null && input.HeatSourceStepAccepted is { } sourceCheck)
                {
                    couplingIterations++;
                    sourceAccepted = sourceCheck(n, next);
                    if (!sourceAccepted && picard >= MaxSourceIterations)
                        throw new InvalidOperationException(
                            $"Time step {n}: the heat source and the temperature did not agree after {picard} solves. " +
                            "Reduce the time step.");
                }

                if (filmSchedule is not null && input.PrescribedFilmStepAccepted is { } accepted)
                {
                    // Implicit coupling owned by the schedule's author: the step is solved
                    // again, from the same start state, until the film it was given agrees
                    // with the temperatures it produced.
                    iterate = next;
                    couplingIterations++;
                    if (accepted(n, next) && sourceAccepted) break;
                    continue;
                }
                if (environment is null && radiation is null)
                {
                    iterate = next;
                    if (sourceAccepted) break;
                    continue;
                }
                nonlinearIterations++;
                change = EnvironmentThermalTerms.MaxChange(iterate, next);
                iterate = next;
                if (picard >= 2 && change < EnvironmentThermalTerms.Threshold(iterate) && sourceAccepted) break;
                if (picard >= EnvironmentThermalTerms.MaxTransientIterations)
                    throw new InvalidOperationException(
                        $"Time step {n}: the {(environment is null ? "radiation exchange" : "environment's surface coefficients")} did not settle " +
                        $"after {picard} iterations (last change {change:g3} K). Reduce the time " +
                        "step — a step that moves the surface temperature far in one go makes the " +
                        "film coefficients chase it.");
            }
            temperature_ = iterate;

            if (n % stride == 0 || n == steps)
            {
                // The film shown is the film as it IS at this state (the step was solved
                // with radiation's Newton tangent, a different pair of numbers for the
                // same exchange).
                if (environment is not null) film = environment.Evaluate(temperature_, ambientTemperature: AirOf(temperature_));
                frames.Add(MakeFrame(n * dt, temperature_, mesh, assembler, input, film, networks, frameScale));
            }
            progress?.Report(new SolverProgress($"Time step {n}/{steps}", 0.05 + 0.95 * n / steps));
        }

        double endTime = steps * dt;
        log.Add($"Backward Euler: {steps} steps of Δt = {dt:g4} s to t = {endTime:g4} s " +
                $"({totalIterations} CG iterations total); {frames.Count} frames stored.");
        if (couplingIterations > 0)
            log.Add($"Film schedule: {couplingIterations} coupled solves over {steps} steps " +
                    $"({(double)couplingIterations / steps:F1} per step).");
        if (environment is null && radiation is not null)
            log.Add($"Surface radiation: {nonlinearIterations} nonlinear iterations over {steps} steps " +
                    $"({(double)nonlinearIterations / steps:F1} per step).");
        if (radiation is not null) log.AddRange(radiation.Describe(temperature_));
        if (environment is not null)
        {
            log.Add($"Environment: {nonlinearIterations} nonlinear iterations over {steps} steps " +
                    $"({(double)nonlinearIterations / steps:F1} per step).");
            var (minFilm, maxFilm) = film!.CoefficientRange();
            log.Add($"Final film coefficient over the exposed surface: " +
                    $"{minFilm:g4} … {maxFilm:g4} W/(m²·K).");
            foreach (string note in environment.DrainNotes())
                log.Add("  " + note);
        }
        var meshFinal = networks is null ? temperature_ : networks.MeshPart(temperature_);
        double min = meshFinal.Min(), max = meshFinal.Max();
        log.Add($"Final temperature range: {min:g4} … {max:g4} K.");
        if (frames[^1].Networks is { } finalStates)
            foreach (var state in finalStates)
                log.Add($"Thermal network '{state.Network}' at the end: " + string.Join(", ",
                    state.Nodes.Select((name, i) => $"{name} {state.Kelvin[i]:F2} K")) + ".");

        progress?.Report(new SolverProgress("Done", 1.0));
        return new SolveOutput
        {
            Fields = frames[^1].Fields,   // default frame: the final (most steady) state
            Log = log,
            Frames = frames,
            FrameAxis = "Time",
            Networks = frames[^1].Networks,
            Summary = new Dictionary<string, double>
            {
                ["Final min temperature (K)"] = min,
                ["Final max temperature (K)"] = max,
                ["Time steps"] = steps,
                ["End time (s)"] = endTime
            }
        };
    }

    /// <summary>Cap on the solves of one step when a heat-source schedule repeats it.</summary>
    private const int MaxSourceIterations = 60;

    /// <summary>TR-BDF2's stage fraction γ = 2 − √2.</summary>
    private static readonly double TrBdf2Stage = 2 - Math.Sqrt(2);

    /// <summary>d = γ/2 = 1 − 1/√2: both stages solve with M + d·h·K.</summary>
    private static readonly double TrBdf2Implicit = 1 - 1 / Math.Sqrt(2);

    /// <summary>
    /// The linear model stepped with TR-BDF2: a trapezoidal stage to t + γh, then BDF2 to
    /// t + h, both with the matrix M + d·h·K. The trapezoid's load is the loads' mean over
    /// its stage and BDF2's is set so the step takes in the loads' integral exactly, so a
    /// pulse inside a step still delivers its energy. At a fixed step this is
    /// the run; adaptive, every step is also taken as two half steps, the difference / 3 is
    /// the local error of the pair (second order: the halves are 2² − 1 times closer), the
    /// pair is kept when that is within the tolerance, and the next step is scaled by
    /// 0.9·(tolerance/error)^⅓ (between ×0.2 and ×4). Adaptive steps land on every stored
    /// frame's time and every edge of the power profiles.
    /// </summary>
    private static SolveOutput SolveHigherOrder(SolveInput input, TransientThermalSettings settings, List<string> log,
        CsrMatrix conduction, CsrMatrix mass, IReadOnlyDictionary<int, double> prescribed, double[] start,
        List<ResultFrame> frames, Func<double, double, double[]> loadsOver,
        Func<double, double, Func<ThermalNetworkNode, double>> scaleOver, ThermalNetworkTerms? networks,
        ScalarDiffusionAssembler assembler, SurfaceFilmModel? film, IReadOnlyList<double> breakpoints,
        IProgress<SolverProgress>? progress, CancellationToken cancellationToken)
    {
        var mesh = input.Mesh;
        double dt = settings.TimeStep;
        var (steps, stride) = PlanSteps(settings);
        double end = steps * dt;
        bool adaptive = settings.Integrator == TimeIntegrator.Adaptive;

        // Prescribed temperatures are constant in time, so their columns move to the loads
        // of K only; M's reduced block is what multiplies the rates.
        var stiffness = ConstrainedSystemSolver.Reduce(conduction, prescribed, allowUnconstrained: true);
        var k = stiffness.Reduced;
        var m = ConstrainedSystemSolver.Reduce(mass, prescribed, allowUnconstrained: true).Reduced;
        int free = stiffness.FreeCount;
        var cg = new ConjugateGradientSolver { Tolerance = 1e-11, MaxIterations = Math.Max(4 * free, 1000) };
        var matrices = new Dictionary<double, (CsrMatrix Matrix, double[] InverseDiagonal)>();
        (CsrMatrix Matrix, double[] InverseDiagonal) StepMatrix(double dh)
        {
            if (matrices.TryGetValue(dh, out var cached)) return cached;
            if (matrices.Count > 8) matrices.Clear();
            var builder = new SparseMatrixBuilder(free, free);
            AddScaled(builder, m, 1.0);
            AddScaled(builder, k, dh);
            var matrix = builder.Build();
            return matrices[dh] = (matrix, ConjugateGradientSolver.BuildJacobiPreconditioner(matrix));
        }

        var work = new double[free];
        var mTheta = new double[free];
        var mStage = new double[free];
        var rhs = new double[free];
        long iterations = 0;
        double a = 1 / (TrBdf2Stage * (2 - TrBdf2Stage));
        double b = (1 - TrBdf2Stage) * (1 - TrBdf2Stage) * a;
        double[] Step(double t, double h, double[] theta)
        {
            double dh = TrBdf2Implicit * h;
            var (matrix, inverseDiagonal) = StepMatrix(dh);
            // Stage loads: the trapezoid takes the mean over its stage; BDF2's load is set so
            // the step as a whole takes in exactly the loads' integral (with K = 0 the step
            // gives h·(a·γ·f₁ + d·f₂), which must equal h·(γ·f̄₁ + (1 − γ)·f̄₂)). For a load
            // varying linearly that is f at t + h, which is what BDF2 wants — second order
            // kept — and a pulse edge inside the step still delivers its energy.
            var first = stiffness.ReduceLoads(loadsOver(t, t + TrBdf2Stage * h));
            var secondMean = stiffness.ReduceLoads(loadsOver(t + TrBdf2Stage * h, t + h));
            var second = new double[free];
            for (int i = 0; i < free; i++)
                second[i] = ((TrBdf2Stage - a * TrBdf2Stage) * first[i] + (1 - TrBdf2Stage) * secondMean[i]) / TrBdf2Implicit;
            k.Multiply(theta, work);
            m.Multiply(theta, mTheta);
            for (int i = 0; i < free; i++)
                rhs[i] = mTheta[i] - dh * work[i] + TrBdf2Stage * h * first[i];
            var stage = (double[])theta.Clone();
            var one = cg.Solve(matrix, rhs, stage, inverseDiagonal, cancellationToken);
            m.Multiply(stage, mStage);
            for (int i = 0; i < free; i++)
                rhs[i] = a * mStage[i] - b * mTheta[i] + dh * second[i];
            var next = (double[])stage.Clone();
            var two = cg.Solve(matrix, rhs, next, inverseDiagonal, cancellationToken);
            if (!one.Converged || !two.Converged)
                throw new InvalidOperationException(
                    $"The step to t = {t + h:g4} s did not converge (residual {Math.Max(one.ResidualNorm, two.ResidualNorm):g3}). " +
                    "Check materials and mesh quality.");
            iterations += one.Iterations + two.Iterations;
            return next;
        }

        var theta = stiffness.Restrict(start);
        int accepted = 0, rejected = 0;
        double shortest = double.PositiveInfinity, longest = 0, worstError = 0;
        void Store(double time, double lastStep)
        {
            var full = stiffness.Expand(theta);
            frames.Add(MakeFrame(time, full, mesh, assembler, input, film, networks, scaleOver(time - lastStep, time)));
        }

        if (!adaptive)
        {
            for (int n = 1; n <= steps; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                theta = Step((n - 1) * dt, dt, theta);
                accepted++;
                if (n % stride == 0 || n == steps) Store(n * dt, dt);
                progress?.Report(new SolverProgress($"Time step {n}/{steps}", 0.05 + 0.95 * n / steps));
            }
            log.Add($"TR-BDF2: {steps} steps of Δt = {dt:g4} s to t = {end:g4} s ({iterations} CG iterations total); " +
                    $"{frames.Count} frames stored.");
        }
        else
        {
            // Landing points: the stored frames' times and the profiles' edges.
            var outputs = Enumerable.Range(1, steps).Where(n => n % stride == 0 || n == steps).Select(n => n * dt).ToList();
            var targets = outputs.Concat(breakpoints.Where(x => x > 0 && x < end)).Distinct().OrderBy(x => x).ToList();
            var stored = new HashSet<double>(outputs);
            double tolerance = settings.Tolerance;
            double t = 0, h = Math.Min(dt, end) / 8, minimum = end * 1e-10;
            foreach (double target in targets)
            {
                double lastStep = 0;
                while (target - t > 1e-12 * end)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double step = Math.Min(h, target - t);
                    bool clipped = step < h;
                    var whole = Step(t, step, theta);
                    var half = Step(t, 0.5 * step, theta);
                    var pair = Step(t + 0.5 * step, 0.5 * step, half);
                    double error = 0;
                    for (int i = 0; i < free; i++) error = Math.Max(error, Math.Abs(pair[i] - whole[i]));
                    error /= 3;
                    double factor = error > 0 ? Math.Clamp(0.9 * Math.Cbrt(tolerance / error), 0.2, 4) : 4;
                    if (error <= tolerance)
                    {
                        theta = pair;
                        t = target - t - step <= 1e-12 * end ? target : t + step;
                        accepted++;
                        lastStep = step;
                        shortest = Math.Min(shortest, step);
                        longest = Math.Max(longest, step);
                        worstError = Math.Max(worstError, error);
                        h = clipped ? Math.Max(h, step * factor) : step * factor;
                    }
                    else
                    {
                        if (step <= minimum)
                            throw new InvalidOperationException(
                                $"The adaptive step fell to {step:g3} s at t = {t:g4} s with the error still {error:g3} K " +
                                $"over the tolerance {tolerance:g3} K. Loosen the tolerance or use a fixed step.");
                        rejected++;
                        h = step * factor;
                    }
                }
                if (stored.Contains(target)) Store(target, lastStep);
                progress?.Report(new SolverProgress($"t = {t:g4} s", 0.05 + 0.95 * t / end));
            }
            log.Add($"Adaptive TR-BDF2: {accepted} steps accepted, {rejected} rejected, from {shortest:g3} s to " +
                    $"{longest:g3} s, to t = {end:g4} s; tolerance {tolerance:g3} K per step (largest estimate " +
                    $"{worstError:g3} K); {iterations} CG iterations; {frames.Count} frames stored.");
        }

        var final = stiffness.Expand(theta);
        var meshFinal = networks is null ? final : networks.MeshPart(final);
        double min = meshFinal.Min(), max = meshFinal.Max();
        log.Add($"Final temperature range: {min:g4} … {max:g4} K.");
        if (frames[^1].Networks is { } finalStates)
            foreach (var state in finalStates)
                log.Add($"Thermal network '{state.Network}' at the end: " + string.Join(", ",
                    state.Nodes.Select((name, i) => $"{name} {state.Kelvin[i]:F2} K")) + ".");
        progress?.Report(new SolverProgress("Done", 1.0));
        return new SolveOutput
        {
            Fields = frames[^1].Fields,
            Log = log,
            Frames = frames,
            FrameAxis = "Time",
            Networks = frames[^1].Networks,
            Summary = new Dictionary<string, double>
            {
                ["Final min temperature (K)"] = min,
                ["Final max temperature (K)"] = max,
                ["Time steps"] = accepted,
                ["End time (s)"] = end
            }
        };
    }

    /// <summary>The loads plus the consistent nodal loads q·V/4 of an element source.</summary>
    private static double[] WithSource(double[] loads, FeMesh mesh, IReadOnlyList<double> source)
    {
        if (source.Count != mesh.ElementCount)
            throw new InvalidOperationException(
                $"The heat-source schedule returned {source.Count} values for {mesh.ElementCount} elements.");
        var result = (double[])loads.Clone();
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            if (source[e] == 0) continue;
            double share = source[e] * mesh.ElementVolume(e) / 4;
            var el = mesh.Elements[e];
            result[el.N0] += share; result[el.N1] += share; result[el.N2] += share; result[el.N3] += share;
        }
        return result;
    }

    /// <summary>
    /// The step below which backward Euler with the CONSISTENT capacity matrix stops being
    /// monotone: the largest h²/(6α) over the elements, with h the edge of the regular
    /// tetrahedron of the element's volume. (In one dimension the bound is exact: the
    /// off-diagonal of M/Δt + K is ρc·h/(6Δt) − k/h.) Also names the element that sets it.
    /// </summary>
    internal static (double Step, string Element) MonotoneTimeStep(SolveInput input)
    {
        var mesh = input.Mesh;
        double worst = 0;
        string name = "mesh";
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var m = input.MaterialOf(e);
            double k = input.ElementThermalConductivity is { } tensors
                ? Math.Min(tensors[e].Xx, Math.Min(tensors[e].Yy, tensors[e].Zz))
                : m.ThermalConductivity!.Value;
            double alpha = k / (m.Density * m.SpecificHeat!.Value);
            double h = Math.Cbrt(6 * Math.Sqrt(2) * Math.Abs(mesh.ElementVolume(e)));
            double bound = h * h / (6 * alpha);
            if (bound <= worst) continue;
            worst = bound;
            name = $"{h * 1e3:g3} mm elements of '{m.Name}'";
        }
        return (worst, name);
    }

    /// <summary>Step count and frame stride for the given settings (shared by Validate
    /// and Solve so the frame-cap check and the actual run can never disagree).</summary>
    private static (int Steps, int Stride) PlanSteps(TransientThermalSettings settings)
    {
        // The tiny tolerance keeps "Duration divisible by TimeStep" runs at exactly
        // Duration instead of one step past it (floating-point division).
        int steps = Math.Max(1, (int)Math.Ceiling(settings.Duration / settings.TimeStep - 1e-9));
        int stride = settings.OutputStride > 0
            ? settings.OutputStride
            : Math.Max(1, (int)Math.Ceiling(steps / 60.0));
        return (steps, stride);
    }

    private static ResultFrame MakeFrame(double time, double[] temperature, FeMesh mesh,
        ScalarDiffusionAssembler assembler, SolveInput input, SurfaceFilmModel? film,
        ThermalNetworkTerms? networks, Func<ThermalNetworkNode, double> powerScale)
    {
        // Flux recovery only for STORED frames — O(stored), not O(steps).
        var flux = new Vector3D[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
            flux[e] = ScalarDiffusionAssembler.HeatFlux(input, assembler, e, temperature);   // q = −K∇T

        var groups = ScalarSolverHelpers.MaterialGroups(input, out var groupNames);
        var (fluxNodal, fluxSides) = ScalarSolverHelpers.NodalAverageWithSides(mesh, flux, groups, groupNames);
        var fields = new List<IResultField>
        {
            new NodalScalarField("Temperature", "K", networks is null ? (double[])temperature.Clone() : networks.MeshPart(temperature)),
            new NodalVectorField("Heat flux", "W/m²", fluxNodal, fluxSides)
        };
        // Present in EVERY frame of an environment run, so the frame field sets stay
        // identical and the UI keeps its selected field while scrubbing.
        if (film is not null)
            fields.Add(new NodalScalarField("Film coefficient", "W/(m²·K)",
                EnvironmentThermalTerms.NodalFilmField(mesh, film)));
        return new ResultFrame($"t = {time:g4} s", time, fields)
        {
            Unit = "s",
            Networks = networks?.States(temperature, powerScale)
        };
    }

    private static void AddScaled(SparseMatrixBuilder builder, CsrMatrix matrix, double scale)
    {
        for (int row = 0; row < matrix.RowCount; row++)
            for (int k = matrix.RowPointers[row]; k < matrix.RowPointers[row + 1]; k++)
                builder.Add(row, matrix.ColumnIndices[k], scale * matrix.Values[k]);
    }
}
