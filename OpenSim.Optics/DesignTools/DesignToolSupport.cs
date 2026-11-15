using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.DesignTools;

// OP-61: the design conversion tools (docs/Optics-Conventions.md §59).

/// <summary>Progress of a design conversion tool after each trial.</summary>
/// <param name="Tool">"Find Best Asphere" or "Stock Lens Matching".</param>
/// <param name="Done">Trials finished.</param>
/// <param name="Total">Trials planned (the combination stage of Stock Lens Matching adds its own).</param>
/// <param name="BestMerit">The lowest merit found so far.</param>
/// <param name="Step">What the last trial was ("surface 2", "component 1: PCX-100 (ACME)").</param>
public sealed record DesignToolProgress(string Tool, int Done, int Total, double BestMerit, string Step);

/// <summary>A design that was re-optimized (or evaluated) and its merit.</summary>
/// <param name="Met">False when the exact (negative-weight) constraints are not met.</param>
internal readonly record struct DesignOutcome(OpticsSettings Design, double Merit, bool Met);

/// <summary>What the three tools share: the merit function, evaluation, re-optimization with
/// <see cref="LocalOptimizer"/> as it is, and writing a fitted shape into a saved surface.</summary>
internal static class DesignToolSupport
{
    /// <summary>The merit function: <paramref name="function"/>, or the record's saved one.
    /// Throws when it has no rows.</summary>
    public static MeritFunction Function(OpticsSettings optics, MeritFunction? function, MeritOperandRegistry registry, string tool, ICollection<string> log)
    {
        if (function is null)
        {
            var notes = new List<string>();
            function = MeritFunctionSettings.FromSettings(optics.MeritFunction, registry, notes);
            foreach (var n in notes) log.Add(n);
        }
        if (function.Rows.Count == 0)
            throw new ArgumentException($"{tool}: the merit function has no rows; build one in the Merit Function Editor first (the tool ranks by the current merit function).");
        return function;
    }

    public static MeritEvaluation Evaluate(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, MeritFunction function,
        MeritOperandRegistry registry, LocalOptimizationOptions local) =>
        MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(optics, catalogs), registry,
            (local.Merit ?? new MeritEvaluationOptions()) with { MaxDegreeOfParallelism = local.MaxDegreeOfParallelism });

    /// <summary>The merit of a design that is only evaluated; throws naming the failed rows.</summary>
    public static double EvaluateOrThrow(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, MeritFunction function,
        MeritOperandRegistry registry, LocalOptimizationOptions local)
    {
        var e = Evaluate(optics, catalogs, function, registry, local);
        if (e.HasFailures) throw new InvalidOperationException(string.Join(Environment.NewLine, e.Errors));
        return e.Value;
    }

    /// <summary>
    /// The design re-optimized with <see cref="LocalOptimizer"/> (or only evaluated when it has no
    /// variables) and its merit; null when cancelled. The run takes <paramref name="local"/>'s
    /// settings with this tool's cancellation token; infinite cycles become automatic (a trial
    /// must end). Throws when the merit function cannot be evaluated.
    /// </summary>
    public static DesignOutcome? Optimize(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, MeritFunction function,
        MeritOperandRegistry registry, LocalOptimizationOptions local, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        if (LensVariables.Find(optics).Count == 0)
            return new DesignOutcome(optics, EvaluateOrThrow(optics, catalogs, function, registry, local), true);
        var result = LocalOptimizer.Run(optics, catalogs, new LocalOptimizationOptions
        {
            Algorithm = local.Algorithm,
            Cycles = local.Cycles == LocalOptimizationCycles.Infinite ? LocalOptimizationCycles.Automatic : local.Cycles,
            CycleCount = local.CycleCount, Kinds = local.Kinds, MaxDegreeOfParallelism = local.MaxDegreeOfParallelism,
            MaxEvaluations = local.MaxEvaluations, ConstraintTolerance = local.ConstraintTolerance, MaxConstraintRounds = local.MaxConstraintRounds,
            Merit = local.Merit, CancellationToken = token,
        }, function, registry);
        if (result.Cancelled) return null;
        if (result.Evaluation.HasFailures) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Evaluation.Errors));
        return new DesignOutcome(result.Optimized, result.Merit, result.Stop != LocalOptimizationStop.ConstraintsNotMet);
    }

    /// <summary>True when <paramref name="a"/> ranks above <paramref name="b"/>: a design that meets
    /// the exact constraints beats one that does not; otherwise the lower merit wins (ties keep
    /// the earlier, <paramref name="b"/>).</summary>
    public static bool Better(double meritA, bool metA, double meritB, bool metB) =>
        metA != metB ? metA : meritA < meritB;

    /// <summary>The surface with <paramref name="shape"/>'s type and parameters (the type's saved
    /// layout, <see cref="SurfaceTypeLayout.Read"/>, in lens units); flags on the old type's
    /// parameter columns are removed.</summary>
    public static OpticsSurfaceSettings WithShape(OpticsSurfaceSettings surface, SurfaceShape? shape, LensUnit unit)
    {
        var flags = surface.Flags?.Where(f => SavedSurfaces.ParameterNumber(f.Column) is null).ToList();
        var s = surface with { Flags = flags is { Count: > 0 } ? flags : null, Parameters = null, ExtraData = null };
        if (shape is null) return s with { Type = null };
        var layout = SurfaceTypes.Find(shape.TypeName)
            ?? throw new ArgumentException($"The surface type '{shape.TypeName}' has no saved parameter layout.");
        double perUnit = OpticsConventions.MetersPer(unit);
        List<OpticsSurfaceParameterSettings>? parameters = null, extra = null;
        foreach (var (number, v) in layout.Read(shape).OrderBy(p => p.Key))
        {
            if (v == 0) continue;
            var info = layout.Parameter(number)!;
            var entry = new OpticsSurfaceParameterSettings { Number = number, Value = v / SurfaceTypes.Factor(info.LengthPower, perUnit) };
            if (info.IsExtraData) (extra ??= new()).Add(entry);
            else (parameters ??= new()).Add(entry);
        }
        return s with { Type = layout.Name, Parameters = parameters, ExtraData = extra };
    }

    /// <summary>The surface with parameter <paramref name="number"/> set (in the parameter or the
    /// extra-data list, as its type keeps it).</summary>
    public static OpticsSurfaceSettings WithParameter(OpticsSurfaceSettings s, int number, double value)
    {
        bool extra = SavedSurfaces.IsExtraData(s, number);
        var list = (extra ? s.ExtraData : s.Parameters)?.Where(p => p.Number != number).ToList() ?? new();
        list.Add(new OpticsSurfaceParameterSettings { Number = number, Value = value });
        list.Sort((a, b) => a.Number.CompareTo(b.Number));
        return extra ? s with { ExtraData = list } : s with { Parameters = list };
    }

    /// <summary>The record with every Variable flag removed except those <paramref name="keep"/>
    /// accepts (surface, column); multi-configuration variables are removed too.</summary>
    public static OpticsSettings KeepVariables(OpticsSettings optics, Func<int, string?, bool> keep)
    {
        var surfaces = optics.Surfaces!.ToList();
        for (int j = 0; j < surfaces.Count; j++)
        {
            var s = surfaces[j];
            if (s.Flags is not { Count: > 0 } flags) continue;
            var kept = flags.Where(f => !string.Equals(f.Kind, SavedSurfaces.VariableFlag, StringComparison.Ordinal) || keep(j, f.Column)).ToList();
            if (kept.Count != flags.Count) surfaces[j] = s with { Flags = kept.Count > 0 ? kept : null };
        }
        var table = optics.MultiConfiguration;
        if (table?.Operands is { Count: > 0 } rows && MultiConfiguration.Variables(optics).Count > 0)
            table = table with
            {
                Operands = rows.Select(r => r?.Values is null ? r : r with
                {
                    Values = r.Values.Select(v => v is { Solve: MultiConfiguration.Variable } ? v with { Solve = null } : v).ToList(),
                }).ToList()!,
            };
        return optics with { Surfaces = surfaces, MultiConfiguration = table };
    }

    public static LensUnit Unit(OpticsSettings optics) => ProjectLens.LensUnitOf(optics.SystemData);

    public static string F(double v) => v.ToString("G10", CultureInfo.InvariantCulture);
}
