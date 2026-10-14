using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Solvers.Environment;

namespace OpenSim.Solvers;

/// <summary>Where a thermal impedance curve is read: the node nearest a point, or the
/// area mean over some faces.</summary>
public sealed record ThermalProbe
{
    public required string Name { get; init; }
    public Vector3D? Position { get; init; }
    public IReadOnlyList<int>? FaceIds { get; init; }

    /// <summary>A node of one of the solve's thermal networks (a part's junction): the
    /// network's name and the node's.</summary>
    public string? Network { get; init; }
    public string? Node { get; init; }
}

public sealed record ThermalImpedanceSettings
{
    /// <summary>First time of the curve [s].</summary>
    public double StartTime { get; init; } = 1e-6;

    /// <summary>Last time of the curve [s].</summary>
    public double EndTime { get; init; } = 100;

    public int PointsPerDecade { get; init; } = 12;

    /// <summary>
    /// False (default): Z_th of the linear model about the steady operating point — the
    /// surface coefficients and radiation held at their steady values. True: the rise of
    /// the full transient at the model's power from its unpowered steady state, with the
    /// environment's correlations and every radiative exchange re-linearized in every stage
    /// of every step. The curves differ where the surface exchange changes with temperature
    /// (radiation, natural convection); a model without either is linear and both are the same.
    /// </summary>
    public bool Nonlinear { get; init; }
}

/// <summary>One heat source of a model with several: a heat-flow condition by its name,
/// or a thermal network's node.</summary>
public sealed record ThermalSource
{
    public required string Name { get; init; }

    /// <summary>The <see cref="HeatFlux"/> condition this source is.</summary>
    public string? HeatFlow { get; init; }

    public string? Network { get; init; }
    public string? Node { get; init; }
}

/// <summary>
/// Z_th between several sources: entry [at, from] is the rise at source <c>at</c> per watt
/// of source <c>from</c> alone — the diagonal the self impedances, the rest the mutual ones.
/// The rise at a source is read where its heat goes in (its faces' area mean, its node), so
/// the matrix is symmetric: a linear conduction model is reciprocal.
/// </summary>
public sealed record ThermalImpedanceMatrix
{
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>Curves[at][from].</summary>
    public required IReadOnlyList<IReadOnlyList<ThermalImpedanceCurve>> Curves { get; init; }

    /// <summary>Each source's power in the model [W].</summary>
    public required IReadOnlyList<double> Powers { get; init; }

    public required IReadOnlyList<string> Log { get; init; }

    public ThermalImpedanceCurve this[int at, int from] => Curves[at][from];

    /// <summary>The rise at source <paramref name="at"/> with every source following its
    /// own profile at its own peak power, by superposition of the matrix's step responses.</summary>
    public double RiseAt(int at, double time, IReadOnlyList<(PowerProfile Profile, double PeakWatts)> powers)
    {
        if (powers.Count != Sources.Count)
            throw new ArgumentException($"Give one profile and power per source ({Sources.Count}).");
        double rise = 0;
        for (int from = 0; from < Sources.Count; from++)
            rise += Curves[at][from].RiseAt(time, powers[from].Profile, powers[from].PeakWatts);
        return rise;
    }
}

/// <summary>Z_th(t) at one probe: temperature rise per watt after the power is switched on.</summary>
public sealed record ThermalImpedanceCurve(string Name, IReadOnlyList<double> TimesSeconds,
    IReadOnlyList<double> KelvinPerWatt)
{
    /// <summary>The steady value, when the model has one; NaN when nothing carries the
    /// heat away (the rise then grows without limit).</summary>
    public double SteadyKelvinPerWatt { get; init; } = double.NaN;

    /// <summary>Z_th at a time, between points by straight line in log time; 0 before the
    /// first point's start, the last value after the end.</summary>
    public double At(double time)
    {
        if (time <= 0) return 0;
        if (time <= TimesSeconds[0]) return KelvinPerWatt[0] * Math.Sqrt(time / TimesSeconds[0]);
        if (time >= TimesSeconds[^1]) return KelvinPerWatt[^1];
        int hi = 1;
        while (TimesSeconds[hi] < time) hi++;
        double w = Math.Log(time / TimesSeconds[hi - 1]) / Math.Log(TimesSeconds[hi] / TimesSeconds[hi - 1]);
        return KelvinPerWatt[hi - 1] + w * (KelvinPerWatt[hi] - KelvinPerWatt[hi - 1]);
    }

    /// <summary>
    /// Temperature rise at a time for power that varies as the profile says, by
    /// superposition of this step response: every change of power starts a new copy of the
    /// curve. Exact for a linear thermal model; a pulse train is taken change by change, a
    /// table as steps at its points' midpoints.
    /// </summary>
    public double RiseAt(double time, PowerProfile profile, double peakWatts)
    {
        double rise = 0, previous = 0;
        foreach (var (at, scale) in Changes(profile, time))
        {
            rise += (scale - previous) * peakWatts * At(time - at);
            previous = scale;
        }
        return rise;
    }

    private static IEnumerable<(double Time, double Scale)> Changes(PowerProfile profile, double until)
    {
        switch (profile.Kind)
        {
            case PowerProfileKind.PulseTrain:
                yield return (0, profile.At(0));
                double on = profile.DutyCycle * profile.Period;
                if (on <= 0 || on >= profile.Period) yield break;
                for (double start = profile.Delay; start < until; start += profile.Period)
                {
                    if (start > 0) yield return (start, profile.OnScale);
                    if (start + on < until) yield return (start + on, profile.OffScale);
                }
                break;
            case PowerProfileKind.Table:
                yield return (0, profile.At(0));
                for (int i = 1; i < profile.Times.Count; i++)
                {
                    double middle = 0.5 * (profile.Times[i - 1] + profile.Times[i]);
                    if (middle >= until) yield break;
                    yield return (middle, profile.Scales[i]);
                }
                break;
            default:
                yield return (0, 1.0);
                break;
        }
    }
}

public sealed record ThermalImpedanceResult
{
    public required IReadOnlyList<ThermalImpedanceCurve> Curves { get; init; }

    /// <summary>Total power of the model's sources [W], which the rises were divided by.</summary>
    public required double PowerWatts { get; init; }

    public required IReadOnlyList<string> Log { get; init; }
}

/// <summary>
/// Thermal impedance curves: the temperature rise at chosen points, per watt, against time
/// after the sources are switched on — over as many decades of time as asked, which a
/// fixed-step transient cannot cover.
/// <para>
/// The rise θ obeys M·θ′ + K·θ = f with θ(0) = 0, the fixed temperatures and ambients set
/// to zero, so no start temperature enters. Time runs on a geometric grid; each step is
/// TR-BDF2 (a trapezoidal stage to 2 − √2 of the step, then BDF2 to its end), second order
/// and free of the ringing a trapezoidal step shows on a sudden load, with one matrix for
/// both stages. The heat capacity is lumped at the nodes, which keeps a step response
/// between its bounds.
/// </para>
/// <para>
/// Z_th belongs to a linear model. With an <see cref="SolveInput.Environment"/> the
/// surface coefficients are taken once, at the steady operating point, and held.
/// </para>
/// </summary>
public static class ThermalImpedanceSolver
{
    private static readonly double StageFraction = 2 - Math.Sqrt(2);   // γ
    private static readonly double Implicit = 1 - 1 / Math.Sqrt(2);    // γ/2 = (1 − γ)/(2 − γ)

    public static ThermalImpedanceResult Solve(SolveInput input, IReadOnlyList<ThermalProbe>? probes = null,
        ThermalImpedanceSettings? settings = null, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= new ThermalImpedanceSettings();
        var mesh = input.Mesh;
        bool nonlinearModel = input.Environment is not null || input.SurfaceRadiation is { Count: > 0 };
        if (mesh.ElementCount == 0) throw new InvalidOperationException("The mesh has no elements.");
        if (mesh.IsQuadratic)
            throw new InvalidOperationException("Thermal impedance runs on linear tetrahedral (TET4) meshes.");
        if (!(settings.StartTime > 0) || !(settings.EndTime > settings.StartTime) || settings.PointsPerDecade < 2)
            throw new InvalidOperationException("The curve needs a positive start time, a later end time and at least 2 points per decade.");
        input.Material.ValidateThermalTransient();
        if (input.RegionMaterials is not null)
            foreach (var material in input.RegionMaterials.Values) material.ValidateThermalTransient();
        if (input.PrescribedFilm is not null || input.PrescribedFilmSchedule is not null)
            throw new InvalidOperationException("Thermal impedance does not take a prescribed film; use convection conditions.");
        foreach (var bc in input.BoundaryConditions)
        {
            if (bc is not (FixedTemperature or HeatFlux or Convection))
                throw new InvalidOperationException($"Boundary condition '{bc.Name}' does not apply to a thermal solve.");
            BoundaryScope.Validate(bc, mesh);
            if (bc is Convection c) ConvectionTerms.Validate(c);
        }
        ThermalNetworkTerms.Validate(input);
        SurfaceRadiationExchange.Validate(input);
        if (settings.Nonlinear && nonlinearModel)
            return SolveNonlinear(input, probes, settings, progress, cancellationToken);
        var log = new List<string>();
        if (settings.Nonlinear)
            log.Add("Thermal impedance: the model has no environment and no radiation exchange, so it is linear and the nonlinear curve is the linear one.");
        // Lumped networks (a part's junction and case, an enclosure's air) are unknowns
        // after the mesh's nodes; their node powers are sources like any other.
        var networks = ThermalNetworkTerms.Build(input, log);

        // Sources: the heat flows and the volumetric source, as they stand.
        var (sources, _) = ScalarSolverHelpers.AssembleThermalLoadParts(input, new List<string>());
        if (networks is not null) sources = networks.AugmentLoads(sources, _ => 1.0);
        double power = sources.Sum();
        if (!(Math.Abs(power) > 0))
            throw new InvalidOperationException("Thermal impedance needs a heat source: a heat flow condition, a volumetric source or a network node's power.");

        // Conduction with the convection conditions; an environment as the film it settles to.
        var assembler = ScalarDiffusionAssembler.Thermal(input);
        var robin = new List<ScalarDiffusionAssembler.RobinTerm>();
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
            foreach (var (t, h) in ConvectionTerms.Triangles(mesh, convection))
                robin.Add(new ScalarDiffusionAssembler.RobinTerm(t, h));
        var conduction = assembler.AssembleStiffness(robin, input.ThermalContacts, cancellationToken);
        if (networks is not null) conduction = networks.Augment(conduction);
        bool anchored = robin.Count > 0 || input.BoundaryConditions.Any(bc => bc is FixedTemperature) || (networks?.HasFixed ?? false);
        if (nonlinearModel)
        {
            progress?.Report(new SolverProgress("Steady state for the surface coefficients", 0.02));
            var steady = new HeatConductionSolver().Solve(input with { TransientThermal = null }, null, cancellationToken);
            var steadyTemperature = ((Core.Results.NodalScalarField)steady.Fields.First(f => f.Name == "Temperature")).Values;
            if (input.Environment is not null && EnvironmentBoundaryModel.Build(input, new List<string>()) is { } environment)
            {
                double? air = null;
                if (networks?.AirDof is not null)
                {
                    var owner = input.ThermalNetworks!.First(n => n.EnvironmentAmbientNode is not null);
                    air = steady.Networks!.First(s => s.Network == owner.Name).Of(owner.EnvironmentAmbientNode!);
                }
                var film = environment.Evaluate(steadyTemperature, ambientTemperature: air);
                conduction = networks is null
                    ? EnvironmentThermalTerms.WithFilm(conduction, mesh, film)
                    : networks.WithFilm(conduction, film);
                var (low, high) = film.CoefficientRange();
                log.Add($"Environment ({input.Environment.Describe()}): surface coefficients held at their steady values, " +
                        $"{low:g3} … {high:g3} W/(m²·K)" + (air is { } airKelvin ? $", the enclosure air at {airKelvin:F2} K" : "") +
                        ". The curve is the response about that state.");
            }
            if (SurfaceRadiationExchange.Build(input, log, cancellationToken) is { } exchange)
            {
                // The exchange as the linear network it is AT the steady state: each pair's
                // secant conductance σ(T_i² + T_j²)(T_i + T_j), so the steady rise is the
                // operating point's own.
                var values = new double[exchange.Pattern.Count];
                exchange.Linearize(steadyTemperature, values, new double[conduction.RowCount], secant: true);
                conduction = WithEntries(conduction, exchange.Pattern, values);
                log.Add("Surface radiation: held at its secant conductances at the steady state; the curve is the response about that state.");
            }
            anchored = true;
        }

        var prescribed = new Dictionary<int, double>();
        foreach (var fixedTemperature in input.BoundaryConditions.OfType<FixedTemperature>())
            foreach (int node in mesh.GetScopeNodes(fixedTemperature))
                prescribed[node] = 0;
        networks?.AddPrescribed(prescribed, zero: true);
        var reduced = ConstrainedSystemSolver.Reduce(conduction, prescribed, allowUnconstrained: true);
        var k = reduced.Reduced;
        int free = reduced.FreeCount;
        var load = reduced.ReduceLoads(sources);

        IReadOnlyList<double> capacity = assembler.AssembleLumpedMass(el =>
        {
            var m = input.MaterialOf(el);
            return m.Density * m.SpecificHeat!.Value;
        }, cancellationToken).GetDiagonal();
        if (networks is not null) capacity = networks.AugmentDiagonal(capacity);
        var mass = reduced.Restrict(capacity);

        // Probes as weights over the nodes.
        var probeList = probes is { Count: > 0 } ? probes : DefaultProbes(input, networks);
        var weights = probeList.Select(p => ProbeWeights(mesh, p, networks)).ToList();

        // Step matrix A = M + d·h·K on K's pattern, rewritten each step.
        var diagonalIndex = new int[free];
        for (int i = 0; i < free; i++)
        {
            diagonalIndex[i] = k.IndexOf(i, i);
            if (diagonalIndex[i] < 0) throw new InvalidOperationException("The conduction matrix has an empty diagonal entry.");
        }
        var step = CsrMatrix.FromArrays(free, free, k.RowPointers, k.ColumnIndices, (double[])k.Values.Clone());
        var cg = new ConjugateGradientSolver { Tolerance = 1e-11, MaxIterations = Math.Max(4 * free, 2000) };

        // Time grid: geometric, started two decades before the first point asked for so the
        // crude first steps have been forgotten by then.
        double ratio = Math.Pow(10, 1.0 / settings.PointsPerDecade);
        int lead = 2 * settings.PointsPerDecade;
        int points = (int)Math.Ceiling(Math.Log10(settings.EndTime / settings.StartTime) * settings.PointsPerDecade - 1e-9) + 1;
        var times = new double[points];
        var curves = weights.Select(_ => new double[points]).ToArray();

        var theta = new double[free];
        var stage = new double[free];
        var rhs = new double[free];
        var work = new double[free];
        var full = new double[mesh.NodeCount];
        double time = 0;
        long iterations = 0;
        for (int n = -lead; n < points; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double next = settings.StartTime * Math.Pow(ratio, n);
            double h = next - time, dh = Implicit * h;
            for (int e = 0; e < step.Values.Length; e++) step.Values[e] = dh * k.Values[e];
            for (int i = 0; i < free; i++) step.Values[diagonalIndex[i]] += mass[i];
            var inverseDiagonal = ConjugateGradientSolver.BuildJacobiPreconditioner(step);

            // Trapezoidal stage to t + γh: (M + d·h·K)·θγ = M·θ − d·h·K·θ + γ·h·f.
            k.Multiply(theta, work);
            for (int i = 0; i < free; i++)
                rhs[i] = mass[i] * theta[i] - dh * work[i] + StageFraction * h * load[i];
            Array.Copy(theta, stage, free);
            var first = cg.Solve(step, rhs, stage, inverseDiagonal, cancellationToken);
            // BDF2 stage to t + h: (M + d·h·K)·θ₊ = M·(θγ − (1 − γ)²·θ)/(γ(2 − γ)) + d·h·f.
            double a = 1 / (StageFraction * (2 - StageFraction));
            double b = (1 - StageFraction) * (1 - StageFraction) * a;
            for (int i = 0; i < free; i++)
                rhs[i] = mass[i] * (a * stage[i] - b * theta[i]) + dh * load[i];
            Array.Copy(stage, work, free);
            var second = cg.Solve(step, rhs, work, inverseDiagonal, cancellationToken);
            if (!first.Converged || !second.Converged)
                throw new InvalidOperationException(
                    $"The step to t = {next:g3} s did not converge (residual {Math.Max(first.ResidualNorm, second.ResidualNorm):g3}).");
            iterations += first.Iterations + second.Iterations;
            Array.Copy(work, theta, free);
            time = next;

            if (n >= 0)
            {
                var expanded = reduced.Expand(theta);
                times[n] = time;
                for (int p = 0; p < weights.Count; p++)
                {
                    double value = 0;
                    foreach (var (node, weight) in weights[p]) value += weight * expanded[node];
                    curves[p][n] = value / power;
                }
            }
            progress?.Report(new SolverProgress($"t = {time:g3} s", 0.05 + 0.9 * (n + lead + 1) / (points + lead)));
        }

        // The steady value, when something carries the heat away.
        var steadyValues = new double[weights.Count];
        Array.Fill(steadyValues, double.NaN);
        if (anchored)
        {
            var solution = new double[free];
            Array.Copy(theta, solution, free);
            var steadySolve = new ConjugateGradientSolver { Tolerance = 1e-11, MaxIterations = Math.Max(10 * free, 5000) }
                .Solve(k, load, solution, cancellationToken);
            if (steadySolve.Converged)
            {
                var expanded = reduced.Expand(solution);
                for (int p = 0; p < weights.Count; p++)
                {
                    double value = 0;
                    foreach (var (node, weight) in weights[p]) value += weight * expanded[node];
                    steadyValues[p] = value / power;
                }
            }
        }

        log.Add($"Thermal impedance: {points} points from {settings.StartTime:g3} s to {times[^1]:g3} s " +
                $"({settings.PointsPerDecade} per decade, {lead} lead-in steps), TR-BDF2, lumped capacity; " +
                $"{iterations} CG iterations; source power {power:g4} W.");
        for (int p = 0; p < weights.Count; p++)
        {
            double last = curves[p][^1];
            log.Add(double.IsNaN(steadyValues[p])
                ? $"'{probeList[p].Name}': {last:g4} K/W at the end; nothing carries the heat away, so there is no steady value."
                : $"'{probeList[p].Name}': {last:g4} K/W at the end, {steadyValues[p]:g4} K/W steady" +
                  (last < 0.98 * steadyValues[p] ? " — the curve has not settled; extend the end time." : "."));
        }

        return new ThermalImpedanceResult
        {
            Curves = probeList.Select((p, i) => new ThermalImpedanceCurve(p.Name, times, curves[i])
            {
                SteadyKelvinPerWatt = steadyValues[i]
            }).ToList(),
            PowerWatts = power,
            Log = log
        };
    }

    /// <summary>A matrix plus extra entries at the given places.</summary>
    private static CsrMatrix WithEntries(CsrMatrix matrix, IReadOnlyList<(int Row, int Column)> places, IReadOnlyList<double> values)
    {
        var builder = new SparseMatrixBuilder(matrix.RowCount, matrix.ColumnCount);
        for (int row = 0; row < matrix.RowCount; row++)
            for (int k = matrix.RowPointers[row]; k < matrix.RowPointers[row + 1]; k++)
                builder.Add(row, matrix.ColumnIndices[k], matrix.Values[k]);
        for (int k = 0; k < places.Count; k++)
            if (values[k] != 0) builder.Add(places[k].Row, places[k].Column, values[k]);
        return builder.Build();
    }

    /// <summary>
    /// The nonlinear curve: the full transient at the model's power from its unpowered
    /// steady state, on the same geometric time grid and TR-BDF2 steps as the linear one.
    /// Each stage is solved by fixed-point iteration, the environment's film (radiation's
    /// Newton tangent) and the radiation exchange linearized at the latest iterate — exact
    /// at convergence. Z(t) is the probe's rise over its unpowered value per watt.
    /// </summary>
    private static ThermalImpedanceResult SolveNonlinear(SolveInput input, IReadOnlyList<ThermalProbe>? probes,
        ThermalImpedanceSettings settings, IProgress<SolverProgress>? progress, CancellationToken cancellationToken)
    {
        var mesh = input.Mesh;
        var log = new List<string>();
        var networks = ThermalNetworkTerms.Build(input, log);
        var (sources, ambientLoads) = ScalarSolverHelpers.AssembleThermalLoadParts(input, new List<string>());
        var on = new double[mesh.NodeCount];
        for (int i = 0; i < on.Length; i++) on[i] = ambientLoads[i] + sources[i];
        double[] off = ambientLoads;
        if (networks is not null)
        {
            on = networks.AugmentLoads(on, _ => 1.0);
            off = networks.AugmentLoads(off, null);
        }
        double power = on.Sum() - off.Sum();
        if (!(Math.Abs(power) > 0))
            throw new InvalidOperationException("Thermal impedance needs a heat source: a heat flow condition, a volumetric source or a network node's power.");

        // The unpowered state the rise is counted from, and the powered one it ends in.
        progress?.Report(new SolverProgress("Unpowered and powered steady states", 0.02));
        var unpowered = input with
        {
            TransientThermal = null,
            BoundaryConditions = input.BoundaryConditions.Where(bc => bc is not HeatFlux).ToList(),
            ElementHeatSource = null,
            ThermalNetworks = input.ThermalNetworks?.Select(n => n with
            {
                Nodes = n.Nodes.Select(node => node with { PowerWatts = 0 }).ToList()
            }).ToList()
        };
        double[] Full(SolveOutput steady)
        {
            var meshValues = ((Core.Results.NodalScalarField)steady.Fields.First(f => f.Name == "Temperature")).Values;
            if (networks is null) return meshValues.ToArray();
            var full = networks.Extend(meshValues, 0);
            networks.ApplyStart(full, steady.Networks!);
            var fixedNodes = new Dictionary<int, double>();
            networks.AddPrescribed(fixedNodes);
            foreach (var (dof, kelvin) in fixedNodes) full[dof] = kelvin;
            return full;
        }
        var start = Full(new HeatConductionSolver().Solve(unpowered, null, cancellationToken));
        var final = Full(new HeatConductionSolver().Solve(input with { TransientThermal = null }, null, cancellationToken));

        var assembler = ScalarDiffusionAssembler.Thermal(input);
        var robin = new List<ScalarDiffusionAssembler.RobinTerm>();
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
            foreach (var (t, h) in ConvectionTerms.Triangles(mesh, convection))
                robin.Add(new ScalarDiffusionAssembler.RobinTerm(t, h));
        var conduction = assembler.AssembleStiffness(robin, input.ThermalContacts, cancellationToken);
        if (networks is not null) conduction = networks.Augment(conduction);
        int dofs = conduction.RowCount;
        IReadOnlyList<double> capacity = assembler.AssembleLumpedMass(el =>
        {
            var m = input.MaterialOf(el);
            return m.Density * m.SpecificHeat!.Value;
        }, cancellationToken).GetDiagonal();
        if (networks is not null) capacity = networks.AugmentDiagonal(capacity);

        var prescribed = new Dictionary<int, double>();
        foreach (var fixedTemperature in input.BoundaryConditions.OfType<FixedTemperature>())
            foreach (int node in mesh.GetScopeNodes(fixedTemperature))
                prescribed[node] = fixedTemperature.Kelvin;
        networks?.AddPrescribed(prescribed);
        var environment = EnvironmentBoundaryModel.Build(input, new List<string>());
        var radiation = SurfaceRadiationExchange.Build(input, log, cancellationToken);
        var radiationValues = radiation is null ? null : new double[radiation.Pattern.Count];

        // The heat into every unknown linearized at a state x, with loads f: A_x and r_x such
        // that the net heat in is r_x − A_x·T near x, exactly at x.
        (CsrMatrix Matrix, double[] Loads) Linearized(double[] x, double[] f)
        {
            double? air = networks?.AirDof is not null ? networks.AirTemperature(x) : null;
            var film = environment?.Evaluate(x, tangent: true, ambientTemperature: air);
            var a = film is null ? conduction : networks is null ? EnvironmentThermalTerms.WithFilm(conduction, mesh, film) : networks.WithFilm(conduction, film);
            var r = film is null ? (double[])f.Clone()
                : networks is null ? EnvironmentThermalTerms.WithFilmLoads(f, mesh, film) : networks.WithFilmLoads(f, film, air ?? 0);
            if (radiation is not null)
            {
                radiation.Linearize(x, radiationValues!, r);
                a = WithEntries(a, radiation.Pattern, radiationValues!);
            }
            return (a, r);
        }
        double[] NetHeat(double[] x, double[] f)
        {
            var (a, r) = Linearized(x, f);
            var ax = new double[dofs];
            a.Multiply(x, ax);
            for (int i = 0; i < dofs; i++) r[i] -= ax[i];
            return r;
        }
        var cg = new ConjugateGradientSolver { Tolerance = 1e-11, MaxIterations = Math.Max(4 * dofs, 2000) };
        long iterations = 0, passes = 0;
        // (C/dh + A_y)·x = rest + r_y(f), iterated on y until it stops moving.
        double[] Stage(double dh, double[] rest, double[] f, double[] guess, double time)
        {
            var y = (double[])guess.Clone();
            for (int pass = 1; ; pass++)
            {
                var (a, r) = Linearized(y, f);
                var builder = new SparseMatrixBuilder(dofs, dofs);
                for (int row = 0; row < dofs; row++)
                {
                    for (int k = a.RowPointers[row]; k < a.RowPointers[row + 1]; k++)
                        builder.Add(row, a.ColumnIndices[k], a.Values[k]);
                    builder.Add(row, row, capacity[row] / dh);
                }
                var system = ConstrainedSystemSolver.Reduce(builder.Build(), prescribed, allowUnconstrained: true);
                var rhs = new double[dofs];
                for (int i = 0; i < dofs; i++) rhs[i] = rest[i] + r[i];
                var free = system.Restrict(y);
                var result = cg.Solve(system.Reduced, system.ReduceLoads(rhs), free,
                    ConjugateGradientSolver.BuildJacobiPreconditioner(system.Reduced), cancellationToken);
                if (!result.Converged)
                    throw new InvalidOperationException($"The step to t = {time:g3} s did not converge (residual {result.ResidualNorm:g3}).");
                iterations += result.Iterations;
                passes++;
                var next = system.Expand(free);
                double change = EnvironmentThermalTerms.MaxChange(y, next);
                y = next;
                if (pass >= 2 && change < EnvironmentThermalTerms.Threshold(y)) return y;
                if (pass >= EnvironmentThermalTerms.MaxTransientIterations)
                    throw new InvalidOperationException(
                        $"The step to t = {time:g3} s: the surface exchange did not settle after {pass} iterations " +
                        $"(last change {change:g3} K). Use more points per decade.");
            }
        }

        var probeList = probes is { Count: > 0 } ? probes : DefaultProbes(input, networks);
        var weights = probeList.Select(p => ProbeWeights(mesh, p, networks)).ToList();
        double Read(int p, double[] state) => weights[p].Sum(w => w.Weight * state[w.Node]);
        var baseline = Enumerable.Range(0, weights.Count).Select(p => Read(p, start)).ToArray();

        double ratio = Math.Pow(10, 1.0 / settings.PointsPerDecade);
        int lead = 2 * settings.PointsPerDecade;
        int points = (int)Math.Ceiling(Math.Log10(settings.EndTime / settings.StartTime) * settings.PointsPerDecade - 1e-9) + 1;
        var times = new double[points];
        var curves = weights.Select(_ => new double[points]).ToArray();
        double a2 = 1 / (StageFraction * (2 - StageFraction));
        double b2 = (1 - StageFraction) * (1 - StageFraction) * a2;
        var theta = start;
        double time = 0;
        for (int n = -lead; n < points; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double next = settings.StartTime * Math.Pow(ratio, n);
            double h = next - time, dh = Implicit * h;
            // Trapezoid: (C/dh + A)·θγ = C·θ/dh + G(θ) + r(f); the power is on throughout.
            var heat = NetHeat(theta, on);
            var rest = new double[dofs];
            for (int i = 0; i < dofs; i++) rest[i] = capacity[i] * theta[i] / dh + heat[i];
            var stage = Stage(dh, rest, on, theta, time + StageFraction * h);
            // BDF2: (C/dh + A)·θ₊ = C·(a·θγ − b·θ)/dh + r(f).
            for (int i = 0; i < dofs; i++) rest[i] = capacity[i] * (a2 * stage[i] - b2 * theta[i]) / dh;
            theta = Stage(dh, rest, on, stage, next);
            time = next;
            if (n >= 0)
            {
                times[n] = time;
                for (int p = 0; p < weights.Count; p++) curves[p][n] = (Read(p, theta) - baseline[p]) / power;
            }
            progress?.Report(new SolverProgress($"t = {time:g3} s", 0.05 + 0.9 * (n + lead + 1) / (points + lead)));
        }

        log.Add($"Thermal impedance, nonlinear: the full transient at {power:g4} W from the unpowered steady state, " +
                $"{points} points from {settings.StartTime:g3} s to {times[^1]:g3} s ({settings.PointsPerDecade} per decade, {lead} lead-in steps), " +
                $"TR-BDF2, lumped capacity; the surface exchange re-linearized in every stage ({passes} solves, {iterations} CG iterations).");
        var steadyValues = Enumerable.Range(0, weights.Count).Select(p => (Read(p, final) - baseline[p]) / power).ToArray();
        for (int p = 0; p < weights.Count; p++)
        {
            double last = curves[p][^1];
            log.Add($"'{probeList[p].Name}': {last:g4} K/W at the end, {steadyValues[p]:g4} K/W steady" +
                    (last < 0.98 * steadyValues[p] ? " — the curve has not settled; extend the end time." : "."));
        }
        return new ThermalImpedanceResult
        {
            Curves = probeList.Select((p, i) => new ThermalImpedanceCurve(p.Name, times, curves[i])
            {
                SteadyKelvinPerWatt = steadyValues[i]
            }).ToList(),
            PowerWatts = power,
            Log = log
        };
    }

    /// <summary>
    /// The impedance matrix of several sources: one linear run per source with the others
    /// off, each read at every source. Each source is a heat-flow condition or a network
    /// node of the model; the model's other heat-flow conditions and node powers are off in
    /// every run, and a volumetric source is not allowed (it belongs to no source).
    /// </summary>
    public static ThermalImpedanceMatrix SolveMatrix(SolveInput input, IReadOnlyList<ThermalSource> sources,
        ThermalImpedanceSettings? settings = null, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= new ThermalImpedanceSettings();
        if (settings.Nonlinear)
            throw new InvalidOperationException(
                "A mutual-impedance matrix belongs to a linear model: superposition is what it is for, and the nonlinear curve does not superpose.");
        if (sources.Count == 0) throw new InvalidOperationException("Name at least one source.");
        if (input.ElementHeatSource is not null)
            throw new InvalidOperationException(
                "The model has a volumetric heat source, which is none of the named sources; leave it out of a mutual-impedance run.");
        var probes = new List<ThermalProbe>();
        foreach (var source in sources)
        {
            if (source.HeatFlow is { } flow)
            {
                var condition = input.BoundaryConditions.OfType<HeatFlux>().FirstOrDefault(c => c.Name == flow)
                    ?? throw new InvalidOperationException($"Source '{source.Name}': the model has no heat-flow condition '{flow}'.");
                probes.Add(new ThermalProbe { Name = source.Name, FaceIds = condition.FaceIds });
            }
            else if (source.Network is not null && source.Node is not null)
            {
                var network = input.ThermalNetworks?.FirstOrDefault(n => n.Name == source.Network)
                    ?? throw new InvalidOperationException($"Source '{source.Name}': the model has no thermal network '{source.Network}'.");
                if (network.IndexOf(source.Node) < 0)
                    throw new InvalidOperationException($"Source '{source.Name}': network '{source.Network}' has no node '{source.Node}'.");
                probes.Add(new ThermalProbe { Name = source.Name, Network = source.Network, Node = source.Node });
            }
            else throw new InvalidOperationException($"Source '{source.Name}' names neither a heat-flow condition nor a network node.");
        }

        var log = new List<string>();
        var columns = new List<ThermalImpedanceResult>();
        for (int j = 0; j < sources.Count; j++)
        {
            var source = sources[j];
            var alone = input with
            {
                BoundaryConditions = input.BoundaryConditions
                    .Where(bc => bc is not HeatFlux || (source.HeatFlow is not null && bc.Name == source.HeatFlow)).ToList(),
                ThermalNetworks = input.ThermalNetworks?.Select(n => n with
                {
                    Nodes = n.Nodes.Select(node => n.Name == source.Network && node.Name == source.Node ? node : node with { PowerWatts = 0 }).ToList()
                }).ToList()
            };
            int column = j;
            var result = Solve(alone, probes, settings, progress is null ? null : new Progress<SolverProgress>(p =>
                progress.Report(new SolverProgress($"Source '{source.Name}': {p.Stage}", (column + p.Fraction) / sources.Count))), cancellationToken);
            columns.Add(result);
            log.Add($"Source '{source.Name}' alone, {result.PowerWatts:g4} W:");
            log.AddRange(result.Log.Select(line => "  " + line));
        }
        var curves = Enumerable.Range(0, sources.Count)
            .Select(at => (IReadOnlyList<ThermalImpedanceCurve>)Enumerable.Range(0, sources.Count)
                .Select(from => columns[from].Curves[at] with { Name = $"{sources[at].Name} ← {sources[from].Name}" }).ToList())
            .ToList();
        return new ThermalImpedanceMatrix
        {
            Sources = sources.Select(s => s.Name).ToList(),
            Curves = curves,
            Powers = columns.Select(c => c.PowerWatts).ToList(),
            Log = log
        };
    }

    /// <summary>Without probes: the network nodes that dissipate (a part's junction), else
    /// the mean over the heated faces, else the hottest place a volumetric source has (its
    /// own elements' mean).</summary>
    private static IReadOnlyList<ThermalProbe> DefaultProbes(SolveInput input, ThermalNetworkTerms? networks)
    {
        if (networks is not null)
        {
            var powered = networks.PoweredNodes()
                .Select(p => new ThermalProbe { Name = $"{p.Network}: {p.Node}", Network = p.Network, Node = p.Node }).ToList();
            if (powered.Count > 0) return powered;
        }
        var faces = input.BoundaryConditions.OfType<HeatFlux>().SelectMany(f => f.FaceIds).Distinct().ToList();
        if (faces.Count > 0) return new[] { new ThermalProbe { Name = "heated faces (mean)", FaceIds = faces } };
        return new[] { new ThermalProbe { Name = "heated volume (mean)" } };
    }

    private static List<(int Node, double Weight)> ProbeWeights(FeMesh mesh, ThermalProbe probe, ThermalNetworkTerms? networks)
    {
        var weights = new Dictionary<int, double>();
        if (probe.Network is not null || probe.Node is not null)
        {
            if (probe.Network is null || probe.Node is null)
                throw new InvalidOperationException($"Probe '{probe.Name}': a network probe names both the network and the node.");
            if (networks is null)
                throw new InvalidOperationException($"Probe '{probe.Name}': the solve has no thermal networks.");
            return networks.ProbeWeights(probe.Network, probe.Node).ToList();
        }
        if (probe.FaceIds is { Count: > 0 } faces)
        {
            double total = 0;
            foreach (var t in mesh.GetFaceTriangles(faces))
            {
                double area = ScalarSolverHelpers.TriangleArea(mesh, t);
                total += area;
                foreach (int node in new[] { t.A, t.B, t.C })
                    weights[node] = weights.GetValueOrDefault(node) + area / 3;
            }
            if (!(total > 0)) throw new InvalidOperationException($"Probe '{probe.Name}': its faces have no area.");
            return weights.Select(w => (w.Key, w.Value / total)).ToList();
        }
        if (probe.Position is { } position)
        {
            int nearest = 0;
            double best = double.MaxValue;
            for (int i = 0; i < mesh.NodeCount; i++)
            {
                double distance = (mesh.Nodes[i] - position).LengthSquared;
                if (distance < best) (best, nearest) = (distance, i);
            }
            return new List<(int, double)> { (nearest, 1.0) };
        }
        // The whole volume's mean: each node's share of the volume.
        double volume = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double v = mesh.ElementVolume(e);
            volume += v;
            foreach (int node in mesh.GetElementNodes(e))
                weights[node] = weights.GetValueOrDefault(node) + v / 4;
        }
        return weights.Select(w => (w.Key, w.Value / volume)).ToList();
    }
}
