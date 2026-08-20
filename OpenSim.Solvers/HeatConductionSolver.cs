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
/// problem is nonlinear and is solved by lagged-coefficient fixed-point iteration: assemble
/// with the film coefficients of the previous iterate, solve, repeat. Because the radiation
/// coefficient is the FACTORED one rather than a tangent, the converged iterate satisfies
/// the exact nonlinear equations, not a linearization of them.
/// </para>
/// </summary>
public sealed class HeatConductionSolver : ISolver
{
    public string Name => "Steady-state heat conduction (thermal)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (input.Mesh.IsQuadratic)
            throw new InvalidOperationException(
                "The thermal solver supports linear (TET4) meshes only; " +
                "re-generate the mesh with linear elements.");

        input.Material.ValidateThermal();
        if (input.RegionMaterials is not null)
            foreach (var material in input.RegionMaterials.Values)
                material.ValidateThermal();

        // The film/environment conflict is more fundamental than any environment detail,
        // so it is checked FIRST — a missing emissivity is irrelevant when the whole
        // environment should not be there.
        ValidatePrescribedFilm(input);
        EnvironmentBoundaryModel.ValidateMaterials(input);
        if (!input.BoundaryConditions.Any(bc => bc is FixedTemperature or Convection)
            && !EnvironmentCouples(input) && !PrescribedFilmCouples(input))
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
            if (bc is Convection { Coefficient: <= 0 } c)
                throw new InvalidOperationException(
                    $"Convection '{c.Name}': the heat transfer coefficient must be positive.");
        }

        if (input.ThermalContacts is not null)
            foreach (var contact in input.ThermalContacts)
                contact.Validate(input.Mesh.NodeCount);

        RequireAnchoredComponents(input);
    }

    /// <summary>
    /// Every connected piece of the mesh — following conduction AND thermal contacts — needs
    /// a temperature anchor. Without one, that piece's block is singular: only the assembly's
    /// heat balance is determined, not its level. An assembly makes this easy to hit (a body
    /// that touches nothing and carries no condition), so it is a named failure rather than a
    /// CG that fails to converge.
    /// </summary>
    private static void RequireAnchoredComponents(SolveInput input)
    {
        var anchored = new HashSet<int>();
        foreach (var bc in input.BoundaryConditions)
            if (bc is FixedTemperature or Convection)
                anchored.UnionWith(input.Mesh.GetScopeNodes(bc));

        // An environment that exchanges heat anchors every exterior face it is allowed to
        // act on — which is every face the user did not claim for themselves.
        if (EnvironmentCouples(input))
        {
            var claimed = new HashSet<int>();
            foreach (var bc in input.BoundaryConditions)
                if (bc is FixedTemperature or HeatFlux or Convection)
                    claimed.UnionWith(bc.FaceIds);
            foreach (var triangle in input.Mesh.BoundaryTriangles)
            {
                if (claimed.Contains(triangle.FaceId)) continue;
                anchored.Add(triangle.A);
                anchored.Add(triangle.B);
                anchored.Add(triangle.C);
            }
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
        var assembler = new ScalarDiffusionAssembler(mesh,
            el => input.MaterialOf(el).ThermalConductivity!.Value);

        // Robin terms regularize the matrix, so they are assembled with it (keeps SPD).
        var robin = new List<ScalarDiffusionAssembler.RobinTerm>();
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
            foreach (var t in mesh.GetFaceTriangles(convection.FaceIds))
                robin.Add(new ScalarDiffusionAssembler.RobinTerm(t, convection.Coefficient));
        var conduction = assembler.AssembleStiffness(robin, input.ThermalContacts, cancellationToken);
        log.Add($"Assembled {conduction.RowCount} DOF system, {conduction.NonZeroCount} non-zeros" +
                (robin.Count > 0 ? $" including {robin.Count} convective surface triangles." : "."));
        LogContacts(input, log);

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.25));
        var loads = ScalarSolverHelpers.AssembleThermalLoads(input, log);

        var prescribed = new Dictionary<int, double>();
        foreach (var temperature in input.BoundaryConditions.OfType<FixedTemperature>())
        {
            var nodes = mesh.GetScopeNodes(temperature);
            foreach (int node in nodes)
                prescribed[node] = temperature.Kelvin;
            log.Add($"Temperature '{temperature.Name}': {temperature.Kelvin:g4} K on {nodes.Count} nodes.");
        }

        progress?.Report(new SolverProgress("Solving linear system", 0.35));
        var environment = EnvironmentBoundaryModel.Build(input, log);
        double[] temperatureField;
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
            temperatureField = result.Displacements;
        }
        else if (environment is null)
        {
            var result = ConstrainedSystemSolver.Solve(conduction, loads, prescribed,
                cancellationToken: cancellationToken, allowUnconstrained: robin.Count > 0);
            log.Add($"Conjugate gradient converged in {result.Iterations.Iterations} iterations " +
                    $"(residual {result.Iterations.ResidualNorm:g3}).");
            temperatureField = result.Displacements;
        }
        else
        {
            (temperatureField, film) = SolveWithEnvironment(input, environment, conduction, loads,
                prescribed, log, progress, cancellationToken);
        }

        progress?.Report(new SolverProgress("Recovering heat flux", 0.85));
        var flux_ = new Vector3D[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
            flux_[e] = assembler.ElementGradient(e, temperatureField)
                       * -input.MaterialOf(e).ThermalConductivity!.Value;   // q = −k∇T

        progress?.Report(new SolverProgress("Done", 1.0));
        var fields = new List<IResultField>
        {
            new NodalScalarField("Temperature", "K", temperatureField),
            new NodalVectorField("Heat flux", "W/m²", ScalarSolverHelpers.NodalAverage(mesh, flux_))
        };
        if (film is not null)
            fields.Add(new NodalScalarField("Film coefficient", "W/(m²·K)",
                EnvironmentThermalTerms.NodalFilmField(mesh, film)));

        return new SolveOutput { Fields = fields, Log = log };
    }

    /// <summary>
    /// The lagged-coefficient fixed-point loop: evaluate the film coefficients at the
    /// current temperature, solve the linear system they define, repeat. Every iterate's
    /// matrix is the conduction matrix plus a positive surface mass term, so it stays SPD
    /// and the shared Jacobi-CG applies throughout.
    /// </summary>
    private static (double[] Temperature, SurfaceFilmModel Film) SolveWithEnvironment(
        SolveInput input, EnvironmentBoundaryModel environment, CsrMatrix conduction,
        double[] loads, IReadOnlyDictionary<int, double> prescribed, List<string> log,
        IProgress<SolverProgress>? progress, CancellationToken cancellationToken)
    {
        var mesh = input.Mesh;
        // Start from the ambient: the film coefficients are least extreme there, so the
        // first iterate never over-drives the surface exchange.
        var temperature = new double[mesh.NodeCount];
        Array.Fill(temperature, input.Environment!.AmbientTemperature);
        foreach (var (node, value) in prescribed)
            temperature[node] = value;

        SurfaceFilmModel film;
        double change;
        int iteration = 0;
        long totalIterations = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iteration++;
            film = environment.Evaluate(temperature);
            var system = EnvironmentThermalTerms.WithFilm(conduction, mesh, film);
            var rhs = EnvironmentThermalTerms.WithFilmLoads(loads, mesh, film);
            var result = ConstrainedSystemSolver.Solve(system, rhs, prescribed,
                cancellationToken: cancellationToken, allowUnconstrained: true);
            totalIterations += result.Iterations.Iterations;

            change = EnvironmentThermalTerms.MaxChange(temperature, result.Displacements);
            temperature = result.Displacements;
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

        log.Add($"Environment: converged in {iteration} nonlinear iterations " +
                $"(last change {change:g3} K, {totalIterations} CG iterations total).");
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
