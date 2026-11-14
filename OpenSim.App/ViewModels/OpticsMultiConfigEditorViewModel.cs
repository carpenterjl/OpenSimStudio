using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Core.Model;
using OpenSim.Optics.MultiConfig;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Multi-Configuration Editor (plan item OP-30; OpticStudio 2025 R2 help, "Multiple
/// Configuration Editor"): operand rows on OP-10's spreadsheet, the operand's numbers in the
/// three columns after the type and one column per configuration, saved as
/// <see cref="OpticsMultiConfigSettings"/> with the project and the lens file. Cells may be
/// fixed, variable, a pickup from another cell (the sheet's pickup; scale and offset) or a
/// thermal pickup (stored, evaluated by OP-91). After every change the table is applied to each
/// configuration (<see cref="MultiConfiguration.SettingsFor"/>) and every problem listed; a
/// pickup cell shows its evaluated value.
/// <para>
/// The sheet's columns depend on the number of configurations, so inserting or deleting a
/// configuration rebuilds it and clears the undo history (cell and row edits are undoable as in
/// the Lens Data Editor). The current configuration is what the analyses use; changing it
/// publishes the system. The Lens Data Editor keeps showing and editing the entered lens: a
/// value an operand controls is replaced in every configuration.
/// </para>
/// </summary>
public sealed partial class OpticsMultiConfigEditorViewModel : ObservableObject
{
    /// <summary>The slots of the operand's three numbers; configuration <c>k</c> is slot
    /// <see cref="FirstConfigurationSlot"/> + k − 1.</summary>
    public const int FirstConfigurationSlot = 3;

    /// <summary>The thermal pickup's solve code in the sheet.</summary>
    public const string ThermalPickupCode = MultiConfiguration.ThermalPickup;

    private readonly Action _publish;
    private readonly Func<OpticsSettings> _system;
    private readonly Action<OpticsSettings> _replaceSystem;
    private bool _applying;

    /// <param name="publish">Called after every change, to publish the system.</param>
    /// <param name="system">The optics record as entered (surfaces and system data, without the
    /// table) the configurations are applied to.</param>
    /// <param name="replaceSystem">Replaces the whole optics record (Make Single Configuration).</param>
    public OpticsMultiConfigEditorViewModel(Action publish, Func<OpticsSettings> system, Action<OpticsSettings> replaceSystem)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _replaceSystem = replaceSystem ?? throw new ArgumentNullException(nameof(replaceSystem));
        Rebuild(new OpticsMultiConfigSettings(), new List<string>());
    }

    /// <summary>The sheet the grid shows; replaced when the number of configurations changes.</summary>
    [ObservableProperty] private SheetDocument _document = null!;

    /// <summary>The number of configurations (columns).</summary>
    [ObservableProperty] private int _configurationCount = 1;

    /// <summary>The configuration the analyses use, 1-based.</summary>
    [ObservableProperty] private int _currentConfiguration = 1;

    /// <summary>The configuration of the grid's cursor (the view sets it): Insert and Delete
    /// Configuration act there, as the help says.</summary>
    [ObservableProperty] private int _cursorConfiguration = 1;

    /// <summary>The operand row of the grid's cursor (the view sets it), 0-based.</summary>
    [ObservableProperty] private int _currentRow;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<string> _problems = Array.Empty<string>();

    /// <summary>The operand Add All Data adds.</summary>
    [ObservableProperty] private string _addAllDataCode = "THIC";

    /// <summary>The configuration numbers, for the current-configuration picker.</summary>
    public IReadOnlyList<int> Configurations => Enumerable.Range(1, ConfigurationCount).ToArray();

    /// <summary>The operands Add All Data can add.</summary>
    public IReadOnlyList<string> AddAllDataCodes { get; } =
        MultiConfigOperands.Supported.Where(o => o.CanAddAllData).Select(o => o.Code).ToArray();

    public bool HasProblems => Problems.Count > 0;

    public string ProblemsText => Problems.Count == 0
        ? (ConfigurationCount == 1 && Document.RowCount == 0 ? "One configuration: the system as entered." : "Every configuration can be applied.")
        : string.Join(Environment.NewLine, Problems);

    /// <summary>"Configuration 2 of 3", for the panel.</summary>
    public string Summary => $"Configuration {CurrentConfiguration} of {ConfigurationCount}";

    partial void OnProblemsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ProblemsText));
    }

    partial void OnConfigurationCountChanged(int value)
    {
        OnPropertyChanged(nameof(Configurations));
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnCurrentConfigurationChanged(int value)
    {
        OnPropertyChanged(nameof(Summary));
        if (_applying) return;
        if (value < 1 || value > ConfigurationCount)
        {
            // A picker pushes a transient 0 while its list rebuilds.
            CurrentConfiguration = Math.Clamp(value, 1, ConfigurationCount);
            return;
        }
        AfterChange(publish: true);
    }

    // ------------------------------------------------------------------ files

    /// <summary>The table as the files store it; null for one configuration without rows (a
    /// single-configuration lens, written as files before OP-30 were).</summary>
    public OpticsMultiConfigSettings? ToSettings()
    {
        var table = ReadTable();
        return table.ConfigurationCount == 1 && (table.Operands?.Count ?? 0) == 0 ? null : table;
    }

    /// <summary>Loads a file's table; null (a single-configuration lens, or a file from before
    /// OP-30) empties the editor, since null means one configuration. Clears the undo history.</summary>
    public void LoadSettings(OpticsMultiConfigSettings? saved, ICollection<string> notes)
    {
        var table = saved ?? new OpticsMultiConfigSettings();
        if (MultiConfiguration.Validate(table) is { Count: > 0 } problems)
        {
            foreach (var p in problems) notes.Add($"{p} The editor kept its table.");
            return;
        }
        Rebuild(table, notes);
        AfterChange(publish: false);
    }

    /// <summary>A new project: one configuration, no rows.</summary>
    public void Reset()
    {
        Rebuild(new OpticsMultiConfigSettings(), new List<string>());
        AfterChange(publish: false);
    }

    /// <summary>Re-checks the configurations after a change of the entered system, without
    /// publishing (the caller publishes).</summary>
    public void Refresh() => AfterChange(publish: false);

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void InsertOperand() => Report(Document.InsertRows(Math.Clamp(CurrentRow, 0, Document.RowCount)), "Inserted an operand row.");

    [RelayCommand]
    private void DeleteOperand()
    {
        if (Document.RowCount == 0) { Status = "There is no operand row to delete."; return; }
        Report(Document.DeleteRows(new[] { Math.Clamp(CurrentRow, 0, Document.RowCount - 1) }), "Deleted an operand row.");
    }

    [RelayCommand] private void Undo() => Document.Undo();
    [RelayCommand] private void Redo() => Document.Redo();

    [RelayCommand]
    private void InsertConfiguration() => Tool(t => MultiConfigTools.InsertConfiguration(t, Math.Clamp(CursorConfiguration, 1, t.ConfigurationCount + 1)),
        $"Inserted configuration {Math.Clamp(CursorConfiguration, 1, ConfigurationCount + 1)} as a copy of the one at the cursor.");

    [RelayCommand]
    private void InsertConfigurationWithPickups()
    {
        int at = Math.Clamp(CursorConfiguration, 1, ConfigurationCount + 1);
        if (at == 1) at = 2;   // configuration 1 cannot pick itself up
        Tool(t => MultiConfigTools.InsertConfigurationWithPickups(t, at), $"Inserted configuration {at}, picking up configuration 1.");
    }

    [RelayCommand]
    private void DeleteConfiguration()
    {
        int at = Math.Clamp(CursorConfiguration, 1, ConfigurationCount);
        IReadOnlyList<string> notes = Array.Empty<string>();
        Tool(t =>
        {
            var edit = MultiConfigTools.DeleteConfiguration(t, at);
            notes = edit.Notes;
            return edit.Table;
        }, $"Deleted configuration {at}.");
        if (notes.Count > 0) Status += " " + string.Join(" ", notes);
    }

    [RelayCommand]
    private void NextConfiguration() => CurrentConfiguration = Math.Min(CurrentConfiguration + 1, ConfigurationCount);

    [RelayCommand]
    private void PreviousConfiguration() => CurrentConfiguration = Math.Max(CurrentConfiguration - 1, 1);

    /// <summary>Adds a row of <see cref="AddAllDataCode"/> for each target the system holds.</summary>
    [RelayCommand]
    private void AddAllData()
    {
        try
        {
            var before = ReadTable();
            var added = MultiConfigTools.AddAllData(_system() with { MultiConfiguration = before }, AddAllDataCode).MultiConfiguration!;
            int count = (added.Operands?.Count ?? 0) - (before.Operands?.Count ?? 0);
            Rebuild(added, new List<string>());
            AfterChange(publish: true);
            Status = count == 0 ? $"Every {AddAllDataCode} target is already in the table." : $"Added {count} {AddAllDataCode} row{(count == 1 ? "" : "s")}.";
        }
        catch (ArgumentException ex) { Status = ex.Message; }
    }

    /// <summary>Removes the table, leaving the system in the current configuration (help: Make
    /// Single Configuration).</summary>
    [RelayCommand]
    private void MakeSingleConfiguration()
    {
        OpticsSettings single;
        try { single = MultiConfigTools.MakeSingleConfiguration(_system() with { MultiConfiguration = ReadTable() }); }
        catch (ArgumentException ex) { Status = ex.Message; return; }
        int was = CurrentConfiguration;
        Rebuild(new OpticsMultiConfigSettings(), new List<string>());
        _replaceSystem(single);
        Status = $"The lens is now configuration {was} alone.";
    }

    private void Tool(Func<OpticsMultiConfigSettings, OpticsMultiConfigSettings> change, string done)
    {
        try
        {
            var table = change(ReadTable());
            Rebuild(table, new List<string>());
            AfterChange(publish: true);
            Status = done + " (The undo history was cleared: the columns changed.)";
        }
        catch (ArgumentException ex) { Status = ex.Message; }
    }

    private void Report(SheetEditResult result, string done) => Status = result.Succeeded ? done : result.Message ?? "";

    // ------------------------------------------------------------------ sheet

    /// <summary>The sheet's schema for <paramref name="configurations"/> configurations: one row
    /// type per operand of <see cref="MultiConfigOperands"/> (MOFF first, the default), and one
    /// for each code in <paramref name="otherCodes"/> this build does not know, so its data is
    /// shown and kept.</summary>
    public static SheetSchema BuildSchema(int configurations, IEnumerable<string> otherCodes)
    {
        var slots = new List<string> { "Number 1", "Number 2", "Number 3" };
        slots.AddRange(Enumerable.Range(1, configurations).Select(k => ConfigHeader(k)));
        var types = new List<SheetRowType>();
        var off = MultiConfigOperands.Find(MultiConfigOperands.Off)!;
        types.Add(RowType(off, configurations));
        foreach (var op in MultiConfigOperands.All.Where(o => o != off)) types.Add(RowType(op, configurations));
        foreach (var code in otherCodes.Distinct(StringComparer.OrdinalIgnoreCase))
            if (MultiConfigOperands.Find(code) is null)
                types.Add(new SheetRowType(code.Trim(), Columns(new[] { "Number 1", "Number 2", "Number 3" }, MultiConfigValueKind.Number, configurations, infinity: true))
                { Description = "An operand this build does not know: kept and saved, but applying the table fails on it." });
        return new SheetSchema("Operand", slots, types) { FirstRowNumber = 1 };
    }

    private static string ConfigHeader(int k) => "Config " + k.ToString(CultureInfo.InvariantCulture);

    private static SheetRowType RowType(MultiConfigOperand op, int configurations) =>
        new(op.Code, Columns(op.Arguments, op.Kind, configurations, infinity: op.Code == "THIC"))
        {
            Description = op.IsSupported ? op.Description : $"{op.Description}. Not supported yet: {op.NotSupportedReason}",
        };

    private static List<SheetColumn?> Columns(IReadOnlyList<string> arguments, MultiConfigValueKind kind, int configurations, bool infinity)
    {
        var columns = new List<SheetColumn?>();
        for (int i = 0; i < 3; i++)
            columns.Add(i < arguments.Count ? new SheetColumn("N" + (i + 1).ToString(CultureInfo.InvariantCulture), arguments[i]) { Kind = CellKind.Integer } : null);
        for (int k = 1; k <= configurations; k++)
            columns.Add(new SheetColumn("C" + k.ToString(CultureInfo.InvariantCulture), ConfigHeader(k))
            {
                Kind = kind switch { MultiConfigValueKind.Text => CellKind.Text, MultiConfigValueKind.Integer => CellKind.Integer, _ => CellKind.Number },
                Flags = kind == MultiConfigValueKind.Text ? CellFlagSupport.Pickup : CellFlagSupport.All,
                SolveCodes = kind == MultiConfigValueKind.Text ? Array.Empty<SheetSolveCode>() : ThermalSolves,
                AllowsInfinity = infinity,
                NumberFormat = "G10",
            });
        return columns;
    }

    /// <summary>The thermal pickup (help: Solve Types): from a reference configuration; stored,
    /// evaluated by OP-91. Its badge "T" is this program's.</summary>
    private static readonly SheetSolveCode[] ThermalSolves =
    {
        new(ThermalPickupCode, "Thermal pickup", "T") { ParameterNames = new[] { "Reference configuration" } },
    };

    private void Rebuild(OpticsMultiConfigSettings table, ICollection<string> notes)
    {
        int n = table.ConfigurationCount;
        var operands = table.Operands ?? new List<OpticsConfigOperandSettings>();
        if (Document is { } old) old.Changed -= OnSheetChanged;
        var doc = new SheetDocument(BuildSchema(n, operands.Select(o => o?.Type ?? "").Where(c => c.Trim().Length > 0)));
        _applying = true;
        try
        {
            if (operands.Count > 0) doc.InsertRows(0, operands.Count);
            for (int i = 0; i < operands.Count; i++) WriteRow(doc, i, operands[i] ?? new(), n, notes);
            for (int i = 0; i < operands.Count; i++) WriteFlags(doc, i, operands[i] ?? new(), n, notes);
            doc.History.Clear();
            Document = doc;
            ConfigurationCount = n;
            CurrentConfiguration = table.CurrentConfiguration;
            CursorConfiguration = Math.Clamp(CursorConfiguration, 1, n);
        }
        finally { _applying = false; }
        doc.Changed += OnSheetChanged;
    }

    private static void WriteRow(SheetDocument doc, int i, OpticsConfigOperandSettings row, int n, ICollection<string> notes)
    {
        string code = string.IsNullOrWhiteSpace(row.Type) ? MultiConfigOperands.Off : row.Type.Trim();
        var type = doc.Schema.FindRowType(code)!;
        Check(doc.SetRowType(i, type));
        var r = doc.Rows[i];
        string where = MultiConfiguration.Where(i, row);
        int[] numbers = { row.Number1, row.Number2, row.Number3 };
        for (int a = 0; a < 3; a++)
        {
            if (r.Cells[a].Definition is null)
            {
                if (numbers[a] != 0) notes.Add($"{where}: number {a + 1} ({numbers[a]}) is not used by {code}; it is dropped.");
                continue;
            }
            Set(a, CellValue.Of(numbers[a]));
        }
        for (int k = 1; k <= n; k++)
        {
            var cell = row.Values is { } list && k - 1 < list.Count ? list[k - 1] ?? new() : new OpticsConfigValueSettings();
            var definition = r.Cells[FirstConfigurationSlot + k - 1].Definition!;
            CellValue value = definition.Kind == CellKind.Text ? CellValue.Of(cell.Text ?? "")
                : CellValue.Of(cell.IsInfinite ? double.PositiveInfinity : cell.Value);
            if (definition.Kind != CellKind.Text && cell.Text is { Length: > 0 })
                notes.Add($"{where}, configuration {k}: the text '{cell.Text}' does not belong to {code}; it is dropped.");
            Set(FirstConfigurationSlot + k - 1, value);
        }

        void Set(int slot, CellValue value)
        {
            if (r.Cells[slot].Value == value) return;
            var result = doc.SetValue(i, slot + 1, value);
            if (!result.Succeeded) notes.Add($"{where}: {result.Message}");
        }
    }

    private static void WriteFlags(SheetDocument doc, int i, OpticsConfigOperandSettings row, int n, ICollection<string> notes)
    {
        string where = MultiConfiguration.Where(i, row);
        for (int k = 1; k <= n; k++)
        {
            var cell = row.Values is { } list && k - 1 < list.Count ? list[k - 1] : null;
            if (cell?.Solve is not { Length: > 0 } solve) continue;
            int column = FirstConfigurationSlot + k;
            CellFlag? flag = solve switch
            {
                MultiConfiguration.Variable => CellFlag.Variable,
                MultiConfiguration.ThermalPickup => CellFlag.Solve(ThermalPickupCode, cell.PickupConfiguration),
                MultiConfiguration.Pickup when cell.PickupOperand >= 1 && cell.PickupOperand <= doc.RowCount
                    && cell.PickupConfiguration >= 1 && cell.PickupConfiguration <= n =>
                    CellFlag.PickupFrom(doc.Rows[cell.PickupOperand - 1], FirstConfigurationSlot + cell.PickupConfiguration, cell.PickupScale, cell.PickupOffset),
                _ => null,
            };
            string? error = flag is null ? $"the cell's '{solve}' could not be read (an unknown solve, or a pickup from a cell not in the table)"
                : doc.ValidateFlag(i, column, flag);
            if (error is not null) { notes.Add($"{where}, configuration {k}: {error}; the cell is fixed."); continue; }
            Check(doc.SetFlag(i, column, flag!));
        }
    }

    private static void Check(SheetEditResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
    }

    /// <summary>The table the sheet holds.</summary>
    private OpticsMultiConfigSettings ReadTable()
    {
        var doc = Document;
        int n = ConfigurationCount;
        var operands = new List<OpticsConfigOperandSettings>();
        foreach (var r in doc.Rows)
        {
            int Number(int slot) => r.Cells[slot].Definition is not null && r.Cells[slot].Value.IsNumber ? (int)r.Cells[slot].Value.Number : 0;
            var values = new List<OpticsConfigValueSettings>();
            for (int k = 1; k <= n; k++)
            {
                var cell = r.Cells[FirstConfigurationSlot + k - 1];
                var v = cell.Value;
                var saved = cell.Definition!.Kind == CellKind.Text
                    ? new OpticsConfigValueSettings { Text = v.Text }
                    : double.IsPositiveInfinity(v.Number) ? new OpticsConfigValueSettings { IsInfinite = true }
                    : new OpticsConfigValueSettings { Value = v.IsNumber ? v.Number : 0 };
                var flag = cell.Flag;
                saved = flag.Kind switch
                {
                    CellFlagKind.Variable => saved with { Solve = MultiConfiguration.Variable },
                    CellFlagKind.Solve when flag.SolveCode == ThermalPickupCode => saved with
                    {
                        Solve = MultiConfiguration.ThermalPickup,
                        PickupConfiguration = flag.SolveParameters.Count > 0 ? (int)flag.SolveParameters[0] : 0,
                    },
                    CellFlagKind.Pickup when flag.Pickup is { } p && doc.FindRow(p.RowId) is { } source => saved with
                    {
                        Solve = MultiConfiguration.Pickup,
                        PickupOperand = source.Index + 1,
                        PickupConfiguration = p.Column - FirstConfigurationSlot,
                        PickupScale = p.Scale,
                        PickupOffset = p.Offset,
                    },
                    _ => saved,
                };
                values.Add(saved);
            }
            operands.Add(new OpticsConfigOperandSettings
            {
                Type = r.Type.Name, Number1 = Number(0), Number2 = Number(1), Number3 = Number(2), Values = values,
            });
        }
        return new OpticsMultiConfigSettings { ConfigurationCount = n, CurrentConfiguration = CurrentConfiguration, Operands = operands };
    }

    private void OnSheetChanged(object? sender, EventArgs e)
    {
        if (_applying) return;
        AfterChange(publish: true);
    }

    /// <summary>After any change: apply every configuration and list the problems, show each
    /// pickup's value and each failing cell's message, publish.</summary>
    private void AfterChange(bool publish)
    {
        var table = ReadTable();
        var problems = new List<string>();
        var doc = Document;
        foreach (var row in doc.Rows)
            foreach (var cell in row.Cells)
                if (cell.Note is not null) doc.SetNote(row.Index, cell.Column, null);
        if (table.ConfigurationCount > 1 || (table.Operands?.Count ?? 0) > 0)
        {
            var system = _system() with { MultiConfiguration = table };
            for (int k = 1; k <= table.ConfigurationCount; k++)
            {
                try { MultiConfiguration.SettingsFor(system, k); }
                catch (ArgumentException ex) { problems.AddRange(ex.Message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)); }
                ShowValues(table, k);
            }
        }
        Problems = problems;
        if (publish) _publish();
    }

    private void ShowValues(OpticsMultiConfigSettings table, int k)
    {
        var doc = Document;
        IReadOnlyList<(MultiConfigValue Value, string? Error)> values;
        try { values = MultiConfiguration.ValuesIn(table, k); }
        catch (ArgumentException) { return; }   // a table-shape problem, already listed by SettingsFor
        for (int i = 0; i < values.Count && i < doc.RowCount; i++)
        {
            int column = FirstConfigurationSlot + k;
            var cell = doc.Rows[i].Cells[column - 1];
            var (value, error) = values[i];
            if (error is not null) { doc.SetNote(i, column, error, isError: true); continue; }
            if (cell.Flag.Kind != CellFlagKind.Pickup) continue;
            var shown = value.IsText ? CellValue.Of(value.Text!) : CellValue.Of(value.Number);
            if (cell.Value != shown && cell.Definition is { } d && CellText.Validate(d, shown) is null) doc.SetComputed(i, column, shown);
        }
    }
}
