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
}

public sealed record ThermalImpedanceSettings
{
    /// <summary>First time of the curve [s].</summary>
    public double StartTime { get; init; } = 1e-6;

    /// <summary>Last time of the curve [s].</summary>
    public double EndTime { get; init; } = 100;

    public int PointsPerDecade { get; init; } = 12;
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
        }
        var log = new List<string>();

        // Sources: the heat flows and the volumetric source, as they stand.
        var (sources, _) = ScalarSolverHelpers.AssembleThermalLoadParts(input, new List<string>());
        double power = sources.Sum();
        if (!(Math.Abs(power) > 0))
            throw new InvalidOperationException("Thermal impedance needs a heat source: a heat flow condition or a volumetric source.");

        // Conduction with the convection conditions; an environment as the film it settles to.
        var assembler = ScalarDiffusionAssembler.Thermal(input);
        var robin = new List<ScalarDiffusionAssembler.RobinTerm>();
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
            foreach (var t in mesh.GetFaceTriangles(convection.FaceIds))
                robin.Add(new ScalarDiffusionAssembler.RobinTerm(t, convection.Coefficient));
        var conduction = assembler.AssembleStiffness(robin, input.ThermalContacts, cancellationToken);
        bool anchored = robin.Count > 0 || input.BoundaryConditions.Any(bc => bc is FixedTemperature);
        if (input.Environment is not null)
        {
            progress?.Report(new SolverProgress("Steady state for the surface coefficients", 0.02));
            var steady = new HeatConductionSolver().Solve(input with { TransientThermal = null }, null, cancellationToken);
            var steadyTemperature = ((Core.Results.NodalScalarField)steady.Fields.First(f => f.Name == "Temperature")).Values;
            var film = EnvironmentBoundaryModel.Build(input, new List<string>())!.Evaluate(steadyTemperature);
            conduction = EnvironmentThermalTerms.WithFilm(conduction, mesh, film);
            var (low, high) = film.CoefficientRange();
            log.Add($"Environment ({input.Environment.Describe()}): surface coefficients held at their steady values, " +
                    $"{low:g3} … {high:g3} W/(m²·K). The curve is the response about that state.");
            anchored = true;
        }

        var prescribed = new Dictionary<int, double>();
        foreach (var fixedTemperature in input.BoundaryConditions.OfType<FixedTemperature>())
            foreach (int node in mesh.GetScopeNodes(fixedTemperature))
                prescribed[node] = 0;
        var reduced = ConstrainedSystemSolver.Reduce(conduction, prescribed, allowUnconstrained: true);
        var k = reduced.Reduced;
        int free = reduced.FreeCount;
        var load = reduced.ReduceLoads(sources);

        var capacity = assembler.AssembleLumpedMass(el =>
        {
            var m = input.MaterialOf(el);
            return m.Density * m.SpecificHeat!.Value;
        }, cancellationToken).GetDiagonal();
        var mass = reduced.Restrict(capacity);

        // Probes as weights over the nodes.
        var probeList = probes is { Count: > 0 } ? probes : DefaultProbes(input);
        var weights = probeList.Select(p => ProbeWeights(mesh, p)).ToList();

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

    /// <summary>Without probes: the mean over the heated faces, or the hottest place a
    /// volumetric source has (its own elements' mean).</summary>
    private static IReadOnlyList<ThermalProbe> DefaultProbes(SolveInput input)
    {
        var faces = input.BoundaryConditions.OfType<HeatFlux>().SelectMany(f => f.FaceIds).Distinct().ToList();
        if (faces.Count > 0) return new[] { new ThermalProbe { Name = "heated faces (mean)", FaceIds = faces } };
        return new[] { new ThermalProbe { Name = "heated volume (mean)" } };
    }

    private static List<(int Node, double Weight)> ProbeWeights(FeMesh mesh, ThermalProbe probe)
    {
        var weights = new Dictionary<int, double>();
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
