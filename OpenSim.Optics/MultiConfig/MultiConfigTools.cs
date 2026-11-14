using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.MultiConfig;

/// <summary>A tool's result: the new table and notes on what it could not carry over.</summary>
public sealed record MultiConfigEdit(OpticsMultiConfigSettings Table, IReadOnlyList<string> Notes);

// OP-91
/// <summary>A tool's result on the whole record (Make Thermal): the new record and notes on what
/// it left out.</summary>
public sealed record MultiConfigEditResult(OpticsSettings Settings, IReadOnlyList<string> Notes);

// OP-91
/// <summary>Make Thermal's dialog (OpticStudio 2025 R2 help, Make Thermal): "the number of
/// configurations, as well as the minimum and maximum temperatures", the option to keep the
/// existing configurations as nominal, and to sort by surface. <see cref="Pressure"/> is this
/// program's addition: null is the help's rule (the nominal pressure).</summary>
public sealed record MakeThermalOptions
{
    /// <summary>The number of new configurations per nominal configuration, 1 or more.</summary>
    public int Configurations { get; init; } = 3;

    /// <summary>The lowest new temperature, °C.</summary>
    public double MinimumTemperature { get; init; }

    /// <summary>The highest new temperature, °C.</summary>
    public double MaximumTemperature { get; init; } = 40;

    /// <summary>The new configurations' air pressure, atmospheres; null keeps the nominal's.</summary>
    public double? Pressure { get; init; }

    /// <summary>True: "Use existing n configurations as nominal"; false: "Delete existing
    /// configuration data".</summary>
    public bool KeepExisting { get; init; }

    /// <summary>Sort the new rows by surface rather than by operand type (the default).</summary>
    public bool SortBySurface { get; init; }
}

/// <summary>
/// The Multi-Configuration Editor's tools (OpticStudio 2025 R2 help, "Multi-Configuration Editor
/// Toolbar" and its pages, plans/sources/optics/op-30-help), as pure functions on the saved
/// table or the optics record. Configuration numbers are 1-based. Make Thermal is OP-91's
/// (<see cref="MakeThermal"/>, docs/Optics-Conventions.md section 37); Make Conjugate is not
/// built (section 24 says why).
/// </summary>
public static class MultiConfigTools
{
    /// <summary>The table of a record, or a new one-configuration table without rows.</summary>
    public static OpticsMultiConfigSettings TableOf(OpticsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.MultiConfiguration ?? new OpticsMultiConfigSettings { ConfigurationCount = 1, CurrentConfiguration = 1, Operands = new() };
    }

    /// <summary>
    /// "Insert a new configuration at the configuration indicated by the current position of the
    /// cursor" (help: Insert Configuration): the new column becomes configuration
    /// <paramref name="at"/> (1 … count + 1) and the later ones move up by one. The help does not
    /// say what the new column holds; here it copies the stored values of the configuration that
    /// was at <paramref name="at"/> (the last one when appending) as fixed values. Pickups,
    /// thermal-pickup references, PUCN values and the current configuration that name a moved
    /// configuration follow it.
    /// </summary>
    public static OpticsMultiConfigSettings InsertConfiguration(OpticsMultiConfigSettings table, int at) =>
        Insert(table, at, (row, i, source) => source is null ? new OpticsConfigValueSettings()
            : new OpticsConfigValueSettings { Value = source.Value, IsInfinite = source.IsInfinite, Text = source.Text });

    /// <summary>
    /// "Insert a new configuration ... with pickup solves inserted to pick up values from the first
    /// configuration" (help: Insert Configuration with Pickups): as <see cref="InsertConfiguration"/>,
    /// but every cell of the new column picks up the same row of configuration 1 (scale 1, offset
    /// 0). Not at position 1, where the new column would be configuration 1 itself.
    /// </summary>
    public static OpticsMultiConfigSettings InsertConfigurationWithPickups(OpticsMultiConfigSettings table, int at)
    {
        if (at == 1) throw new ArgumentException("A configuration that picks up configuration 1 cannot be inserted as configuration 1.");
        return Insert(table, at, (row, i, _) =>
        {
            var first = row.Values is { Count: > 0 } v ? v[0] ?? new() : new OpticsConfigValueSettings();
            return new OpticsConfigValueSettings
            {
                Value = first.Value, IsInfinite = first.IsInfinite, Text = first.Text,
                Solve = MultiConfiguration.Pickup, PickupConfiguration = 1, PickupOperand = i + 1, PickupScale = 1, PickupOffset = 0,
            };
        });
    }

    private static OpticsMultiConfigSettings Insert(OpticsMultiConfigSettings table, int at,
        Func<OpticsConfigOperandSettings, int, OpticsConfigValueSettings?, OpticsConfigValueSettings> make)
    {
        ArgumentNullException.ThrowIfNull(table);
        int n = table.ConfigurationCount;
        if (n >= MultiConfigOperands.MaxConfigurations)
            throw new ArgumentException($"The table has {n} configurations, OpticStudio's limit (help: \"This version supports {MultiConfigOperands.MaxConfigurations} configurations\").");
        if (at < 1 || at > n + 1) throw new ArgumentException($"A configuration can be inserted at 1 to {n + 1}, not {at}.");
        int from = Math.Min(at, n);
        var operands = (table.Operands ?? new()).Select((row, i) =>
        {
            row ??= new OpticsConfigOperandSettings();
            var values = Padded(row.Values, n).Select(c => Shifted(row, c, at, +1)).ToList();
            var source = row.Values is { } list && from - 1 < list.Count ? list[from - 1] : null;
            values.Insert(at - 1, make(row, i, source));
            return row with { Values = values };
        }).ToList();
        return table with
        {
            ConfigurationCount = n + 1,
            CurrentConfiguration = table.CurrentConfiguration >= at ? table.CurrentConfiguration + 1 : table.CurrentConfiguration,
            Operands = operands,
        };
    }

    /// <summary>
    /// "Delete the current configuration ... This eliminates the column and all the data it
    /// contained" (help: Delete Configuration). A pickup from the deleted configuration becomes a
    /// fixed cell holding its stored value, and a PUCN naming it becomes 0; each is listed in the
    /// notes. Later configurations move down by one. The last configuration cannot be deleted.
    /// </summary>
    public static MultiConfigEdit DeleteConfiguration(OpticsMultiConfigSettings table, int at)
    {
        ArgumentNullException.ThrowIfNull(table);
        int n = table.ConfigurationCount;
        if (n <= 1) throw new ArgumentException("The table has one configuration; it cannot be deleted (Make Single Configuration removes the table).");
        if (at < 1 || at > n) throw new ArgumentException($"There is no configuration {at}; the table has configurations 1 to {n}.");
        var notes = new List<string>();
        var operands = (table.Operands ?? new()).Select((row, i) =>
        {
            row ??= new OpticsConfigOperandSettings();
            var values = Padded(row.Values, n);
            values.RemoveAt(at - 1);
            for (int k = 0; k < values.Count; k++)
            {
                var c = values[k];
                int config = k < at - 1 ? k + 1 : k + 2;   // its number before the deletion
                if (IsReference(c) && c.PickupConfiguration == at)
                {
                    notes.Add($"{MultiConfiguration.Where(i, row)}, configuration {config}: its {(c.Solve == MultiConfiguration.Pickup ? "pickup" : "thermal pickup")} named the deleted configuration {at}; it is now fixed at its stored value.");
                    values[k] = c with { Solve = null, PickupConfiguration = 0, PickupOperand = 0, PickupScale = 1, PickupOffset = 0 };
                }
                else if (IsPucn(row) && c.Value == at && c.Solve is null or MultiConfiguration.Variable)
                {
                    notes.Add($"{MultiConfiguration.Where(i, row)}, configuration {config}: it named the deleted configuration {at}; it is now 0 (no pickup).");
                    values[k] = c with { Value = 0 };
                }
                else values[k] = Shifted(row, c, at + 1, -1);
            }
            return row with { Values = values };
        }).ToList();
        int current = table.CurrentConfiguration;
        current = current > at ? current - 1 : current == at ? Math.Min(at, n - 1) : current;
        return new MultiConfigEdit(table with { ConfigurationCount = n - 1, CurrentConfiguration = current, Operands = operands }, notes);
    }

    /// <summary>"Delete all the multi-configuration data, leaving the lens as a single
    /// configuration system in whatever configuration is active at the time this tool is used"
    /// (help: Make Single Configuration).</summary>
    public static OpticsSettings MakeSingleConfiguration(OpticsSettings settings, IReadOnlyList<OpticalCatalog>? catalogs = null) =>
        MultiConfiguration.CurrentSettings(settings, catalogs);

    /// <summary>
    /// "Add all data currently in the system, broken down by the type of data" (help: Add All
    /// Data): one row of operand <paramref name="code"/> for each target the system holds data
    /// for (each surface's thickness, each field's y value, each surface with a tilt before …)
    /// that the table does not already control with that operand. Each new cell is fixed at the
    /// value the target has in that configuration, so no configuration changes. Creates the
    /// table when the record has none.
    /// </summary>
    public static OpticsSettings AddAllData(OpticsSettings settings, string code, IReadOnlyList<OpticalCatalog>? catalogs = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var op = MultiConfigOperands.Find(code) ?? throw new ArgumentException($"There is no multi-configuration operand '{code}'.");
        if (!op.IsSupported) throw new ArgumentException($"The multi-configuration operand {op.Code} is not supported yet: {op.NotSupportedReason}");
        if (!op.CanAddAllData) throw new ArgumentException($"Add All Data does not add {op.Code} rows: the operand has no target in the system data to read.");
        var table = TableOf(settings);
        var withTable = settings with { MultiConfiguration = table };
        int n = table.ConfigurationCount;
        var systems = Enumerable.Range(1, n).Select(k => MultiConfiguration.SettingsFor(withTable, k, catalogs)).ToArray();
        var operands = (table.Operands ?? new()).ToList();
        var present = operands.Where(o => string.Equals(o?.Type?.Trim(), op.Code, StringComparison.OrdinalIgnoreCase))
            .Select(o => (o.Number1, o.Number2, o.Number3)).ToHashSet();
        var targets = systems.SelectMany(s => op.Targets!(s)).Distinct().ToList();
        foreach (var (a, b, c) in targets)
        {
            if (!present.Add((a, b, c))) continue;
            var values = new List<OpticsConfigValueSettings>();
            for (int k = 0; k < n; k++)
            {
                MultiConfigValue v;
                try { v = op.Get(systems[k], a, b, c); }
                catch (ArgumentException ex) { throw new ArgumentException($"Add All Data ({op.Code} {a} {b} {c}), configuration {k + 1}: {ex.Message}", ex); }
                values.Add(v.IsText ? new OpticsConfigValueSettings { Text = v.Text }
                    : double.IsPositiveInfinity(v.Number) ? new OpticsConfigValueSettings { IsInfinite = true }
                    : new OpticsConfigValueSettings { Value = v.Number });
            }
            operands.Add(new OpticsConfigOperandSettings { Type = op.Code, Number1 = a, Number2 = b, Number3 = c, Values = values });
        }
        return settings with { MultiConfiguration = table with { Operands = operands } };
    }

    /// <summary>The table with configuration <paramref name="configuration"/> current.</summary>
    public static OpticsMultiConfigSettings SetCurrent(OpticsMultiConfigSettings table, int configuration)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (configuration < 1 || configuration > table.ConfigurationCount)
            throw new ArgumentException($"There is no configuration {configuration}; the table has configurations 1 to {table.ConfigurationCount}.");
        return table with { CurrentConfiguration = configuration };
    }

    /// <summary>Next configuration (help: Multiple Configuration Editor, "the previous/next
    /// configuration arrows"); stays on the last one.</summary>
    public static OpticsMultiConfigSettings Next(OpticsMultiConfigSettings table) =>
        SetCurrent(table, Math.Min(table.CurrentConfiguration + 1, table.ConfigurationCount));

    /// <summary>Previous configuration; stays on the first one.</summary>
    public static OpticsMultiConfigSettings Previous(OpticsMultiConfigSettings table) =>
        SetCurrent(table, Math.Max(table.CurrentConfiguration - 1, 1));

    /// <summary>The table with one cell replaced (rows and values padded as needed).</summary>
    public static OpticsMultiConfigSettings WithCell(OpticsMultiConfigSettings table, int operand, int configuration, OpticsConfigValueSettings value)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(value);
        var operands = (table.Operands ?? new()).ToList();
        if (operand < 1 || operand > operands.Count) throw new ArgumentException($"There is no operand {operand}; the table has rows 1 to {operands.Count}.");
        if (configuration < 1 || configuration > table.ConfigurationCount)
            throw new ArgumentException($"There is no configuration {configuration}; the table has configurations 1 to {table.ConfigurationCount}.");
        var row = operands[operand - 1] ?? new OpticsConfigOperandSettings();
        var values = Padded(row.Values, Math.Max(configuration, row.Values?.Count ?? 0));
        values[configuration - 1] = value;
        operands[operand - 1] = row with { Values = values };
        return table with { Operands = operands };
    }

    // ------------------------------------------------------------------ OP-91: Make Thermal

    /// <summary>The operands whose cells Make Thermal gives thermal pickups, in its row order.</summary>
    public static IReadOnlyList<string> ThermalLengthCodes { get; } = new[] { "CRVT", "THIC", "SDIA", "MCSD" };

    /// <summary>
    /// "Set up multiple configurations of the design at different temperatures" (OpticStudio 2025
    /// R2 help, Make Thermal, plans/sources/optics/op-30-help/Make_Thermal.txt).
    /// <list type="bullet">
    /// <item><b>Delete existing configuration data</b> (<see cref="MakeThermalOptions.KeepExisting"/>
    /// false): "1 nominal configuration at the current temperature and pressure", then
    /// <see cref="MakeThermalOptions.Configurations"/> configurations "that span the specified
    /// temperature range in equal increments"; the nominal is the current configuration's
    /// system.</item>
    /// <item><b>Use existing n configurations as nominal</b>: "the total number of configurations
    /// will be determined by the number of nominal configurations times the number of new
    /// configurations, plus the original nominal configurations"; nominal c's new configurations
    /// follow the originals, c by c. Existing rows' new cells pick up nominal c (a thermal pickup
    /// for the lengths), TEMP takes the new temperatures.</item>
    /// </list>
    /// For each radius, thickness, clear and mechanical semi-diameter of surfaces 1 to the one
    /// before the image that holds a fixed value, a CRVT, THIC, SDIA or MCSD row is added (when
    /// the table does not already control it) with the nominal value in the nominal
    /// configurations and a thermal pickup of the nominal in the others; TEMP and PRES rows hold
    /// the temperatures and the pressure ("The air pressure will be assumed to be the same as the
    /// nominal pressure", unless <see cref="MakeThermalOptions.Pressure"/> gives another). Rows
    /// are sorted by operand type, or by surface. A value under a Lens Data Editor solve or
    /// pickup, a plane's curvature, an automatic semi-diameter, an infinite thickness, glasses,
    /// parameters and extra data get no row; the notes say which. Throws
    /// <see cref="ArgumentException"/> naming the problem.
    /// </summary>
    public static MultiConfigEditResult MakeThermal(OpticsSettings settings, MakeThermalOptions options, IReadOnlyList<OpticalCatalog>? catalogs = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        var temperatures = ThermalTemperatures(options);
        if (options.Pressure is double pr && !(double.IsFinite(pr) && pr >= 0))
            throw new ArgumentException($"Make Thermal: the pressure must be zero (a vacuum) or more atmospheres, not {N(pr)}.");
        var notes = new List<string>();
        int newPer = temperatures.Count;

        OpticsSettings nominalBase;
        OpticsMultiConfigSettings table;
        OpticsSettings[] nominals;
        if (!options.KeepExisting)
        {
            nominalBase = settings.MultiConfiguration is null ? settings : MultiConfiguration.CurrentSettings(settings, catalogs);
            nominals = new[] { nominalBase };
            table = new OpticsMultiConfigSettings { ConfigurationCount = 1, CurrentConfiguration = 1, Operands = new() };
        }
        else
        {
            nominalBase = settings with { MultiConfiguration = null };
            table = TableOf(settings);
            var withTable = settings with { MultiConfiguration = table };
            nominals = Enumerable.Range(1, table.ConfigurationCount).Select(c => MultiConfiguration.SettingsFor(withTable, c, catalogs)).ToArray();
        }
        int n0 = table.ConfigurationCount;
        int total = n0 + n0 * newPer;
        if (total > MultiConfigOperands.MaxConfigurations)
            throw new ArgumentException($"Make Thermal would make {total} configurations ({n0} nominal + {n0} × {newPer}); OpticStudio's limit is {MultiConfigOperands.MaxConfigurations}.");
        var existing = (table.Operands ?? new()).Select(o => o ?? new OpticsConfigOperandSettings()).ToList();
        int temps = existing.Count(o => Is(o, "TEMP")), presses = existing.Count(o => Is(o, "PRES"));
        if (temps > 1 || presses > 1)
            throw new ArgumentException("Make Thermal: the table has more than one TEMP or PRES row (several environments in one configuration), which thermal pickups do not support; remove the extra rows first.");

        // Nominal c's j-th new configuration (both 1-based) is n0 + (c − 1)·newPer + j: the cells
        // are appended in that order below. Existing rows: new cells copy (pick up) their nominal; lengths expand; TEMP and PRES are set.
        var rows = new List<OpticsConfigOperandSettings>();
        for (int i = 0; i < existing.Count; i++)
        {
            var row = existing[i];
            var values = Padded(row.Values, n0);
            var op = MultiConfigOperands.Find(row.Type);
            for (int c = 1; c <= n0; c++)
                for (int j = 1; j <= newPer; j++)
                {
                    var source = values[c - 1];
                    OpticsConfigValueSettings cell;
                    if (Is(row, "TEMP")) cell = new OpticsConfigValueSettings { Value = temperatures[j - 1] };
                    else if (Is(row, "PRES") && options.Pressure is double p) cell = new OpticsConfigValueSettings { Value = p };
                    else if (op is not null && ThermalLengthCodes.Contains(op.Code))
                        cell = new OpticsConfigValueSettings { Value = source.Value, IsInfinite = source.IsInfinite, Solve = MultiConfiguration.ThermalPickup, PickupConfiguration = c };
                    else
                        cell = new OpticsConfigValueSettings
                        {
                            Value = source.Value, IsInfinite = source.IsInfinite, Text = source.Text,
                            Solve = MultiConfiguration.Pickup, PickupConfiguration = c, PickupOperand = i + 1, PickupScale = 1, PickupOffset = 0,
                        };
                    values.Add(cell);
                }
            rows.Add(row with { Values = values });
        }

        // New rows: TEMP and PRES when missing, then the lengths not yet in the table.
        var added = new List<OpticsConfigOperandSettings>();
        if (temps == 0) added.Add(EnvironmentRow("TEMP", s => s.TemperatureCelsius, j => temperatures[j - 1]));
        if (presses == 0) added.Add(EnvironmentRow("PRES", s => s.PressureAtmospheres, j => options.Pressure));
        var present = existing.Select(o => ((o.Type ?? "").Trim().ToUpperInvariant(), o.Number1)).ToHashSet();
        var lengths = new List<(string Code, int Surface)>();
        int surfaces = nominals.Max(s => s.Surfaces?.Count ?? 0);
        for (int surface = 1; surface <= surfaces - 2; surface++)
            foreach (string code in ThermalLengthCodes)
            {
                if (present.Contains((code, surface))) continue;
                string? why = null;
                foreach (var nominal in nominals)
                    why ??= WhyNoThermalRow(nominal, code, surface);
                if (why is not null)
                {
                    if (why.Length > 0) notes.Add($"{code} {surface}: no row, {why}.");
                    continue;
                }
                lengths.Add((code, surface));
            }
        var ordered = options.SortBySurface
            ? lengths.OrderBy(l => l.Surface).ThenBy(l => Array.IndexOf(ThermalLengthCodes.ToArray(), l.Code))
            : lengths.OrderBy(l => Array.IndexOf(ThermalLengthCodes.ToArray(), l.Code)).ThenBy(l => l.Surface);
        foreach (var (code, surface) in ordered)
        {
            var op = MultiConfigOperands.Find(code)!;
            var cells = new List<OpticsConfigValueSettings>();
            var nominalValues = nominals.Select(nominal => op.Get(nominal, surface).Number).ToArray();
            cells.AddRange(nominalValues.Select(v => new OpticsConfigValueSettings { Value = v }));
            for (int c = 1; c <= n0; c++)
                for (int j = 1; j <= newPer; j++)
                    cells.Add(new OpticsConfigValueSettings { Value = nominalValues[c - 1], Solve = MultiConfiguration.ThermalPickup, PickupConfiguration = c });
            added.Add(new OpticsConfigOperandSettings { Type = code, Number1 = surface, Values = cells });
        }

        // A fresh table puts TEMP and PRES first (OpticStudio's "the last TEMP ... define[s] the
        // global environment" holds for one TEMP row wherever it is); a kept table gets the new rows
        // after its own, so its pickups' operand numbers still hold.
        rows.AddRange(added);
        if (!(nominalBase.SystemData?.AdjustIndexToEnvironment ?? false))
            notes.Add("Adjust index to environment is off in the system data, so the glass indices do not follow the temperatures; turn it on for the thermal index model.");
        if (nominals.Any(s => s.Surfaces?.Any(x => x?.Parameters is { Count: > 0 } || x?.ExtraData is { Count: > 0 }) == true))
            notes.Add("Parameters and extra data get no rows: a thermal pickup of a parameter is not built (its units are not declared by the surface types).");
        var result = new OpticsMultiConfigSettings
        {
            ConfigurationCount = total,
            CurrentConfiguration = options.KeepExisting ? table.CurrentConfiguration : 1,
            Operands = rows,
        };
        return new MultiConfigEditResult(nominalBase with { MultiConfiguration = result }, notes);

        OpticsConfigOperandSettings EnvironmentRow(string code, Func<OpticsSystemSettings, double> nominalValue, Func<int, double?> newValue)
        {
            var nominalValues = nominals.Select(s => nominalValue(s.SystemData ?? new OpticsSystemSettings())).ToArray();
            var cells = nominalValues.Select(v => new OpticsConfigValueSettings { Value = v }).ToList();
            for (int c = 1; c <= n0; c++)
                for (int j = 1; j <= newPer; j++)
                    cells.Add(new OpticsConfigValueSettings { Value = newValue(j) ?? nominalValues[c - 1] });
            return new OpticsConfigOperandSettings { Type = code, Values = cells };
        }
    }

    /// <summary>The new configurations' temperatures: <see cref="MakeThermalOptions.Configurations"/>
    /// values from the minimum to the maximum in equal steps (one configuration: the minimum).</summary>
    public static IReadOnlyList<double> ThermalTemperatures(MakeThermalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int n = options.Configurations;
        double lo = options.MinimumTemperature, hi = options.MaximumTemperature;
        if (n < 1) throw new ArgumentException($"Make Thermal adds at least one configuration, not {n}.");
        // −273.15 °C is 0 K by the definition of the Celsius scale.
        if (!double.IsFinite(lo) || !double.IsFinite(hi) || lo <= -273.15 || hi <= -273.15)
            throw new ArgumentException($"Make Thermal: the temperatures must be above absolute zero, −273.15 °C (they are {N(lo)} and {N(hi)} °C).");
        if (hi < lo) throw new ArgumentException($"Make Thermal: the maximum temperature ({N(hi)} °C) is below the minimum ({N(lo)} °C).");
        if (n == 1) return new[] { lo };
        return Enumerable.Range(0, n).Select(j => j == n - 1 ? hi : lo + (hi - lo) * j / (n - 1)).ToArray();
    }

    /// <summary>Why a surface's value gets no thermal row: null when it gets one, "" when there
    /// is nothing to expand (silently), else the reason for the notes.</summary>
    private static string? WhyNoThermalRow(OpticsSettings nominal, string code, int surface)
    {
        var list = nominal.Surfaces;
        if (list is null || surface >= list.Count - 1) return "";
        var s = list[surface] ?? new OpticsSurfaceSettings();
        if (string.Equals(s.Type, "Coordinate Break", StringComparison.OrdinalIgnoreCase)) return "";
        string column = code switch
        {
            "CRVT" => SavedSurfaces.RadiusColumn,
            "THIC" => SavedSurfaces.ThicknessColumn,
            "SDIA" => SavedSurfaces.SemiDiameterColumn,
            _ => SavedSurfaces.MechanicalSemiDiameterColumn,
        };
        switch (code)
        {
            case "CRVT" when SavedSurfaces.IsPlane(s): return "";
            case "THIC" when s.ThicknessIsInfinite: return "";
            case "SDIA" when s.SemiDiameter is null: return "";
            case "MCSD" when s.MechanicalSemiDiameter is null: return "";
        }
        if (s.Flags?.Any(f => f?.Column == column && f.Kind is SavedSurfaces.SolveFlag or SavedSurfaces.PickupFlag) == true)
            return "the Lens Data Editor has a solve or pickup on it, which a table row would replace";
        return null;
    }

    private static bool Is(OpticsConfigOperandSettings row, string code) => string.Equals(row.Type?.Trim(), code, StringComparison.OrdinalIgnoreCase);

    private static string N(double v) => v.ToString("G10", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ helpers

    private static List<OpticsConfigValueSettings> Padded(List<OpticsConfigValueSettings>? values, int count)
    {
        var list = (values ?? new()).Select(v => v ?? new OpticsConfigValueSettings()).ToList();
        while (list.Count < count) list.Add(new OpticsConfigValueSettings());
        return list;
    }

    private static bool IsReference(OpticsConfigValueSettings c) =>
        c.Solve is MultiConfiguration.Pickup or MultiConfiguration.ThermalPickup;

    private static bool IsPucn(OpticsConfigOperandSettings row) =>
        string.Equals(row.Type?.Trim(), "PUCN", StringComparison.OrdinalIgnoreCase);

    /// <summary>A cell whose configuration references (pickup source, thermal reference, a
    /// PUCN's fixed value) at or past <paramref name="from"/> move by <paramref name="by"/>.</summary>
    private static OpticsConfigValueSettings Shifted(OpticsConfigOperandSettings row, OpticsConfigValueSettings c, int from, int by)
    {
        if (IsReference(c) && c.PickupConfiguration >= from) c = c with { PickupConfiguration = c.PickupConfiguration + by };
        if (IsPucn(row) && c.Solve is null or MultiConfiguration.Variable && c.Value >= from && Math.Round(c.Value) == c.Value)
            c = c with { Value = c.Value + by };
        return c;
    }
}
