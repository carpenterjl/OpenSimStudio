using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Merit.Catalogue;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis.UniversalPlot;

// OP-45: the sweep behind Universal Plot 1D and 2D (docs/Optics-Conventions.md §65).

/// <summary>One independent variable as the settings give it.</summary>
public sealed record UniversalPlotAxis(UniversalPlotCategory Category, UniversalPlotParameter Parameter, int Surface, int Number,
    double Start, double Stop, int Steps);

/// <summary>The dependent variable as the settings give it: an operand code with its Int1, Int2 and
/// Data1 to Data6, or "Merit" with a Line (0 = the merit function's value).</summary>
public sealed record UniversalPlotOperand(string Operand, int Line, int Int1, int Int2, IReadOnlyList<double> Data);

/// <summary>One sweep point's outcome: the operand's value, or why it could not be computed.</summary>
public readonly record struct UniversalPlotPoint(double Value, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// A resolved independent variable: its label and unit, how its value is written into a copy of
/// the optics record, and (for surface and system data) how it is read back from a
/// configuration's record, to find a multi-configuration row that would overwrite it.
/// </summary>
public sealed class UniversalPlotVariable
{
    internal UniversalPlotVariable(string label, string unit, Func<OpticsSettings, double, OpticsSettings> apply,
        Func<OpticsSettings, double>? read, IReadOnlyList<double> values, string? key = null)
    {
        Label = label;
        Key = key ?? label;
        Unit = unit;
        _apply = apply;
        _read = read;
        Values = values;
    }

    private readonly Func<OpticsSettings, double, OpticsSettings> _apply;
    private readonly Func<OpticsSettings, double>? _read;

    /// <summary>"Surface 1 radius", "Wavelength 2", "Configuration 2, operand 1 (THIC)".</summary>
    public string Label { get; }

    /// <summary>The cell written: two variables with the same key write the same value.</summary>
    internal string Key { get; }

    /// <summary>"mm", "µm", "degrees", or empty.</summary>
    public string Unit { get; }

    /// <summary>The swept values, start to stop, both exactly as given.</summary>
    public IReadOnlyList<double> Values { get; }

    /// <summary>"Surface 1 radius (mm)".</summary>
    public string AxisLabel => Unit.Length == 0 ? Label : $"{Label} ({Unit})";

    /// <summary>"Surface 1 radius = 12.5 mm".</summary>
    public string At(double value) => $"{Label} = {StudyReport.Number(value, "g10")}{(Unit.Length == 0 ? "" : " " + Unit)}";

    /// <summary>The record with this variable set to <paramref name="value"/>; nothing else changes.</summary>
    public OpticsSettings Apply(OpticsSettings system, double value) => _apply(system, value);

    internal Func<OpticsSettings, double>? Read => _read;
}

/// <summary>A resolved dependent variable: the merit function evaluated at each point and how its
/// value is read.</summary>
public sealed class UniversalPlotDependent
{
    internal UniversalPlotDependent(string label, string unit, MeritFunction function, int? line, IReadOnlyList<string> columns,
        IReadOnlyList<string> notes)
    {
        Label = label;
        Unit = unit;
        Function = function;
        Line = line;
        Columns = columns;
        Notes = notes;
    }

    /// <summary>"Effective focal length (EFFL)", "Merit function", "Merit function row 3 (EFFL)".</summary>
    public string Label { get; }

    /// <summary>The short name shown on the colour bar: the code, or "merit".</summary>
    public string Unit { get; }

    /// <summary>A one-row function for an operand; the system's merit function for Merit.</summary>
    public MeritFunction Function { get; }

    /// <summary>Null for an operand (row 1 of <see cref="Function"/>); 0 for the merit function's
    /// value; k for row k of the merit function.</summary>
    public int? Line { get; }

    /// <summary>The operand's columns as "Wave = 1", for the settings listing.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>What the reader should know: unused columns given values, merit function load notes.</summary>
    public IReadOnlyList<string> Notes { get; }
}

/// <summary>
/// The sweep of a Universal Plot (OpticStudio 2025 R2 help, Universal Plot 1-D and 2-D): the value
/// of any registered merit operand, or of the merit function or one of its rows, while one or two
/// independent variables step from a start to a stop value.
/// <para>
/// Each point is a copy of the optics record with the variables written into it
/// (<see cref="UniversalPlotVariable.Apply"/>), evaluated by <see cref="MeritEvaluator"/> over
/// <see cref="ProjectMeritConfigurations"/>: multi-configuration pickups, then solves and pickups
/// of the lens, are evaluated again at every point, exactly as the local optimizer evaluates a
/// trial design. The operand is evaluated in the current configuration. The caller's record is
/// never changed. Points are evaluated in parallel, each into its own slot, with the evaluator
/// single-threaded inside a point, so the result is the same bits at any thread count.
/// </para>
/// <para>
/// A point whose operand cannot be computed (a ray that misses, TIR, a solve that fails, system
/// data made invalid by the swept value) carries the reason (<see cref="UniversalPlotPoint.Error"/>);
/// it is never given a number.
/// </para>
/// </summary>
public static class UniversalPlotEngine
{
    /// <summary>The independent variable of <paramref name="axis"/> on <paramref name="system"/>
    /// (the whole record, multi-configuration table included). Throws
    /// <see cref="ArgumentException"/> naming what cannot be swept and why.</summary>
    public static UniversalPlotVariable Resolve(OpticsSettings system, UniversalPlotAxis axis, IReadOnlyList<OpticalCatalog> catalogs, string who)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(catalogs);
        if (!double.IsFinite(axis.Start) || !double.IsFinite(axis.Stop))
            throw new ArgumentException($"{who}: the start and stop values must be finite numbers (they are {N(axis.Start)} and {N(axis.Stop)}).");
        if (axis.Start == axis.Stop)
            throw new ArgumentException($"{who}: the start and stop values are both {N(axis.Start)}; give a range.");
        if (axis.Steps < 2)
            throw new ArgumentException($"{who}: the number of steps must be at least 2 (it is {axis.Steps}).");
        var values = Values(axis);
        var variable = axis.Category switch
        {
            UniversalPlotCategory.Surface => SurfaceVariable(system, axis, values, who),
            UniversalPlotCategory.System => SystemVariable(system, axis, values, who),
            UniversalPlotCategory.Configuration => ConfigurationVariable(system, axis, values, who),
            UniversalPlotCategory.NonSequential => throw new ArgumentException(
                $"{who}: non-sequential data (object positions and parameters) is not built: this program has no non-sequential system yet."),
            _ => throw new ArgumentException($"{who}: unknown category {axis.Category}."),
        };
        CheckNotOverwrittenByTable(system, variable, catalogs, who);
        return variable;
    }

    /// <summary>The swept values: <see cref="OpenSim.Core.Studies.SweepParameter.Linear"/> with the
    /// last value set to the stop value exactly.</summary>
    private static double[] Values(UniversalPlotAxis axis)
    {
        var values = OpenSim.Core.Studies.SweepParameter.Linear("v", axis.Start, axis.Stop, axis.Steps).Values.ToArray();
        values[^1] = axis.Stop;
        return values;
    }

    private static string N(double v) => StudyReport.Number(v, "g10");

    // ------------------------------------------------------------------ surface data

    private static UniversalPlotVariable SurfaceVariable(OpticsSettings system, UniversalPlotAxis axis, double[] values, string who)
    {
        var surfaces = system.Surfaces;
        if (surfaces is not { Count: > 0 })
            throw new ArgumentException($"{who}: the system has no sequential surfaces to sweep.");
        int j = axis.Surface;
        if (j < 0 || j >= surfaces.Count)
            throw new ArgumentException($"{who}: surface {j} does not exist; the lens has surfaces 0 (object) to {surfaces.Count - 1} (image).");
        var s = surfaces[j] ?? new OpticsSurfaceSettings();
        string unit = ParaxialReport.UnitName(ProjectLens.LensUnitOf(system.SystemData));
        string column;
        string label;
        string valueUnit;
        Func<OpticsSettings, double, OpticsSettings> apply;
        Func<OpticsSettings, double> read;
        switch (axis.Parameter)
        {
            case UniversalPlotParameter.Radius:
                column = SavedSurfaces.RadiusColumn; label = $"Surface {j} radius"; valueUnit = unit;
                apply = (r, x) => WithSurface(r, j, t => t with { Radius = x == 0 ? null : x });
                read = r => SurfaceOf(r, j).Radius ?? 0;
                break;
            case UniversalPlotParameter.Curvature:
                column = SavedSurfaces.RadiusColumn; label = $"Surface {j} curvature"; valueUnit = "1/" + unit;
                apply = (r, x) => WithSurface(r, j, t => t with { Radius = x == 0 ? null : 1 / x });
                read = r => SurfaceOf(r, j).Radius is { } radius && radius != 0 ? 1 / radius : 0;
                break;
            case UniversalPlotParameter.Thickness:
                column = SavedSurfaces.ThicknessColumn; label = $"Surface {j} thickness"; valueUnit = unit;
                apply = (r, x) => WithSurface(r, j, t => t with { Thickness = x, ThicknessIsInfinite = false });
                read = r => SurfaceOf(r, j) is { ThicknessIsInfinite: true } ? double.PositiveInfinity : SurfaceOf(r, j).Thickness;
                break;
            case UniversalPlotParameter.Conic:
                column = SavedSurfaces.ConicColumn; label = $"Surface {j} conic"; valueUnit = "";
                apply = (r, x) => WithSurface(r, j, t => t with { Conic = x });
                read = r => SurfaceOf(r, j).Conic;
                break;
            case UniversalPlotParameter.Parameter:
            {
                int n = axis.Number;
                string type = string.IsNullOrWhiteSpace(s.Type) ? SavedSurfaces.StandardType : s.Type!;
                if (string.Equals(type, SavedSurfaces.StandardType, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"{who}: the Standard surface {j} has no parameter {n} (a Standard surface has no parameters).");
                var layout = SurfaceTypes.Find(type)
                    ?? throw new ArgumentException($"{who}: surface {j} has the type '{type}', which this program does not know, so its parameters cannot be swept.");
                var info = layout.Parameter(n)
                    ?? throw new ArgumentException($"{who}: the {layout.Name} surface {j} has no parameter {n}{(layout.Parameters.Count == 0 ? " (it has no parameters)" : $"; its parameters are {string.Join(", ", layout.Parameters.Select(p => p.Number))}")}.");
                if (info.IsInteger)
                    throw new ArgumentException($"{who}: surface {j} {SurfaceTypeLayout.Label(info)} takes a whole number and cannot be swept continuously.");
                column = SavedSurfaces.ParameterColumn(n);
                label = $"Surface {j} {SurfaceTypeLayout.Label(info)}";
                valueUnit = info.IsAngle ? "degrees" : info.LengthPower switch
                {
                    0 => "",
                    1 => unit,
                    _ => $"{unit}^{info.LengthPower.ToString(CultureInfo.InvariantCulture)}",
                };
                var variable = new LensVariable { Kind = LensVariableKind.Parameter, Surface = j, Column = column, Label = label };
                apply = (r, x) => LensVariables.Apply(r, new[] { variable }, new[] { x });
                read = r => SavedSurfaces.Value(SurfaceOf(r, j), n);
                break;
            }
            default:
                throw new ArgumentException($"{who}: {axis.Parameter} is not surface data; surface data is Radius, Curvature, Thickness, Conic or Parameter.");
        }
        if (s.Flags?.FirstOrDefault(f => f is not null && f.Column == column
                && (f.Kind == SavedSurfaces.SolveFlag || f.Kind == SavedSurfaces.PickupFlag)) is { } flag)
        {
            string what = flag.Kind == SavedSurfaces.PickupFlag ? "a pickup" : $"a {flag.Solve ?? "solve"} solve";
            throw new ArgumentException($"{who}: {label} is driven by {what}, which would overwrite every swept value. "
                                        + "Remove the solve, or sweep a value it reads.");
        }
        return new UniversalPlotVariable(label, valueUnit, apply, read, values, $"surface {j} {column}");
    }

    private static OpticsSurfaceSettings SurfaceOf(OpticsSettings r, int j) => r.Surfaces![j] ?? new OpticsSurfaceSettings();

    private static OpticsSettings WithSurface(OpticsSettings r, int j, Func<OpticsSurfaceSettings, OpticsSurfaceSettings> edit)
    {
        var surfaces = r.Surfaces!.ToList();
        surfaces[j] = edit(surfaces[j] ?? new OpticsSurfaceSettings());
        return r with { Surfaces = surfaces };
    }

    // ------------------------------------------------------------------ system data

    /// <summary>The saved system data with the field and wavelength lists written out (a record
    /// without them uses the default lists, which a sweep must edit).</summary>
    private static OpticsSystemSettings SystemOf(OpticsSettings r)
    {
        var saved = r.SystemData ?? SystemDataSettings.ToSettings(OpticalSystemData.Default);
        if (saved.Fields is not null && saved.Wavelengths is not null) return saved;
        var full = SystemDataSettings.ToSettings(SystemDataSettings.FromSettings(saved, new List<string>()));
        return saved with { Fields = saved.Fields ?? full.Fields, Wavelengths = saved.Wavelengths ?? full.Wavelengths };
    }

    private static UniversalPlotVariable SystemVariable(OpticsSettings system, UniversalPlotAxis axis, double[] values, string who)
    {
        var saved = SystemOf(system);
        var data = SystemDataSettings.FromSettings(saved, new List<string>());
        string unit = ParaxialReport.UnitName(data.Units.LensUnit);
        int n = axis.Number;
        OpticsSettings Sys(OpticsSettings r, Func<OpticsSystemSettings, OpticsSystemSettings> edit) => r with { SystemData = edit(SystemOf(r)) };

        int fields = saved.Fields!.Count, waves = saved.Wavelengths!.Count;
        void Field()
        {
            if (n < 1 || n > fields) throw new ArgumentException($"{who}: field {n} does not exist; the system has fields 1 to {fields}.");
        }
        void Wave()
        {
            if (n < 1 || n > waves) throw new ArgumentException($"{who}: wavelength {n} does not exist; the system has wavelengths 1 to {waves}.");
        }
        OpticsSettings F(OpticsSettings r, Func<OpticsFieldSettings, OpticsFieldSettings> edit) => Sys(r, d =>
        {
            var list = d.Fields!.ToList();
            list[n - 1] = edit(list[n - 1] ?? new OpticsFieldSettings());
            return d with { Fields = list };
        });
        OpticsSettings W(OpticsSettings r, Func<OpticsWavelengthSettings, OpticsWavelengthSettings> edit) => Sys(r, d =>
        {
            var list = d.Wavelengths!.ToList();
            list[n - 1] = edit(list[n - 1] ?? new OpticsWavelengthSettings());
            return d with { Wavelengths = list };
        });
        OpticsFieldSettings FieldOf(OpticsSettings r) => SystemOf(r).Fields![n - 1] ?? new OpticsFieldSettings();
        OpticsWavelengthSettings WaveOf(OpticsSettings r) => SystemOf(r).Wavelengths![n - 1] ?? new OpticsWavelengthSettings();
        string fieldUnit = FieldTypes.IsAngle(data.Fields.Type) ? "degrees" : unit;

        switch (axis.Parameter)
        {
            case UniversalPlotParameter.ApertureValue:
                return new(SystemLabel("Aperture value", data.Aperture.Type.ToString()), ApertureTypes.IsLength(data.Aperture.Type) ? unit : "",
                    (r, x) => Sys(r, d => d with { ApertureValue = x }), r => SystemOf(r).ApertureValue, values);
            case UniversalPlotParameter.ApodizationFactor:
                return new("Apodization factor", "", (r, x) => Sys(r, d => d with { ApodizationFactor = x }), r => SystemOf(r).ApodizationFactor, values);
            case UniversalPlotParameter.Temperature:
                return new("Temperature", "°C", (r, x) => Sys(r, d => d with { TemperatureCelsius = x }), r => SystemOf(r).TemperatureCelsius, values);
            case UniversalPlotParameter.Pressure:
                return new("Pressure", "atm", (r, x) => Sys(r, d => d with { PressureAtmospheres = x }), r => SystemOf(r).PressureAtmospheres, values);
            case UniversalPlotParameter.FieldX:
                Field();
                return new($"Field {n} x", fieldUnit, (r, x) => F(r, f => f with { X = x }), r => FieldOf(r).X, values);
            case UniversalPlotParameter.FieldY:
                Field();
                return new($"Field {n} y", fieldUnit, (r, x) => F(r, f => f with { Y = x }), r => FieldOf(r).Y, values);
            case UniversalPlotParameter.FieldWeight:
                Field();
                return new($"Field {n} weight", "", (r, x) => F(r, f => f with { Weight = x }), r => FieldOf(r).Weight, values);
            case UniversalPlotParameter.FieldDecenterX:
                Field();
                return new($"Field {n} VDX", "", (r, x) => F(r, f => f with { DecenterX = x }), r => FieldOf(r).DecenterX, values);
            case UniversalPlotParameter.FieldDecenterY:
                Field();
                return new($"Field {n} VDY", "", (r, x) => F(r, f => f with { DecenterY = x }), r => FieldOf(r).DecenterY, values);
            case UniversalPlotParameter.FieldCompressionX:
                Field();
                return new($"Field {n} VCX", "", (r, x) => F(r, f => f with { CompressionX = x }), r => FieldOf(r).CompressionX, values);
            case UniversalPlotParameter.FieldCompressionY:
                Field();
                return new($"Field {n} VCY", "", (r, x) => F(r, f => f with { CompressionY = x }), r => FieldOf(r).CompressionY, values);
            case UniversalPlotParameter.FieldTilt:
                Field();
                return new($"Field {n} VAN", "degrees", (r, x) => F(r, f => f with { TiltDegrees = x }), r => FieldOf(r).TiltDegrees, values);
            case UniversalPlotParameter.Wavelength:
                Wave();
                return new($"Wavelength {n}", "µm", (r, x) => W(r, w => w with { Micrometers = x }), r => WaveOf(r).Micrometers, values);
            case UniversalPlotParameter.WavelengthWeight:
                Wave();
                return new($"Wavelength {n} weight", "", (r, x) => W(r, w => w with { Weight = x }), r => WaveOf(r).Weight, values);
            default:
                throw new ArgumentException($"{who}: {axis.Parameter} is not system data; system data is ApertureValue, ApodizationFactor, Temperature, Pressure, FieldX, FieldY, FieldWeight, FieldDecenterX, FieldDecenterY, FieldCompressionX, FieldCompressionY, FieldTilt, Wavelength or WavelengthWeight.");
        }
    }

    private static string SystemLabel(string what, string type) => $"{what} ({type})";

    // ------------------------------------------------------------------ configuration data

    private static UniversalPlotVariable ConfigurationVariable(OpticsSettings system, UniversalPlotAxis axis, double[] values, string who)
    {
        if (axis.Parameter != UniversalPlotParameter.Operand)
            throw new ArgumentException($"{who}: configuration data is a row of the multi-configuration table: choose the parameter Operand, the row in Number and the configuration in Surface.");
        var table = system.MultiConfiguration
            ?? throw new ArgumentException($"{who}: the system has no multi-configuration table, so there is no configuration data to sweep.");
        int rows = table.Operands?.Count ?? 0, n = axis.Number, c = axis.Surface;
        if (n < 1 || n > rows) throw new ArgumentException($"{who}: the multi-configuration table has no row {n}; it has rows 1 to {rows}.");
        if (c < 1 || c > table.ConfigurationCount)
            throw new ArgumentException($"{who}: there is no configuration {c}; the table has configurations 1 to {table.ConfigurationCount}.");
        var row = table.Operands![n - 1] ?? new OpticsConfigOperandSettings();
        string label = $"Configuration {c}, operand {n} ({row.Type})";
        var operand = MultiConfigOperands.Find(row.Type);
        if (operand is null || !operand.IsSupported)
            throw new ArgumentException($"{who}: {label}: the operand '{row.Type}' is not one this program applies, so it cannot be swept.");
        if (operand.Kind != MultiConfigValueKind.Number)
            throw new ArgumentException($"{who}: {label}: only real-number cells can be swept; this operand holds {(operand.Kind == MultiConfigValueKind.Integer ? "a whole number" : "a text")}.");
        var cellValues = row.Values;
        string? solve = cellValues is not null && c - 1 < cellValues.Count ? cellValues[c - 1]?.Solve : null;
        if (solve is MultiConfiguration.Pickup or MultiConfiguration.ThermalPickup)
            throw new ArgumentException($"{who}: {label} is a {(solve == MultiConfiguration.Pickup ? "pickup" : "thermal pickup")}, which would overwrite every swept value.");
        var cell = new MultiConfigCell(n, c);
        return new UniversalPlotVariable(label, "", (r, x) => MultiConfiguration.WithValue(r, cell, x), null, values);
    }

    // ------------------------------------------------------------------ the table's overrides

    private static bool Same(double a, double b) => a == b || Math.Abs(a - b) <= 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b));

    /// <summary>
    /// Refuses a surface or system variable that the current configuration's multi-configuration
    /// table sets, since the table would overwrite every swept value. OpticStudio "automatically
    /// alters the corresponding data on the Multi-Configuration operand as well" (help, Universal
    /// Plot 1-D); this program does not, and names the row to sweep instead. Found by writing the
    /// start and stop values and reading them back from the configuration's record; the driving
    /// rows are those whose removal lets the value through.
    /// </summary>
    private static void CheckNotOverwrittenByTable(OpticsSettings system, UniversalPlotVariable variable, IReadOnlyList<OpticalCatalog> catalogs, string who)
    {
        if (system.MultiConfiguration is not { } table || variable.Read is not { } read) return;
        int current = MultiConfiguration.CurrentConfiguration(system);
        bool? PassesThrough(OpticsSettings record, double x)
        {
            try { return Same(read(MultiConfiguration.SettingsFor(variable.Apply(record, x), current, catalogs)), x); }
            catch (Exception e) when (e is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException) { return null; }   // the point fails by itself
        }
        foreach (double x in new[] { variable.Values[0], variable.Values[^1] })
        {
            if (PassesThrough(system, x) != false) continue;
            var operands = table.Operands ?? new List<OpticsConfigOperandSettings>();
            var drivers = new List<string>();
            for (int i = 0; i < operands.Count; i++)
            {
                var without = system with { MultiConfiguration = table with { Operands = operands.Where((_, k) => k != i).ToList() } };
                if (PassesThrough(without, x) == true) drivers.Add($"row {i + 1} ({operands[i]?.Type})");
            }
            string rows = drivers.Count == 0 ? "the multi-configuration table" : "multi-configuration " + string.Join(" and ", drivers);
            string instead = drivers.Count == 1 ? $" Sweep that row instead (category Configuration, parameter Operand, Number {drivers[0].Split(' ')[1]}, Surface {current})." : "";
            throw new ArgumentException($"{who}: {variable.Label} is set by {rows} in configuration {current} (the current one), which would overwrite every swept value.{instead}");
        }
    }

    // ------------------------------------------------------------------ the dependent variable

    /// <summary>The dependent variable. Throws <see cref="ArgumentException"/> for an unknown or
    /// not-built operand, a control row, a math operand alone, an empty merit function or a bad Line.</summary>
    public static UniversalPlotDependent Resolve(OpticsSettings system, UniversalPlotOperand operand, MeritOperandRegistry registry, string who)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(operand);
        ArgumentNullException.ThrowIfNull(registry);
        string code = (operand.Operand ?? "").Trim();
        var notes = new List<string>();
        int configurations = MultiConfiguration.ConfigurationCount(system);
        if (code.Length == 0)
            throw new ArgumentException($"{who}: choose an operand: a merit operand's four-letter code, or Merit for the merit function.");
        if (code.Equals("Merit", StringComparison.OrdinalIgnoreCase))
        {
            var function = MeritFunctionSettings.FromSettings(system.MeritFunction, registry, notes);
            if (function.Rows.Count == 0)
                throw new ArgumentException($"{who}: the merit function is empty; add rows in the Merit Function Editor, or choose an operand.");
            if (MeritEvaluator.Validate(function, registry, configurations) is { Count: > 0 } problems)
                throw new ArgumentException($"{who}: the merit function cannot be evaluated:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
            int line = operand.Line;
            if (line < 0 || line > function.Rows.Count)
                throw new ArgumentException($"{who}: Line {line} is not a row of the merit function; use 0 for its value or a row from 1 to {function.Rows.Count}.");
            if (line == 0)
                return new UniversalPlotDependent("Merit function", "merit", function, 0, Array.Empty<string>(), notes);
            var d = registry.Get(function.Row(line).Code);
            if (d.Control != MeritControl.None)
                throw new ArgumentException($"{who}: merit function row {line} is a {d.Code} row, which has no value.");
            return new UniversalPlotDependent($"Merit function row {line} ({d.Code})", d.Code, function, line, Array.Empty<string>(), notes);
        }

        var definition = registry.Find(code)
            ?? throw new ArgumentException($"{who}: {MeritOperandCatalogue.Unknown(code)}");
        if (definition.Control != MeritControl.None)
            throw new ArgumentException($"{who}: {definition.Code} ({definition.Name}) is a control operand and has no value.");
        if (definition.References is not null)
            throw new ArgumentException($"{who}: {definition.Code} reads other rows of a merit function, so it has no value alone; choose Merit and the row's number in Line instead (help, Universal Plot 1-D).");
        if (operand.Line != 0) notes.Add($"Line {operand.Line} is read only with Merit; it is ignored for {definition.Code}.");
        if (operand.Data.Count > MeritOperandRow.DataCount)
            throw new ArgumentException($"{who}: an operand has {MeritOperandRow.DataCount} real parameters; {operand.Data.Count} were given.");
        var row = MeritOperandRow.Of(definition.Code, operand.Int1, operand.Int2, 0, 0, operand.Data.ToArray());
        var columns = new List<string>();
        void Column(string name, string? meaning, double value, string text)
        {
            if (meaning is not null) columns.Add($"{name} ({meaning}) = {text}");
            else if (value != 0) notes.Add($"{name} = {text} is not used by {definition.Code}.");
        }
        Column("Int1", definition.Int1, row.Int1, row.Int1.ToString(CultureInfo.InvariantCulture));
        Column("Int2", definition.Int2, row.Int2, row.Int2.ToString(CultureInfo.InvariantCulture));
        for (int k = 1; k <= MeritOperandRow.DataCount; k++)
            Column($"Data{k}", definition.DataLabel(k), row.DataAt(k), N(row.DataAt(k)));
        return new UniversalPlotDependent(definition.DisplayName, definition.Code, new MeritFunction(new[] { row }), null, columns, notes);
    }

    // ------------------------------------------------------------------ evaluation

    /// <summary>
    /// The operand at each of <paramref name="count"/> points, point <c>i</c> being the record
    /// <paramref name="recordAt"/>(i). Parallel over points, each into its own slot; the same bits
    /// at any <paramref name="maxDegreeOfParallelism"/>.
    /// </summary>
    public static UniversalPlotPoint[] Evaluate(OpticsSettings system, IReadOnlyList<OpticalCatalog> catalogs, UniversalPlotDependent dependent,
        int count, Func<int, OpticsSettings> recordAt, MeritOperandRegistry registry, int? maxDegreeOfParallelism, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(dependent);
        ArgumentNullException.ThrowIfNull(recordAt);
        ArgumentNullException.ThrowIfNull(registry);
        int current = MultiConfiguration.CurrentConfiguration(system);
        var baseProblems = SystemProblems(system, current, catalogs) ?? new HashSet<string>();
        var options = new MeritEvaluationOptions { StartConfiguration = current, MaxDegreeOfParallelism = 1 };
        return ParallelSlots.Compute(count, maxDegreeOfParallelism, i =>
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var record = recordAt(i);
                // System data the swept value makes invalid fails the point by name (the evaluator's
                // configurations read the system data without checking it).
                if (SystemProblems(record, current, catalogs) is { } problems && problems.Except(baseProblems).ToList() is { Count: > 0 } added)
                    return new UniversalPlotPoint(double.NaN, "the system data is not valid: " + string.Join(" ", added));
                var e = MeritEvaluator.Evaluate(dependent.Function, new ProjectMeritConfigurations(record, catalogs), registry, options, cancellation);
                return Read(e, dependent);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return new UniversalPlotPoint(double.NaN, ex.Message);
            }
        }, cancellation);
    }

    /// <summary>The current configuration's system-data problems, or null when the configuration
    /// cannot be built (the evaluation then fails the point naming why).</summary>
    private static HashSet<string>? SystemProblems(OpticsSettings record, int current, IReadOnlyList<OpticalCatalog> catalogs)
    {
        try
        {
            var settings = MultiConfiguration.SettingsFor(record, current, catalogs);
            return SystemDataSettings.FromSettings(settings.SystemData, new List<string>()).Validate().ToHashSet(StringComparer.Ordinal);
        }
        catch (ArgumentException) { return null; }
    }

    private static UniversalPlotPoint Read(MeritEvaluation e, UniversalPlotDependent dependent)
    {
        if (dependent.Line == 0)
        {
            var failed = e.Rows.Where(r => r.Counts && r.Status == MeritRowStatus.Failed).ToList();
            return failed.Count == 0 ? new UniversalPlotPoint(e.Value, null)
                : new UniversalPlotPoint(double.NaN, string.Join(" ", failed.Select(r => r.Error)));
        }
        var row = e.Rows[(dependent.Line ?? 1) - 1];
        return row.Status switch
        {
            MeritRowStatus.Evaluated => new UniversalPlotPoint(row.Value, null),
            MeritRowStatus.Failed => new UniversalPlotPoint(double.NaN, row.Error),
            _ => new UniversalPlotPoint(double.NaN, $"row {row.Number} was not evaluated (a GOTO, SKIS, SKIN or ENDX row skipped it)."),
        };
    }
}
