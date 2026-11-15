using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Tolerancing;
using S = OpenSim.App.ViewModels.OpticsToleranceEditorSchema;

namespace OpenSim.App.ViewModels;

/// <summary>A choice of the irregularity model, for the editor's picker.</summary>
public sealed record IrregularityModelOption(IrregularityModel Model, string Label)
{
    public static IReadOnlyList<IrregularityModelOption> All { get; } = new[]
    {
        new IrregularityModelOption(IrregularityModel.PowerRemoved, "Peak-to-valley with power removed (ISO 10110)"),
        new IrregularityModelOption(IrregularityModel.OpticStudio, "OpticStudio's TIRR formula"),
    };

    public override string ToString() => Label;
}

/// <summary>
/// The Tolerance Data Editor (plan item OP-63): the tolerance and control rows on OP-10's
/// spreadsheet (<see cref="SheetDocument"/>, schema <see cref="OpticsToleranceEditorSchema"/>),
/// saved as <see cref="OpticsToleranceSettings"/> with the project and the lens file. Update is
/// OpticStudio's Validate (help page "Tolerance Data Editor Toolbar": "Checks all tolerance
/// operands for proper surface ranges and other common errors") and fills the Nominal column;
/// Loosen 2x and Tighten 2x scale every tolerance's Min and Max as the toolbar's buttons do. Every
/// edit lands on the sheet's undo stack and publishes the system. The tolerance run (OP-64:
/// sensitivity, inverse limit and increment, Quick Sensitivity) is in the partial file
/// OpticsToleranceEditorViewModel.Run.cs; the Monte Carlo run (OP-65) in OpticsToleranceEditorViewModel.MonteCarlo.cs.
/// </summary>
public sealed partial class OpticsToleranceEditorViewModel : ObservableObject
{
    private readonly Action _publish;
    private readonly Func<OpticsSettings> _lens;
    private readonly Func<IReadOnlyList<OpticalCatalog>> _catalogs;   // OP-64
    private readonly Dictionary<string, ToleranceOperandDefinition> _byTypeName = new(StringComparer.Ordinal);
    private readonly SheetDocument _document;
    private bool _applying;

    /// <param name="publish">Called after every edit, to publish the system.</param>
    /// <param name="lens">The project's optics record, for Update and the tolerance run.</param>
    /// <param name="catalogs">The active glass catalogs, for the tolerance run (OP-64); null for none.</param>
    public OpticsToleranceEditorViewModel(Action publish, Func<OpticsSettings> lens, Func<IReadOnlyList<OpticalCatalog>>? catalogs = null)
    {
        _catalogs = catalogs ?? (() => Array.Empty<OpticalCatalog>());
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _lens = lens ?? throw new ArgumentNullException(nameof(lens));
        foreach (var d in ToleranceOperandRegistry.All) _byTypeName[S.TypeName(d)] = d;
        _document = new SheetDocument(S.Build());
        _document.Changed += OnSheetChanged;
        _applying = true;
        InitializeRun();   // OP-64
        InitializeMonteCarlo();   // OP-65
        _applying = false;
    }

    /// <summary>The sheet the grid shows.</summary>
    public SheetDocument Document => _document;

    [ObservableProperty] private int _currentRow;

    [ObservableProperty] private string _status = "";

    [ObservableProperty] private IReadOnlyList<string> _problems = Array.Empty<string>();

    [ObservableProperty] private string _findText = "";

    /// <summary>The irregularity model the TIRR rows use (docs/Optics-Conventions.md §36).</summary>
    [ObservableProperty] private IrregularityModelOption _selectedIrregularityModel = IrregularityModelOption.All[0];

    public IReadOnlyList<IrregularityModelOption> IrregularityModels => IrregularityModelOption.All;

    partial void OnSelectedIrregularityModelChanged(IrregularityModelOption value)
    {
        if (value is null) { SelectedIrregularityModel = IrregularityModelOption.All[0]; return; }
        if (!_applying) _publish();
    }

    public bool HasProblems => Problems.Count > 0;

    public string ProblemsText => string.Join(Environment.NewLine, Problems);

    partial void OnProblemsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ProblemsText));
    }

    /// <summary>The operands matching <see cref="FindText"/>, one line each.</summary>
    public IReadOnlyList<string> FindResults => FindText.Trim().Length == 0 ? Array.Empty<string>()
        : ToleranceOperandRegistry.Search(FindText).Select(d => $"{d.Code}  {d.Name}: {d.Description}").ToArray();

    partial void OnFindTextChanged(string value) => OnPropertyChanged(nameof(FindResults));

    public int OperandCount => Document.RowCount;

    // ------------------------------------------------------------------ model

    public ToleranceOperandDefinition DefinitionOf(SheetRow row) => _byTypeName[row.Type.Name];

    /// <summary>The tolerance table the sheet holds.</summary>
    public ToleranceTable ToTable() => new(Document.Rows.Select(row =>
    {
        int Int(string key) => row.ValueOf(key) is { IsNumber: true } v ? (int)v.Number : 0;
        double Num(string key) => row.ValueOf(key) is { IsNumber: true } v ? v.Number : 0;
        bool Flag(string key) => row.ValueOf(key) is { } v && string.Equals(v.Text, S.Yes, StringComparison.OrdinalIgnoreCase);
        return new ToleranceOperandRow
        {
            Code = DefinitionOf(row).Code,
            Comment = row.ValueOf(S.CommentKey)?.Text ?? "",
            Int1 = Int(S.Int1Key), Int2 = Int(S.Int2Key), Int3 = Int(S.Int3Key),
            Min = Num(S.MinKey), Max = Num(S.MaxKey),
            Ignore = Flag(S.IgnoreKey), DoNotAdjust = Flag(S.DoNotAdjustKey),
        };
    }), SelectedIrregularityModel.Model);

    // ------------------------------------------------------------------ files

    /// <summary>The table as the files store it; null for an empty table with the default model.</summary>
    public OpticsToleranceSettings? ToSettings()
    {
        var data = ToleranceDataSettings.ToSettings(ToTable());
        var run = RunSettingsToSave();   // OP-64
        var monteCarlo = MonteCarloSettingsToSave();   // OP-65
        if (run is null && monteCarlo is null) return data;
        return (data ?? new OpticsToleranceSettings()) with { Run = run, MonteCarlo = monteCarlo };
    }

    /// <summary>Loads a file's tolerance data; null is an empty table. Unknown codes become TOFF
    /// and are named in <paramref name="notes"/>. Clears the undo history.</summary>
    public void LoadSettings(OpticsToleranceSettings? saved, ICollection<string> notes)
    {
        Load(ToleranceDataSettings.FromSettings(saved, notes), notes);
        LoadRunOptions(ToleranceRunSettings.FromSettings(saved?.Run, notes));   // OP-64
        LoadMonteCarloSettings(saved?.MonteCarlo, notes);   // OP-65
    }

    /// <summary>A new project's tolerance data: empty, with the default run settings.</summary>
    public void Reset()
    {
        Load(new ToleranceTable(), new List<string>());
        LoadRunOptions(new ToleranceRunOptions());   // OP-64
        InitializeMonteCarlo();   // OP-65
    }

    /// <summary>Replaces the rows with <paramref name="table"/>'s, without an undo step.</summary>
    public void Load(ToleranceTable table, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(table);
        _applying = true;
        try
        {
            SelectedIrregularityModel = IrregularityModelOption.All.First(o => o.Model == table.IrregularityModel);
            var doc = Document;
            if (doc.RowCount > 0) doc.DeleteRows(Enumerable.Range(0, doc.RowCount));
            for (int i = 0; i < table.Rows.Count; i++)
            {
                var r = table.Rows[i];
                var d = ToleranceOperandRegistry.Get(r.Code);
                var row = doc.AddRow(doc.Schema.FindRowType(S.TypeName(d)));
                void Set(int slot, CellValue value)
                {
                    if (row.Cells[slot].Definition is null) return;
                    var result = doc.SetValue(i, slot + 1, value);
                    if (!result.Succeeded) notes.Add($"Tolerance data row {i + 1}: {result.Message}");
                }
                Set(S.CommentSlot, CellValue.Of(r.Comment));
                Set(S.Int1Slot, CellValue.Of(r.Int1));
                Set(S.Int2Slot, CellValue.Of(r.Int2));
                Set(S.Int3Slot, CellValue.Of(r.Int3));
                Set(S.MinSlot, CellValue.Of(r.Min));
                Set(S.MaxSlot, CellValue.Of(r.Max));
                Set(S.IgnoreSlot, CellValue.Of(r.Ignore ? S.Yes : S.No));
                Set(S.DoNotAdjustSlot, CellValue.Of(r.DoNotAdjust ? S.Yes : S.No));
            }
            doc.History.Clear();
        }
        finally { _applying = false; }
        Problems = Array.Empty<string>();
        OnPropertyChanged(nameof(OperandCount));
    }

    private void OnSheetChanged(object? sender, EventArgs e)
    {
        if (_applying) return;
        OnPropertyChanged(nameof(OperandCount));
        _publish();
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Validates the rows against the lens and fills the Nominal column (OpticStudio's Validate).</summary>
    [RelayCommand]
    private void Update()
    {
        var table = ToTable();
        var lens = _lens();
        _applying = true;
        try
        {
            for (int i = 0; i < table.Rows.Count; i++)
            {
                if (Document.Rows[i].Cells[S.NominalSlot].Definition is null) continue;
                double? v = ToleranceNominal.ValueOf(lens, table.Rows[i]);
                Document.SetComputed(i, S.NominalSlot + 1, CellValue.Of(v is double x ? x.ToString("G8", CultureInfo.InvariantCulture) : ""));
            }
        }
        finally { _applying = false; }
        Problems = ToleranceValidation.Validate(table, lens);
        Status = table.Rows.Count == 0 ? "There are no operands."
            : Problems.Count == 0 ? $"Validated: {table.Rows.Count} row{(table.Rows.Count == 1 ? "" : "s")}, no problems."
            : $"Validated: {Problems.Count} problem{(Problems.Count == 1 ? "" : "s")}.";
    }

    [RelayCommand] private void Loosen() => Scale(2, "Loosen 2x");

    [RelayCommand] private void Tighten() => Scale(0.5, "Tighten 2x");

    /// <summary>Multiplies every tolerance row's Min and Max by <paramref name="factor"/> as one undo step.</summary>
    private void Scale(double factor, string name)
    {
        int changed = 0;
        using (Document.History.BeginGroup(name))
        {
            for (int i = 0; i < Document.RowCount; i++)
            {
                var row = Document.Rows[i];
                if (DefinitionOf(row).IsControl) continue;
                foreach (int slot in new[] { S.MinSlot, S.MaxSlot })
                    if (row.Cells[slot].Definition is not null && row.ValueOf(slot == S.MinSlot ? S.MinKey : S.MaxKey) is { IsNumber: true } v && v.Number != 0)
                    {
                        Document.SetValue(i, slot + 1, CellValue.Of(v.Number * factor));
                        changed++;
                    }
            }
        }
        Status = changed == 0 ? $"{name}: there are no tolerance limits to change." : $"{name}: {changed} limit{(changed == 1 ? "" : "s")} changed.";
    }

    [RelayCommand]
    private void InsertOperand()
    {
        int at = Math.Clamp(CurrentRow, 0, Document.RowCount);
        Report(Document.InsertRows(at), $"Row {at + 1} inserted.");
        CurrentRow = at;
    }

    [RelayCommand]
    private void InsertOperandAfter()
    {
        int at = Document.RowCount == 0 ? 0 : Math.Clamp(CurrentRow + 1, 0, Document.RowCount);
        Report(Document.InsertRows(at), $"Row {at + 1} inserted.");
        CurrentRow = at;
    }

    [RelayCommand]
    private void DeleteOperand()
    {
        if (CurrentRow < 0 || CurrentRow >= Document.RowCount) { Status = "There is no row to delete."; return; }
        int at = CurrentRow;
        if (Report(Document.DeleteRows(new[] { at }), $"Row {at + 1} deleted."))
            CurrentRow = Math.Max(0, Math.Min(at, Document.RowCount - 1));
    }

    [RelayCommand]
    private void Undo()
    {
        string? what = Document.History.UndoDescription;
        Status = Document.Undo() ? $"Undone: {what}." : "There is nothing to undo.";
    }

    [RelayCommand]
    private void Redo()
    {
        string? what = Document.History.RedoDescription;
        Status = Document.Redo() ? $"Redone: {what}." : "There is nothing to redo.";
    }

    private bool Report(SheetEditResult result, string done)
    {
        Status = result.Succeeded ? done : result.Message ?? "Not done.";
        return result.Succeeded;
    }
}
