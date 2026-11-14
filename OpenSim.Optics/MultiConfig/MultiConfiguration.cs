using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.MultiConfig;

/// <summary>One cell of the table: operand row and configuration, both 1-based.</summary>
public readonly record struct MultiConfigCell(int Operand, int Configuration);

/// <summary>
/// The multi-configuration system (plan item OP-30; OpticStudio's Multi-Configuration Editor,
/// OpticStudio 2025 R2 help, plans/sources/optics/op-30-help): an optics record with a
/// <see cref="OpticsSettings.MultiConfiguration"/> table is a set of systems, configuration
/// <c>k</c> being the record with every operand row's value in column <c>k</c> substituted
/// ("OpticStudio uses a substitution procedure for defining multiple configurations", help:
/// Multiple Configuration Editor). docs/Optics-Conventions.md section 24.
/// <para>
/// <see cref="SettingsFor"/> is the pure entry point: the record of configuration <c>k</c>,
/// without the table, from which <see cref="ProjectLens"/> gives the solved lens
/// (<see cref="SolveFor"/>, <see cref="LensFor"/>). Rows are applied in table order, so a later
/// row wins where two control the same value (the help says so for IGNR and IGNM). Cells are
/// evaluated first: a pickup is target × scale + offset from a cell whose configuration and
/// operand numbers are each no greater than its own (help: Solve Types); a PUCN row makes the
/// fixed and variable cells below it, up to the next PUCN row, copy a previous configuration;
/// a thermal pickup is stored but not evaluated (OP-91) and fails by name. Every problem —
/// an unknown or unsupported operand, a missing surface, field or wavelength, a bad value —
/// is collected and thrown as one <see cref="ArgumentException"/>, one line each naming the
/// operand row, its code and the configuration.
/// </para>
/// </summary>
public static class MultiConfiguration
{
    // Cell solve names as saved (OpticsConfigValueSettings.Solve); null is Fixed.
    public const string Variable = "Variable";
    public const string Pickup = "Pickup";
    public const string ThermalPickup = "ThermalPickup";

    /// <summary>The number of configurations: 1 without a table.</summary>
    public static int ConfigurationCount(OpticsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.MultiConfiguration?.ConfigurationCount ?? 1;
    }

    /// <summary>The configuration analyses use (1-based): 1 without a table.</summary>
    public static int CurrentConfiguration(OpticsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.MultiConfiguration?.CurrentConfiguration ?? 1;
    }

    /// <summary>
    /// The optics record of configuration <paramref name="configuration"/> (1-based), with the
    /// table removed. Without a table only configuration 1 exists and the record is returned as
    /// it is. Throws <see cref="ArgumentException"/> listing every problem.
    /// </summary>
    public static OpticsSettings SettingsFor(OpticsSettings settings, int configuration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var table = settings.MultiConfiguration;
        if (table is null)
        {
            if (configuration != 1) throw new ArgumentException($"The system has one configuration; there is no configuration {configuration}.");
            return settings;
        }
        var problems = Validate(table).ToList();
        if (problems.Count == 0 && (configuration < 1 || configuration > table.ConfigurationCount))
            problems.Add($"There is no configuration {configuration}; the table has configurations 1 to {table.ConfigurationCount}.");
        if (problems.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, problems));

        var operands = table.Operands ?? new List<OpticsConfigOperandSettings>();
        var evaluated = Evaluate(table, configuration);
        var system = settings with { MultiConfiguration = null };
        for (int i = 0; i < operands.Count; i++)
        {
            var row = operands[i] ?? new OpticsConfigOperandSettings();
            string where = $"{Where(i, row)}, configuration {configuration}";
            var op = MultiConfigOperands.Find(row.Type);
            if (op is null)
            {
                problems.Add($"{where}: there is no multi-configuration operand '{row.Type}' (the help lists {MultiConfigOperands.All.Count}).");
                continue;
            }
            if (!op.IsSupported)
            {
                problems.Add($"{where}: the operand {op.Code} is not supported yet: {op.NotSupportedReason}");
                continue;
            }
            if (evaluated.Errors[i, configuration - 1] is { } error)
            {
                problems.Add($"{where}: {error}");
                continue;
            }
            try { system = op.Set(system, row.Number1, row.Number2, row.Number3, evaluated.Values[i, configuration - 1]); }
            catch (ArgumentException ex) { problems.Add($"{where}: {ex.Message}"); }
        }
        if (problems.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, problems));
        return system;
    }

    /// <summary>The record of the current configuration (<see cref="CurrentConfiguration"/>).</summary>
    public static OpticsSettings CurrentSettings(OpticsSettings settings) => SettingsFor(settings, CurrentConfiguration(settings));

    /// <summary>The lens of configuration <paramref name="configuration"/> as entered and solved
    /// (<see cref="ProjectLens.Solve"/>), or null when the record has no surfaces.</summary>
    public static ProjectLensSolution? SolveFor(OpticsSettings settings, int configuration, IReadOnlyList<OpticalCatalog> catalogs) =>
        ProjectLens.Solve(SettingsFor(settings, configuration), catalogs);

    /// <summary>The solved lens of configuration <paramref name="configuration"/>
    /// (<see cref="ProjectLens.From"/>), or null when the record has no surfaces; throws
    /// <see cref="ArgumentException"/> listing every problem.</summary>
    public static SequentialLens? LensFor(OpticsSettings settings, int configuration, IReadOnlyList<OpticalCatalog> catalogs) =>
        ProjectLens.From(SettingsFor(settings, configuration), catalogs);

    /// <summary>
    /// The analysis context of the current configuration: what every analysis computes from
    /// (<see cref="OpticsAnalysis{TSettings}"/> calls this), so analyses use the current
    /// configuration as the help says ("The program will use the current configuration for all
    /// calculations and graphics", help: Changing Configurations). A context without a table is
    /// returned as it is.
    /// </summary>
    public static OpticsAnalysisContext ForCurrentConfiguration(OpticsAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.System.MultiConfiguration is null ? context : context with { System = CurrentSettings(context.System) };
    }

    /// <summary>The weight of configuration <paramref name="configuration"/>: the value of the
    /// last CWGT row there ("The overall weight for the configuration. This number only has
    /// meaning relative to the weights in other configurations", help), 1 without one.</summary>
    public static double WeightOf(OpticsSettings settings, int configuration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var table = settings.MultiConfiguration;
        if (table?.Operands is not { Count: > 0 } operands) return 1;
        int row = operands.FindLastIndex(o => string.Equals(o?.Type, "CWGT", StringComparison.OrdinalIgnoreCase));
        if (row < 0) return 1;
        var problems = Validate(table).ToList();
        if (configuration < 1 || configuration > table.ConfigurationCount)
            problems.Add($"There is no configuration {configuration}; the table has configurations 1 to {table.ConfigurationCount}.");
        if (problems.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, problems));
        var evaluated = Evaluate(table, configuration);
        string where = $"{Where(row, operands[row])}, configuration {configuration}";
        if (evaluated.Errors[row, configuration - 1] is { } error) throw new ArgumentException($"{where}: {error}");
        double w = evaluated.Values[row, configuration - 1].Number;
        return double.IsFinite(w) && w >= 0 ? w : throw new ArgumentException($"{where}: a configuration weight is a finite number of 0 or more, not {w.ToString("R", CultureInfo.InvariantCulture)}.");
    }

    /// <summary>The evaluated value of every row in configuration <paramref name="configuration"/>
    /// (pickups and PUCN applied), with each failing cell's message instead of a value.</summary>
    public static IReadOnlyList<(MultiConfigValue Value, string? Error)> ValuesIn(OpticsMultiConfigSettings table, int configuration)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (Validate(table) is { Count: > 0 } problems) throw new ArgumentException(string.Join(Environment.NewLine, problems));
        if (configuration < 1 || configuration > table.ConfigurationCount)
            throw new ArgumentException($"There is no configuration {configuration}; the table has configurations 1 to {table.ConfigurationCount}.");
        var e = Evaluate(table, configuration);
        int rows = table.Operands?.Count ?? 0;
        return Enumerable.Range(0, rows).Select(i => (e.Values[i, configuration - 1], e.Errors[i, configuration - 1])).ToArray();
    }

    /// <summary>The cells flagged Variable, row by row (OP-54's optimizer varies them).</summary>
    public static IReadOnlyList<MultiConfigCell> Variables(OpticsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new List<MultiConfigCell>();
        var operands = settings.MultiConfiguration?.Operands;
        if (operands is null) return result;
        for (int i = 0; i < operands.Count; i++)
        {
            var values = operands[i]?.Values;
            if (values is null) continue;
            for (int k = 0; k < values.Count && k < settings.MultiConfiguration!.ConfigurationCount; k++)
                if (string.Equals(values[k]?.Solve, Variable, StringComparison.Ordinal)) result.Add(new MultiConfigCell(i + 1, k + 1));
        }
        return result;
    }

    /// <summary>The stored number of a cell.</summary>
    public static double ValueOf(OpticsSettings settings, MultiConfigCell cell)
    {
        var (_, value) = Cell(settings, cell);
        return value is null ? 0 : value.IsInfinite ? double.PositiveInfinity : value.Value;
    }

    /// <summary>The record with a cell's stored number replaced (its flag kept): how an
    /// optimizer writes a variable back.</summary>
    public static OpticsSettings WithValue(OpticsSettings settings, MultiConfigCell cell, double value)
    {
        var (table, old) = Cell(settings, cell);
        if (double.IsNaN(value) || double.IsNegativeInfinity(value)) throw new ArgumentException($"A cell takes a number or +infinity, not {value}.");
        var cellValue = (old ?? new OpticsConfigValueSettings()) with
        {
            Value = double.IsPositiveInfinity(value) ? 0 : value,
            IsInfinite = double.IsPositiveInfinity(value),
        };
        return settings with { MultiConfiguration = MultiConfigTools.WithCell(table, cell.Operand, cell.Configuration, cellValue) };
    }

    private static (OpticsMultiConfigSettings Table, OpticsConfigValueSettings? Value) Cell(OpticsSettings settings, MultiConfigCell cell)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var table = settings.MultiConfiguration ?? throw new ArgumentException("The system has no multi-configuration table.");
        int rows = table.Operands?.Count ?? 0;
        if (cell.Operand < 1 || cell.Operand > rows) throw new ArgumentException($"There is no operand {cell.Operand}; the table has rows 1 to {rows}.");
        if (cell.Configuration < 1 || cell.Configuration > table.ConfigurationCount)
            throw new ArgumentException($"There is no configuration {cell.Configuration}; the table has configurations 1 to {table.ConfigurationCount}.");
        var values = table.Operands![cell.Operand - 1]?.Values;
        return (table, values is not null && cell.Configuration - 1 < values.Count ? values[cell.Configuration - 1] : null);
    }

    /// <summary>What is wrong with the table's shape (not its values): the configuration count
    /// and the current configuration in range, no row with more values than configurations.</summary>
    public static IReadOnlyList<string> Validate(OpticsMultiConfigSettings table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var problems = new List<string>();
        if (table.ConfigurationCount < 1 || table.ConfigurationCount > MultiConfigOperands.MaxConfigurations)
            problems.Add($"The multi-configuration table has {table.ConfigurationCount} configurations; it takes 1 to {MultiConfigOperands.MaxConfigurations} (OpticStudio's limit).");
        else if (table.CurrentConfiguration < 1 || table.CurrentConfiguration > table.ConfigurationCount)
            problems.Add($"The current configuration is {table.CurrentConfiguration}; the table has configurations 1 to {table.ConfigurationCount}.");
        var operands = table.Operands ?? new List<OpticsConfigOperandSettings>();
        for (int i = 0; i < operands.Count; i++)
            if (operands[i]?.Values is { } v && v.Count > table.ConfigurationCount)
                problems.Add($"{Where(i, operands[i])}: it holds {v.Count} values but the table has {table.ConfigurationCount} configurations.");
        return problems;
    }

    /// <summary>"Operand 3 (THIC 5)": a row as messages name it.</summary>
    public static string Where(int index, OpticsConfigOperandSettings? row)
    {
        string code = string.IsNullOrWhiteSpace(row?.Type) ? MultiConfigOperands.Off : row!.Type!.Trim();
        var op = MultiConfigOperands.Find(code);
        int args = op?.Arguments.Count ?? 3;
        var numbers = new[] { row?.Number1 ?? 0, row?.Number2 ?? 0, row?.Number3 ?? 0 }.Take(args);
        string tail = string.Concat(numbers.Select(n => " " + n.ToString(CultureInfo.InvariantCulture)));
        return $"Multi-configuration operand {index + 1} ({code}{tail})";
    }

    // ------------------------------------------------------------------ evaluation

    private sealed record Evaluated(MultiConfigValue[,] Values, string?[,] Errors);

    /// <summary>Every cell of configurations 1 to <paramref name="through"/>, configuration by
    /// configuration and row by row: the order the pickup rule (no later configuration, no later
    /// row) makes sufficient.</summary>
    private static Evaluated Evaluate(OpticsMultiConfigSettings table, int through)
    {
        var operands = table.Operands ?? new List<OpticsConfigOperandSettings>();
        int rows = operands.Count, configs = table.ConfigurationCount;
        var values = new MultiConfigValue[rows, configs];
        var errors = new string?[rows, configs];
        for (int k = 1; k <= through; k++)
        {
            int pucn = 0;
            for (int i = 0; i < rows; i++)
            {
                var row = operands[i] ?? new OpticsConfigOperandSettings();
                bool text = MultiConfigOperands.Find(row.Type)?.Kind == MultiConfigValueKind.Text;
                var cell = row.Values is { } list && k - 1 < list.Count ? list[k - 1] ?? new() : new OpticsConfigValueSettings();
                bool isPucn = string.Equals(row.Type?.Trim(), "PUCN", StringComparison.OrdinalIgnoreCase);
                try
                {
                    string? solve = string.IsNullOrEmpty(cell.Solve) ? null : cell.Solve;
                    MultiConfigValue v;
                    if (solve is null or Variable)
                    {
                        v = pucn > 0 && !isPucn ? Source(i + 1, pucn) : Raw(cell, text);
                    }
                    else if (solve == Pickup)
                    {
                        int c = cell.PickupConfiguration, r = cell.PickupOperand;
                        if (c < 1 || c > configs || r < 1 || r > rows)
                            throw new ArgumentException($"the pickup names operand {r} in configuration {c}, which is not in the table (operands 1 to {rows}, configurations 1 to {configs}).");
                        // "the target configuration and operand number is less than or equal to the
                        // current configuration and operand number, respectively" (help: Solve Types).
                        if (c > k || r > i + 1 || (c == k && r == i + 1))
                            throw new ArgumentException($"the pickup names operand {r} in configuration {c}; a pickup reads a cell whose configuration and operand numbers are each no greater than its own ({i + 1}, {k}), and not itself.");
                        var source = Source(r, c);
                        if (text) v = source.IsText ? source : throw new ArgumentException($"a text operand cannot pick up the number {source} of operand {r}.");
                        else if (source.IsText) throw new ArgumentException($"a number cannot pick up the text '{source.Text}' of operand {r}.");
                        else v = MultiConfigValue.Of(source.Number * cell.PickupScale + cell.PickupOffset);
                    }
                    else if (solve == ThermalPickup)
                        throw new ArgumentException("thermal pickups are stored but not evaluated: thermal analysis is plan item OP-91, not built.");
                    else
                        throw new ArgumentException($"the cell's solve '{cell.Solve}' is not one this version knows (fixed, {Variable}, {Pickup}, {ThermalPickup}).");
                    values[i, k - 1] = v;
                    if (isPucn)
                    {
                        if (v.IsText || Math.Round(v.Number) != v.Number) throw new ArgumentException($"PUCN takes a configuration number, not {v}.");
                        int from = (int)v.Number;
                        if (from < 0)
                            throw new ArgumentException($"a negative PUCN ({from}) asks for a negative pickup, which the help does not define further; it is not built.");
                        // "All specified configuration numbers must be less than the configuration the PUCN data is provided for."
                        if (from >= k) throw new ArgumentException($"PUCN names configuration {from}; it must be less than {k}.");
                        pucn = from;
                    }
                }
                catch (ArgumentException ex)
                {
                    errors[i, k - 1] = ex.Message;
                    values[i, k - 1] = MultiConfigValue.Of(double.NaN);
                    if (isPucn) pucn = 0;
                }

                MultiConfigValue Source(int r, int c) =>
                    errors[r - 1, c - 1] is null ? values[r - 1, c - 1]
                    : throw new ArgumentException($"it takes its value from operand {r} in configuration {c}, which failed: {errors[r - 1, c - 1]}");
            }
        }
        return new Evaluated(values, errors);
    }

    private static MultiConfigValue Raw(OpticsConfigValueSettings cell, bool text) =>
        text ? MultiConfigValue.Of(cell.Text ?? "")
        : MultiConfigValue.Of(cell.IsInfinite ? double.PositiveInfinity : cell.Value);
}
