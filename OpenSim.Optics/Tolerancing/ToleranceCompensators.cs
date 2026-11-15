using OpenSim.Core.Model;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Tolerancing;

// OP-64: the compensators of a tolerance run (docs/Optics-Conventions.md §42).

/// <summary>A compensated lens: the record, its criterion and the compensators' changes.</summary>
internal sealed record CompensatedLens(OpticsSettings Settings, double Criterion, IReadOnlyList<double> Compensators, IReadOnlyList<string> Notes);

/// <summary>
/// Evaluates the compensators of a tolerance run (help, "Tolerancing", "Comp"; "Evaluating
/// Compensators"; "How OpticStudio Computes the Tolerance Analysis"; "General Comments About Min
/// &amp; Max Values on Compensators").
/// <para>
/// <b>None</b>: the criterion of the perturbed lens as it is.
/// </para>
/// <para>
/// <b>Paraxial focus</b>: "Back focus is implemented as a compensator using a paraxial focus error
/// solve. This means the focus is adjusted to maintain the amount of defocus in the nominal lens
/// design". The defocus is the thickness before the image minus the paraxial image distance of the
/// marginal ray (<see cref="ToleranceCriterionEvaluator.Defocus"/>); each perturbed lens is moved
/// at the image by the shift that restores the nominal value, in every configuration the criterion
/// reads. COMP and CPAR rows are ignored (help: "all defined compensators and compensator boundary
/// constraints are ignored"). The help's other page also says "the back focal position is set to
/// paraxial focus" on the nominal lens; this program keeps the nominal lens's defocus as the
/// "Evaluating Compensators" page describes (§10 item 38).
/// </para>
/// <para>
/// <b>Optimize</b>: every variable flag of the lens is removed and the COMP, CPAR and CMCO cells
/// (all, or the rows chosen) are flagged Variable ("OpticStudio then removes all of the variables
/// ... the compensating parameters defined by COMP and CPAR specified are set as variables"); OP-57's
/// <see cref="LocalOptimizer"/> then minimises a merit function built from the criterion: one row
/// per field (weight the field's weight, target 0, or 1 for the Strehl ratio) and, for each COMP
/// row, a boundary row holding the cell within nominal + Min and nominal + Max (weight 1). Bounds
/// are soft, as the help says ("these boundaries may be violated if doing so would provide better
/// performance"). CPAR and CMCO bounds are not built (their values are not readable from the traced
/// lens), and with the merit function criterion no bounds are added (help). "Optimize All (DLS)"
/// runs one Orthogonal Descent cycle, then damped least squares; "(OD)" Orthogonal Descent only.
/// A compensator cell that already carries a solve or pickup is refused: the optimizer could not
/// move it.
/// </para>
/// </summary>
internal sealed class ToleranceCompensators
{
    private const string CriterionCode = "TCRT";
    private const string BoundCode = "TCBD";

    private readonly ToleranceRunOptions _o;
    private readonly IReadOnlyList<OpticalCatalog> _catalogs;
    private readonly ToleranceCriterionEvaluator _e;
    private readonly OpticsSettings _nominal;
    private readonly IReadOnlyList<ToleranceCompensatorRow> _rows;
    private readonly double[] _defocus;
    private readonly int _configurations;
    private OpticsSettings? _start;

    public ToleranceCompensators(ToleranceRunOptions options, ToleranceTable table, OpticsSettings nominal, IReadOnlyList<OpticalCatalog> catalogs,
        ToleranceCriterionEvaluator evaluator, int configurations)
    {
        _o = options;
        _catalogs = catalogs;
        _e = evaluator;
        _nominal = nominal;
        _configurations = configurations;
        _defocus = new double[configurations];
        var rows = new List<ToleranceCompensatorRow>();
        if (IsOptimize)
        {
            var all = table.Compensators();
            var chosen = all;
            if (options.CompensatorRows is { } pick)
            {
                foreach (int r in pick)
                    if (!all.Any(c => c.Row == r))
                        throw new ArgumentException($"Tolerance run: row {r} is not a COMP, CPAR or CMCO row of the tolerance data, so it cannot be a chosen compensator.");
                chosen = all.Where(c => pick.Contains(c.Row)).ToArray();
            }
            foreach (var c in chosen)
            {
                if (c.Code == "CEDV")
                    throw new ArgumentException($"Tolerance row {c.Row} (CEDV): deprecated and replaced by CPAR (help); use CPAR.");
                rows.Add(new ToleranceCompensatorRow(c, Value(nominal, c, s => s)));
            }
            if (rows.Count == 0)
                throw new ArgumentException("Optimize compensation needs at least one COMP, CPAR or CMCO row in the tolerance data (or choose Paraxial focus or None).");
        }
        _rows = rows;
        Labels = _o.Compensation switch
        {
            ToleranceCompensation.ParaxialFocus => new[] { "Back focus shift (paraxial focus), lens units" },
            ToleranceCompensation.None => Array.Empty<string>(),
            _ => rows.Select(r => r.Label).ToArray(),
        };
    }

    private bool IsOptimize => _o.Compensation is ToleranceCompensation.OptimizeDls or ToleranceCompensation.OptimizeOd;

    public IReadOnlyList<string> Labels { get; }

    public IReadOnlyList<string> Assumptions => _o.Compensation switch
    {
        ToleranceCompensation.None => new[] { "Compensation: none; COMP and CPAR rows are ignored." },
        ToleranceCompensation.ParaxialFocus => new[]
        {
            "Compensation: paraxial focus. Each perturbed lens is moved at the image so that its defocus (the thickness before the image minus the paraxial image distance −y/u of the marginal ray, primary wavelength) equals the nominal lens's (help, \"Evaluating Compensators\"); COMP and CPAR rows are ignored.",
        },
        _ => new[]
        {
            $"Compensation: {(_o.Compensation == ToleranceCompensation.OptimizeDls ? "one Orthogonal Descent cycle, then damped least squares" : "Orthogonal Descent")}, {(_o.OptimizerCycles is int k && k > 0 ? $"{k} cycles" : "automatic cycles")} (OP-57's local optimizer), over: {string.Join("; ", _rows.Select(r => r.Label))}.",
            _o.Criterion == ToleranceCriterion.MeritFunction
                ? "The optimizer minimises the lens's merit function; no compensator bounds are added (help)."
                : "The optimizer minimises √(Σ w r² / Σ w) of the criterion's field values (target 0, or 1 for the Strehl ratio) and soft bounds holding each COMP within nominal + Min to nominal + Max (weight 1, this program's choice); CPAR and CMCO bounds are not built.",
            "The nominal lens is compensated first and its criterion is the nominal; every other variable flag of the lens is ignored.",
        },
    };

    /// <summary>The compensated nominal lens.</summary>
    public CompensatedLens Nominal(CancellationToken token)
    {
        var notes = new List<string>();
        switch (_o.Compensation)
        {
            case ToleranceCompensation.ParaxialFocus:
                foreach (int k in FocusConfigurations())
                {
                    var (lens, system) = ToleranceCriterionEvaluator.Build(_nominal, k, _catalogs);
                    _defocus[k - 1] = ToleranceCriterionEvaluator.Defocus(lens, system, $"nominal lens, configuration {k}");
                }
                _start = _nominal;
                return new CompensatedLens(_nominal, _e.Evaluate(_nominal, new double[_configurations], token), new[] { 0.0 }, notes);
            case ToleranceCompensation.None:
                _start = _nominal;
                return new CompensatedLens(_nominal, _e.Evaluate(_nominal, null, token), Array.Empty<double>(), notes);
            default:
                var optimized = Optimize(_nominal, s => s, token, notes);
                _start = optimized;
                return new CompensatedLens(optimized, _e.Evaluate(optimized, null, token), new double[_rows.Count], notes);
        }
    }

    /// <summary>The compensated criterion of a perturbed record; <paramref name="map"/> takes the
    /// table's (nominal) surface numbers to the record's.</summary>
    public CompensatedLens Evaluate(OpticsSettings perturbed, Func<int, int> map, CancellationToken token)
    {
        switch (_o.Compensation)
        {
            case ToleranceCompensation.ParaxialFocus:
            {
                var shifts = new double[_configurations];
                foreach (int k in FocusConfigurations())
                {
                    var (lens, system) = ToleranceCriterionEvaluator.Build(perturbed, k, _catalogs);
                    shifts[k - 1] = _defocus[k - 1] - ToleranceCriterionEvaluator.Defocus(lens, system, $"configuration {k}");
                }
                double criterion = _e.Evaluate(perturbed, shifts, token);
                var unit = ProjectLens.LensUnitOf(perturbed.SystemData);
                return new CompensatedLens(perturbed, criterion, new[] { OpticsConventions.FromMeters(shifts[_e.Configuration - 1], unit) }, Array.Empty<string>());
            }
            case ToleranceCompensation.None:
                return new CompensatedLens(perturbed, _e.Evaluate(perturbed, null, token), Array.Empty<double>(), Array.Empty<string>());
            default:
            {
                var optimized = Optimize(perturbed, map, token, null);
                var changes = _rows.Select(r => Value(optimized, r.Definition, map) - Value(_start!, r.Definition, s => s)).ToArray();
                return new CompensatedLens(optimized, _e.Evaluate(optimized, null, token), changes, Array.Empty<string>());
            }
        }
    }

    /// <summary>The configurations whose focus the criterion reads: all for the merit function, else the toleranced one.</summary>
    private IEnumerable<int> FocusConfigurations() => _o.Criterion == ToleranceCriterion.MeritFunction
        ? Enumerable.Range(1, _configurations) : new[] { _e.Configuration };

    // ------------------------------------------------------------------ optimize

    private OpticsSettings Optimize(OpticsSettings settings, Func<int, int> map, CancellationToken token, List<string>? notes)
    {
        var withVariables = WithCompensatorVariables(settings, map);
        MeritFunction function;
        MeritOperandRegistry registry;
        if (_o.Criterion == ToleranceCriterion.MeritFunction)
        {
            function = _e.Merit!;
            registry = MeritOperandRegistry.CreateDefault();
        }
        else
        {
            registry = Registry(token);
            function = Function(map);
        }
        LocalOptimizationOptions Options(LocalOptimizationAlgorithm a, LocalOptimizationCycles c, int count) => new()
        {
            Algorithm = a, Cycles = c, CycleCount = Math.Max(1, count), MaxDegreeOfParallelism = 1, CancellationToken = token,
        };
        var cycles = _o.OptimizerCycles is int k && k > 0 ? LocalOptimizationCycles.Fixed : LocalOptimizationCycles.Automatic;
        int n = _o.OptimizerCycles ?? 0;
        var current = withVariables;
        if (_o.Compensation == ToleranceCompensation.OptimizeDls)
        {
            var od = LocalOptimizer.Run(current, _catalogs, Options(LocalOptimizationAlgorithm.OrthogonalDescent, LocalOptimizationCycles.Fixed, 1), function, registry);
            if (od.Cancelled) throw new OperationCanceledException(token);
            current = od.Optimized;
        }
        var result = LocalOptimizer.Run(current, _catalogs,
            Options(_o.Compensation == ToleranceCompensation.OptimizeDls ? LocalOptimizationAlgorithm.DampedLeastSquares : LocalOptimizationAlgorithm.OrthogonalDescent, cycles, n),
            function, registry);
        if (result.Cancelled) throw new OperationCanceledException(token);
        notes?.AddRange(result.Log.Select(l => "Nominal compensation: " + l));
        return result.Optimized;
    }

    /// <summary>The record with every variable flag removed and the compensator cells flagged Variable.</summary>
    internal OpticsSettings WithCompensatorVariables(OpticsSettings settings, Func<int, int> map)
    {
        var surfaces = (settings.Surfaces ?? new List<OpticsSurfaceSettings>()).Select(s =>
            s?.Flags is { } f && f.Any(x => x is not null && string.Equals(x.Kind, SavedSurfaces.VariableFlag, StringComparison.Ordinal))
                ? s with { Flags = f.Where(x => x is not null && !string.Equals(x.Kind, SavedSurfaces.VariableFlag, StringComparison.Ordinal)).ToList() }
                : s!).ToList();
        var table = settings.MultiConfiguration;
        List<OpticsConfigOperandSettings>? operands = table?.Operands?.Select(o => o is null ? null! : o with
        {
            Values = o.Values?.Select(v => v is not null && string.Equals(v.Solve, "Variable", StringComparison.Ordinal) ? v with { Solve = null } : v!).ToList(),
        }).ToList();
        foreach (var r in _rows)
        {
            var c = r.Definition;
            string who = $"Tolerance row {c.Row} ({c.Code})";
            if (c.Code == "CMCO")
            {
                var values = operands is not null && c.Int1 >= 1 && c.Int1 <= operands.Count ? operands[c.Int1 - 1]?.Values : null;
                if (values is null || c.Int2 < 1 || c.Int2 > values.Count)
                    throw new ArgumentException($"{who}: the multi-configuration cell (operand {c.Int1}, configuration {c.Int2}) does not exist.");
                var cell = values[c.Int2 - 1] ?? new OpticsConfigValueSettings();
                if (cell.Solve is not null)
                    throw new ArgumentException($"{who}: the multi-configuration cell (operand {c.Int1}, configuration {c.Int2}) has a {cell.Solve}; a compensator cannot vary it.");
                values[c.Int2 - 1] = cell with { Solve = "Variable" };
                continue;
            }
            int j = map(c.Int1);
            if (j < 0 || j >= surfaces.Count) throw new ArgumentException($"{who}: surface {c.Int1} is not a surface of the lens.");
            string column = Column(c);
            var s = surfaces[j] ?? throw new ArgumentException($"{who}: surface {c.Int1} is empty in the saved lens.");
            if (s.Flags is { } flags && flags.Any(x => x is not null && string.Equals(x.Column, column, StringComparison.Ordinal)))
                throw new ArgumentException($"{who}: surface {c.Int1}'s {column} has a solve or pickup, so a compensator cannot vary it; remove the solve or the compensator row.");
            surfaces[j] = s with { Flags = (s.Flags ?? new List<OpticsCellFlagSettings>()).Append(new OpticsCellFlagSettings { Column = column, Kind = SavedSurfaces.VariableFlag }).ToList() };
        }
        var result = settings with { Surfaces = surfaces };
        if (table is not null) result = result with { MultiConfiguration = table with { Operands = operands } };
        return result;
    }

    private static string Column(ToleranceCompensator c) => c.Code == "CPAR"
        ? SavedSurfaces.ParameterColumn(c.Int2)
        : c.Int2 switch { 0 => SavedSurfaces.ThicknessColumn, 1 => SavedSurfaces.RadiusColumn, _ => SavedSurfaces.ConicColumn };

    /// <summary>A compensator's value in its unit (lens units, 1/lens unit for a curvature, the
    /// parameter's or the multi-configuration operand's unit).</summary>
    internal static double Value(OpticsSettings settings, ToleranceCompensator c, Func<int, int> map)
    {
        if (c.Code == "CMCO") return MultiConfiguration.ValueOf(settings, new MultiConfigCell(c.Int1, c.Int2));
        var s = settings.Surfaces![map(c.Int1)];
        if (c.Code == "CPAR") return SavedSurfaces.Value(s, c.Int2);
        return c.Int2 switch
        {
            0 => s.ThicknessIsInfinite ? double.PositiveInfinity : s.Thickness,
            1 => SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value,
            _ => s.Conic,
        };
    }

    private MeritFunction Function(Func<int, int> map)
    {
        var rows = new List<MeritOperandRow> { MeritOperandRow.Of("CONF", _e.Configuration) };
        double target = _e.Info.HigherIsBetter ? 1 : 0;
        if (_o.Criterion == ToleranceCriterion.BoresightError) rows.Add(MeritOperandRow.Of(CriterionCode, -1, 0, 0, 1));
        else for (int f = 0; f < _e.Fields.Count; f++) rows.Add(MeritOperandRow.Of(CriterionCode, f, 0, target, _e.Fields[f].Weight));
        foreach (var r in _rows.Where(r => r.Definition.Code == "COMP"))
        {
            var c = r.Definition;
            rows.Add(MeritOperandRow.Of(BoundCode, map(c.Int1), c.Int2, 0, 1, r.Nominal + c.Min, r.Nominal + c.Max));
        }
        return new MeritFunction(rows);
    }

    private MeritOperandRegistry Registry(CancellationToken token)
    {
        var r = new MeritOperandRegistry();
        r.Register(MeritOperandRegistry.CreateDefault().Get("CONF"));
        r.Register(new MeritOperandDefinition
        {
            Code = CriterionCode, Name = "Tolerance criterion", Category = "Tolerancing", Source = "OP-64 (internal)",
            Description = "The tolerance criterion at field Int1 (−1: the boresight error).", Int1 = "Field",
            Compute = c =>
            {
                try
                {
                    return c.Row.Int1 < 0 ? ToleranceCriterionEvaluator.Boresight(c.Lens, c.System) : _e.FieldValue(c.Lens, c.System, c.Row.Int1, token);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new MeritOperandException(e.Message); }
            },
        });
        r.Register(new MeritOperandDefinition
        {
            Code = BoundCode, Name = "Compensator bound", Category = "Tolerancing", Source = "OP-64 (internal)",
            Description = "Zero while the compensator of surface Int1 (Int2: 0 thickness, 1 curvature, 2 conic) lies in [Data1, Data2]; else its distance outside.",
            Int1 = "Surf", Int2 = "Code", Data = new[] { "Lower", "Upper" },
            Compute = c =>
            {
                var s = c.Lens.Surfaces[c.Surface(c.Row.Int1)];
                double v = c.Row.Int2 switch { 0 => c.Length(s.Thickness), 1 => c.Curvature(s.Curvature), _ => s.Conic };
                double lo = c.Row.DataAt(1), hi = c.Row.DataAt(2);
                return v < lo ? v - lo : v > hi ? v - hi : 0;
            },
        });
        return r;
    }
}

/// <summary>A compensator row and its value on the nominal lens as entered (the bounds' origin).</summary>
internal sealed record ToleranceCompensatorRow(ToleranceCompensator Definition, double Nominal)
{
    public string Label => Definition.Code switch
    {
        "CMCO" => $"row {Definition.Row} CMCO: operand {Definition.Int1}, configuration {Definition.Int2}",
        "CPAR" => $"row {Definition.Row} CPAR: surface {Definition.Int1} parameter {Definition.Int2}",
        _ => $"row {Definition.Row} COMP: surface {Definition.Int1} {Definition.Int2 switch { 0 => "thickness", 1 => "curvature", _ => "conic" }}",
    };
}
