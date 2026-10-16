using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers.Environment;

namespace OpenSim.Solvers;

/// <summary>
/// Steady-state heat conduction solver over TET4 elements: ∇·(k∇T) + q = 0 with
/// prescribed temperatures (Dirichlet), heat flows (Neumann), convection (Robin),
/// and an optional volumetric source (e.g. Joule heating). Produces temperature
/// and heat flux result fields.
/// <para>
/// With an <see cref="SolveInput.Environment"/> the surface exchange becomes a function of
/// the surface temperature (correlations for convection, εσ(T⁴−T_a⁴) for radiation), so the
/// problem is nonlinear and is solved by fixed-point iteration: assemble with the film of
/// the previous iterate, solve, repeat. The convection coefficient is lagged; radiation
/// enters as its Newton tangent about the previous iterate, per boundary triangle. The
/// converged iterate therefore satisfies εσ(T⁴−T_a⁴) with T the mean of each triangle —
/// the radiation law at the mesh's resolution, not at one temperature per face — and the
/// convection correlations at each face's mean temperature, which is what they are
/// written for.
/// </para>
/// </summary>
public sealed class HeatConductionSolver : ISolver
{
    public string Name => "Steady-state heat conduction (thermal)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (input.Mesh.IsHex)
            throw new InvalidOperationException(
                "The thermal solver supports tetrahedral meshes (TET4 or TET10) only; " +
                "re-generate the mesh with tetrahedral elements.");
        if (input.Mesh.IsQuadratic) RequireQuadraticSupported(input);

        input.Material.ValidateThermal();
        if (input.RegionMaterials is not null)
            foreach (var material in input.RegionMaterials.Values)
                material.ValidateThermal();

        // The film/environment conflict is more fundamental than any environment detail,
        // so it is checked FIRST — a missing emissivity is irrelevant when the whole
        // environment should not be there.
        ValidatePrescribedFilm(input);
        ScalarSolverHelpers.ValidateConductivityTensor(input);
        EnvironmentBoundaryModel.ValidateMaterials(input);
        SurfaceRadiationExchange.Validate(input);
        var networks = ThermalNetworkTerms.Validate(input);
        if (!input.BoundaryConditions.Any(bc => bc is FixedTemperature or Convection)
            && !EnvironmentCouples(input) && !PrescribedFilmCouples(input) && !(networks?.AnchorsMesh ?? false)
            && input.SurfaceRadiation is not { Count: > 0 })
            throw new InvalidOperationException(
                "At least one fixed temperature or convection condition is required; " +
                "with only heat inflows the temperature level is undetermined.");

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

        if (input.ThermalContacts is not null)
            foreach (var contact in input.ThermalContacts)
                contact.Validate(input.Mesh.NodeCount);

        RequireAnchoredComponents(input, networks);
        FluxRecoveryOutput.Validate(input);
    }

    /// <summary>
    /// What a quadratic (TET10) mesh carries: fixed temperatures, heat flows, convection
    /// (one coefficient per surface triangle — a convection profile's triangle mean),
    /// volumetric sources and conductivity tensors. The surface models below are stamped on
    /// the corner nodes of the skin and are refused by name, not applied to three of six nodes.
    /// </summary>
    private static void RequireQuadraticSupported(SolveInput input)
    {
        var refused = new List<string>();
        if (input.Environment is not null) refused.Add("an environment");
        if (input.PrescribedFilm is not null) refused.Add("a prescribed film");
        if (input.SurfaceRadiation is { Count: > 0 }) refused.Add("surface-to-surface radiation");
        if (input.ThermalNetworks is { Count: > 0 }) refused.Add("thermal networks");
        if (input.ThermalContacts is { Count: > 0 }) refused.Add("thermal contacts");
        if (refused.Count > 0)
            throw new InvalidOperationException(
                $"On a quadratic (TET10) mesh the thermal solver takes fixed temperatures, heat flows, " +
                $"convection and volumetric sources; {string.Join(", ", refused)} " +
                "work on linear meshes only. Re-generate the mesh with linear elements.");
    }

    /// <summary>
    /// Every connected piece of the mesh — following conduction AND thermal contacts — needs
    /// a temperature anchor. Without one, that piece's block is singular: only the assembly's
    /// heat balance is determined, not its level. An assembly makes this easy to hit (a body
    /// that touches nothing and carries no condition), so it is a named failure rather than a
    /// CG that fails to converge. A thermal network anchors the faces it is attached to when
    /// it reaches a fixed temperature itself.
    /// </summary>
    private static void RequireAnchoredComponents(SolveInput input, ThermalNetworkTerms? networks)
    {
        var anchored = new HashSet<int>();
        foreach (var bc in input.BoundaryConditions)
            if (bc is FixedTemperature or Convection)
                anchored.UnionWith(input.Mesh.GetScopeNodes(bc));
        if (networks is not null) anchored.UnionWith(networks.AnchoredMeshNodes());

        // An environment that exchanges heat anchors every exterior face it is allowed to
        // act on — which is every face the user did not claim for themselves (a network
        // driving the environment toward its own node reaches a fixed temperature, checked).
        if (EnvironmentCouples(input))
        {
            var claimed = new HashSet<int>();
            foreach (var bc in input.BoundaryConditions)
                if (bc is FixedTemperature or HeatFlux or Convection)
                    claimed.UnionWith(bc.FaceIds);
            claimed.UnionWith(ThermalNetwork.AttachedFaceIds(input.ThermalNetworks));
            foreach (var triangle in input.Mesh.BoundaryTriangles)
            {
                if (claimed.Contains(triangle.FaceId)) continue;
                anchored.Add(triangle.A);
                anchored.Add(triangle.B);
                anchored.Add(triangle.C);
            }
        }

        // Radiation to the surroundings anchors the faces that see them, and radiation between
        // faces couples bodies the way a contact does. Which faces see the surroundings is
        // only known once the view factors are, so every radiating face counts here: a closed
        // enclosure whose walls reach nothing fixed is left to the solve, which will not settle.
        foreach (int t in SurfaceRadiationExchange.TrianglesOf(input))
        {
            var triangle = input.Mesh.BoundaryTriangles[t];
            anchored.Add(triangle.A);
            anchored.Add(triangle.B);
            anchored.Add(triangle.C);
        }

        // A prescribed film anchors exactly the triangles it wets.
        if (input.PrescribedFilm is { } prescribedFilm)
        {
            for (int t = 0; t < input.Mesh.BoundaryTriangles.Count; t++)
            {
                if (!prescribedFilm.IsWetted(t) || prescribedFilm.TriangleFilmCoefficient[t] <= 0)
                    continue;
                var triangle = input.Mesh.BoundaryTriangles[t];
                anchored.Add(triangle.A);
                anchored.Add(triangle.B);
                anchored.Add(triangle.C);
            }
        }

        var floating = ScalarSolverHelpers.FindUnanchoredRegions(
            input.Mesh, input.ThermalContacts, anchored);
        if (floating.Count == 0) return;

        string regions = string.Join(", ", floating);
        throw new InvalidOperationException(
            $"Mesh region(s) {regions} reach no fixed temperature or convection condition, " +
            "directly or through a thermal contact, so their temperature level is undetermined. " +
            "Give those bodies a condition, or check that they actually touch the rest of the " +
            "assembly (a gap wider than the contact tolerance leaves them floating).");
    }

    /// <summary>A prescribed film with any positive coefficient anchors the temperature level.</summary>
    private static bool PrescribedFilmCouples(SolveInput input) =>
        input.PrescribedFilm is { } film
        && film.TriangleFilmCoefficient.Any(h => !double.IsNaN(h) && h > 0);

    /// <summary>Shared by the steady and transient solvers: a prescribed film must fit the
    /// mesh, and it replaces — never stacks with — an environment.</summary>
    internal static void ValidatePrescribedFilm(SolveInput input)
    {
        if (input.PrescribedFilmSchedule is not null && input.Environment is not null)
            throw new InvalidOperationException(
                "A prescribed film schedule and an environment cannot both be set: each is a " +
                "complete surface-exchange model, and applying both would double-count the film.");
        if (input.PrescribedFilm is not { } film) return;
        if (input.Environment is not null)
            throw new InvalidOperationException(
                "A prescribed film and an environment cannot both be set: each is a complete " +
                "surface-exchange model, and applying both would double-count the film.");
        if (film.TriangleFilmCoefficient.Count != input.Mesh.BoundaryTriangles.Count)
            throw new InvalidOperationException(
                $"The prescribed film has {film.TriangleFilmCoefficient.Count} triangle coefficients " +
                $"but the mesh has {input.Mesh.BoundaryTriangles.Count} boundary triangles.");
        if (film.TriangleReferenceTemperature is { } refs
            && refs.Count != film.TriangleFilmCoefficient.Count)
            throw new InvalidOperationException(
                "The prescribed film's per-triangle reference temperatures must parallel its coefficients.");
        for (int t = 0; t < film.TriangleFilmCoefficient.Count; t++)
        {
            double h = film.TriangleFilmCoefficient[t];
            if (!double.IsNaN(h) && h < 0)
                throw new InvalidOperationException(
                    $"The prescribed film has a negative coefficient ({h:g3} W/(m²·K)) at boundary " +
                    $"triangle {t}; a negative film would make the system indefinite.");
        }
    }

    /// <summary>
    /// Whether the environment actually exchanges heat with the surfaces. A vacuum with
    /// radiation switched off (or with every emissivity at zero) is genuinely adiabatic —
    /// it must NOT count as a temperature anchor, or the steady solve would be reported as
    /// well-posed when its level is still undetermined.
    /// </summary>
    private static bool EnvironmentCouples(SolveInput input)
    {
        if (input.Environment is not { } environment) return false;
        if (environment.Medium != MediumKind.Vacuum) return true;
        if (!environment.IncludeRadiation) return false;
        if (input.Material.Emissivity > 0) return true;
        return input.RegionMaterials?.Values.Any(m => m.Emissivity > 0) ?? false;
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;

        progress?.Report(new SolverProgress("Assembling conduction matrix", 0.05));
        var assembler = ScalarDiffusionAssembler.Thermal(input);

        // Robin terms regularize the matrix, so they are assembled with it (keeps SPD).
        var robin = new List<ScalarDiffusionAssembler.RobinTerm>();
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
            foreach (var (t, h) in ConvectionTerms.Triangles(mesh, convection))
                robin.Add(new ScalarDiffusionAssembler.RobinTerm(t, h));
        var conduction = assembler.AssembleStiffness(robin, input.ThermalContacts, cancellationToken);
        // Lumped networks (parts, an enclosure's air) are unknowns after the mesh's nodes.
        var networks = ThermalNetworkTerms.Build(input, log);
        if (networks is not null) conduction = networks.Augment(conduction);
        log.Add($"Assembled {conduction.RowCount} DOF system, {conduction.NonZeroCount} non-zeros" +
                (robin.Count > 0 ? $" including {robin.Count} convective surface triangles." : "."));
        LogContacts(input, log);

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.25));
        var loads = ScalarSolverHelpers.AssembleThermalLoads(input, log);
        if (networks is not null) loads = networks.AugmentLoads(loads, _ => 1.0);

        var prescribed = new Dictionary<int, double>();
        var edgeMid = mesh.IsQuadratic ? QuadraticMeshBuilder.BuildEdgeMidMap(mesh) : null;
        foreach (var temperature in input.BoundaryConditions.OfType<FixedTemperature>())
        {
            var nodes = ScalarSolverHelpers.ScopeNodes(mesh, temperature, edgeMid);
            foreach (int node in nodes)
                prescribed[node] = temperature.Kelvin;
            log.Add($"Temperature '{temperature.Name}': {temperature.Kelvin:g4} K on {nodes.Count} nodes.");
        }
        networks?.AddPrescribed(prescribed);

        progress?.Report(new SolverProgress("Solving linear system", 0.35));
        var environment = EnvironmentBoundaryModel.Build(input, log);
        var radiation = SurfaceRadiationExchange.Build(input, log, cancellationToken);
        double[] solution;
        SurfaceFilmModel? film = null;
        if (input.PrescribedFilm is { } prescribedFilm)
        {
            // The film is one FIXED iterate of an outer conjugate loop, so this solve is
            // linear: fold its Robin terms in and solve once. The nonlinearity lives in
            // whoever supplies the film.
            film = prescribedFilm;
            var system = EnvironmentThermalTerms.WithFilm(conduction, mesh, film);
            var rhs = EnvironmentThermalTerms.WithFilmLoads(loads, mesh, film);
            var result = ConstrainedSystemSolver.Solve(system, rhs, prescribed,
                cancellationToken: cancellationToken, allowUnconstrained: true);
            log.Add($"Prescribed film ({film.Origin}): {film.WettedCount} wetted triangles; " +
                    $"CG converged in {result.Iterations.Iterations} iterations " +
                    $"(residual {result.Iterations.ResidualNorm:g3}).");
            solution = result.Displacements;
        }
        else if (environment is null && radiation is null)
        {
            var result = ConstrainedSystemSolver.Solve(conduction, loads, prescribed,
                cancellationToken: cancellationToken, allowUnconstrained: robin.Count > 0 || networks is not null);
            log.Add($"Conjugate gradient converged in {result.Iterations.Iterations} iterations " +
                    $"(residual {result.Iterations.ResidualNorm:g3}).");
            solution = result.Displacements;
        }
        else
        {
            (solution, film) = SolveWithEnvironment(input, environment, radiation, conduction, loads,
                prescribed, networks, log, progress, cancellationToken);
        }
        var temperatureField = networks is null ? solution : networks.MeshPart(solution);
        var states = networks?.States(solution, _ => 1.0);
        if (states is not null)
            foreach (var state in states)
                log.Add($"Thermal network '{state.Network}': " + string.Join(", ",
                    state.Nodes.Select((n, i) => $"{n} {state.Kelvin[i]:F2} K")) + ".");

        progress?.Report(new SolverProgress("Recovering heat flux", 0.85));
        var flux_ = new Vector3D[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
            flux_[e] = ScalarDiffusionAssembler.HeatFlux(input, assembler, e, temperatureField);   // q = −K∇T

        var groups = ScalarSolverHelpers.MaterialGroups(input, out var groupNames);
        if (groups is not null)
            log.Add(ScalarSolverHelpers.InterfaceNote("heat flux"));

        progress?.Report(new SolverProgress("Done", 1.0));
        var (fluxNodal, fluxSides) = ScalarSolverHelpers.NodalAverageWithSides(mesh, flux_, groups, groupNames);
        var fields = new List<IResultField>
        {
            new NodalScalarField("Temperature", "K", temperatureField),
            new NodalVectorField("Heat flux", "W/m²", fluxNodal, fluxSides)
        };
        if (film is not null)
            fields.Add(new NodalScalarField("Film coefficient", "W/(m²·K)",
                EnvironmentThermalTerms.NodalFilmField(mesh, film)));

        Dictionary<string, double>? summary = null;
        if (input.FluxRecovery is not null)
        {
            summary = new Dictionary<string, double>();
            FluxRecoveryOutput.Add(input, assembler, temperatureField, assembler.Tensor, input.ElementHeatSource,
                FluxRecoveryOutput.ThermalOutflow(input, assembler, temperatureField), ("Heat flux", "W/m²"),
                ("heat flow", "W"), fields, summary, log, cancellationToken);
        }

        return new SolveOutput { Fields = fields, Log = log, Networks = states, Summary = summary };
    }

    /// <summary>
    /// The fixed-point loop: evaluate the film at the current temperature, solve the
    /// linear system it defines, repeat. Every iterate's matrix is the conduction matrix
    /// plus a positive surface mass term, so it stays SPD and the shared Jacobi-CG applies
    /// throughout.
    /// <para>
    /// Radiation is taken as its Newton tangent. With the factored coefficient lagged
    /// instead, the map's slope at a radiation-dominated fixed point is
    /// −(T−T_a)·h_r′/h_r, which passes −1 at T_s = 1.84·T_a (552 K in a 300 K room):
    /// above that the iteration oscillated with growing amplitude and a well-posed
    /// problem was reported as not settling. Newton on the convex T⁴ comes down onto the
    /// solution from above and stays there.
    /// </para>
    /// </summary>
    private static (double[] Temperature, SurfaceFilmModel? Film) SolveWithEnvironment(
        SolveInput input, EnvironmentBoundaryModel? environment, SurfaceRadiationExchange? radiation, CsrMatrix conduction,
        double[] loads, IReadOnlyDictionary<int, double> prescribed, ThermalNetworkTerms? networks,
        List<string> log, IProgress<SolverProgress>? progress, CancellationToken cancellationToken)
    {
        var mesh = input.Mesh;
        // Start from the ambient: the film coefficients are least extreme there, so the
        // first iterate never over-drives the surface exchange.
        var temperature = new double[conduction.RowCount];
        Array.Fill(temperature, input.Environment?.AmbientTemperature ?? input.SurfaceRadiation![0].AmbientTemperature);
        foreach (var (node, value) in prescribed)
            temperature[node] = value;

        SurfaceFilmModel? film;
        double change;
        int iteration = 0;
        long totalIterations = 0;
        var radiationValues = radiation is null ? null : new double[radiation.Pattern.Count];

        // Reduced ONCE. Only the surface film moves between iterates, and it writes to the
        // same entries every time, so the whole-matrix rebuild and re-reduction this loop used
        // to do per iterate collapse to rewriting those entries. With a network's node as the
        // environment's ambient (an enclosure's air) the film couples to that column as well,
        // and the slots hold those positions too.
        var updatable = FilmUpdatableSystem.Prepare(conduction, mesh, prescribed,
            environment is not null ? networks?.AirDof : null, radiation?.Pattern);
        var cg = new ConjugateGradientSolver { Tolerance = 1e-10 };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iteration++;
            double? air = networks?.AirDof is not null ? networks.AirTemperature(temperature) : null;
            film = environment?.Evaluate(temperature, tangent: true, ambientTemperature: air);
            var rhs = film is null ? (double[])loads.Clone()
                : networks is null
                    ? EnvironmentThermalTerms.WithFilmLoads(loads, mesh, film)
                    : networks.WithFilmLoads(loads, film, air ?? 0);
            radiation?.Linearize(temperature, radiationValues!, rhs);
            updatable.Apply(film, radiationValues);
            var system = updatable.System;
            var free = system.Restrict(temperature);       // warm start from the last iterate
            var iterations = cg.Solve(system.Reduced, system.ReduceLoads(rhs), free,
                ConjugateGradientSolver.BuildJacobiPreconditioner(system.Reduced),
                cancellationToken);
            if (!iterations.Converged)
                throw new InvalidOperationException(
                    $"Environment iterate {iteration} did not converge after " +
                    $"{iterations.Iterations} CG iterations (residual {iterations.ResidualNorm:g3}).");
            totalIterations += iterations.Iterations;
            var next = system.Expand(free);

            change = EnvironmentThermalTerms.MaxChange(temperature, next);
            temperature = next;
            progress?.Report(new SolverProgress(
                $"Environment iterate {iteration} (Δ = {change:g3} K)",
                Math.Min(0.8, 0.35 + 0.4 * iteration / 10.0)));

            // At least two iterates: the first one only proves the SOLVER converged, not
            // that the temperature-dependent coefficients stopped moving.
            if (iteration >= 2 && change < EnvironmentThermalTerms.Threshold(temperature)) break;
            if (iteration >= EnvironmentThermalTerms.MaxSteadyIterations)
                throw new InvalidOperationException(
                    $"The environment's surface coefficients did not settle: after {iteration} " +
                    $"iterations the temperature is still moving by {change:g3} K. This usually " +
                    "means a very large temperature rise with almost no conduction path — check " +
                    "the heat sources, the emissivity, and that the bodies touch what they should.");
        }

        log.Add($"{(environment is null ? "Surface radiation" : "Environment")}: converged in {iteration} nonlinear iterations " +
                $"(last change {change:g3} K, {totalIterations} CG iterations total).");
        if (radiation is not null) log.AddRange(radiation.Describe(temperature));
        if (environment is null) return (temperature, null);
        // What is reported and displayed is the film as it IS at the converged state.
        film = environment.Evaluate(temperature,
            ambientTemperature: networks?.AirDof is not null ? networks.AirTemperature(temperature) : null);
        var (minFilm, maxFilm) = film.CoefficientRange();
        log.Add($"Film coefficient over the exposed surface: {minFilm:g4} … {maxFilm:g4} W/(m²·K).");
        foreach (string note in environment.DrainNotes())
            log.Add("  " + note);
        return (temperature, film);
    }

    /// <summary>One line per contact interface: the coupled area and conductance are modeling
    /// assumptions, and an assumption that is not printed is a black box.</summary>
    internal static void LogContacts(SolveInput input, List<string> log)
    {
        if (input.ThermalContacts is null) return;
        foreach (var c in input.ThermalContacts)
            log.Add($"Thermal contact bodies {c.BodyA}–{c.BodyB}: {c.CoupledArea:g4} m² at " +
                    $"h_c = {c.Conductance:g4} W/(m²·K) ({c.Stamps.Count} coupling points" +
                    (c.UnpairedPoints > 0 ? $", {c.UnpairedPoints} unpaired" : "") + ").");
    }
}
