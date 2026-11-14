using OpenSim.Core.Model;

namespace OpenSim.Optics.MultiConfig;

/// <summary>A tool's result: the new table and notes on what it could not carry over.</summary>
public sealed record MultiConfigEdit(OpticsMultiConfigSettings Table, IReadOnlyList<string> Notes);

/// <summary>
/// The Multi-Configuration Editor's tools (OpticStudio 2025 R2 help, "Multi-Configuration Editor
/// Toolbar" and its pages, plans/sources/optics/op-30-help), as pure functions on the saved
/// table or the optics record. Configuration numbers are 1-based. Make Thermal (OP-91) and Make
/// Conjugate are not built; docs/Optics-Conventions.md section 24 says why.
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
    public static OpticsSettings MakeSingleConfiguration(OpticsSettings settings) => MultiConfiguration.CurrentSettings(settings);

    /// <summary>
    /// "Add all data currently in the system, broken down by the type of data" (help: Add All
    /// Data): one row of operand <paramref name="code"/> for each target the system holds data
    /// for (each surface's thickness, each field's y value, each surface with a tilt before …)
    /// that the table does not already control with that operand. Each new cell is fixed at the
    /// value the target has in that configuration, so no configuration changes. Creates the
    /// table when the record has none.
    /// </summary>
    public static OpticsSettings AddAllData(OpticsSettings settings, string code)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var op = MultiConfigOperands.Find(code) ?? throw new ArgumentException($"There is no multi-configuration operand '{code}'.");
        if (!op.IsSupported) throw new ArgumentException($"The multi-configuration operand {op.Code} is not supported yet: {op.NotSupportedReason}");
        if (!op.CanAddAllData) throw new ArgumentException($"Add All Data does not add {op.Code} rows: the operand has no target in the system data to read.");
        var table = TableOf(settings);
        var withTable = settings with { MultiConfiguration = table };
        int n = table.ConfigurationCount;
        var systems = Enumerable.Range(1, n).Select(k => MultiConfiguration.SettingsFor(withTable, k)).ToArray();
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
