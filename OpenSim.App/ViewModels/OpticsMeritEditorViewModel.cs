using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using S = OpenSim.App.ViewModels.OpticsMeritEditorSchema;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Merit Function Editor (plan item OP-54): the operand rows on OP-10's spreadsheet
/// (<see cref="SheetDocument"/>, schema <see cref="OpticsMeritEditorSchema"/>), saved as
/// <see cref="OpticsMeritFunctionSettings"/> with the project and the lens file. Update evaluates
/// the merit function on the project's lens (<see cref="MeritEvaluator"/>) and writes each row's
/// value and percent contribution; a row that failed shows "failed" with the reason in its
/// tooltip. Every edit lands on the sheet's undo stack, publishes the system and clears the
/// values until the next Update, as OpticStudio's editor recomputes on its Update button
/// (OpticStudio 2025 R2 help, Merit Function Editor Toolbar).
/// </summary>
public sealed partial class OpticsMeritEditorViewModel : ObservableObject
{
    private readonly Action _publish;
    private readonly Func<IMeritConfigurations> _configurations;
    private readonly Func<int> _currentConfiguration;   // OP-56
    private readonly Dictionary<string, MeritOperandDefinition> _byTypeName = new(StringComparer.Ordinal);
    private bool _applying;

    /// <param name="publish">Called after every edit, to publish the system.</param>
    /// <param name="configurations">The configurations to evaluate over; may throw
    /// <see cref="ArgumentException"/> naming why the lens cannot be built.</param>
    /// <param name="currentConfiguration">The current configuration (1-based) the Optimization
    /// Wizard writes for when asked for the current one; null is configuration 1 (OP-56).</param>
    public OpticsMeritEditorViewModel(Action publish, Func<IMeritConfigurations> configurations, MeritOperandRegistry? registry = null,
        Func<int>? currentConfiguration = null)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        _currentConfiguration = currentConfiguration ?? (() => 1);
        Registry = registry ?? MeritOperandRegistry.CreateDefault();
        foreach (var d in Registry.All) _byTypeName[S.TypeName(d)] = d;
        _document = new SheetDocument(S.Build(Registry));
        _document.Changed += OnSheetChanged;
    }

    /// <summary>The operands the editor offers.</summary>
    public MeritOperandRegistry Registry { get; }

    /// <summary>The sheet the grid shows.</summary>
    public SheetDocument Document => _document;

    private readonly SheetDocument _document;

    /// <summary>The row the grid's current cell is on (the view sets it).</summary>
    [ObservableProperty] private int _currentRow;

    /// <summary>What the last command did, or why it did nothing.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>The merit value after Update, or why there is none.</summary>
    [ObservableProperty] private string _meritValueText = "Not evaluated: press Update.";

    /// <summary>The problems of the last Update, one line each naming the row.</summary>
    [ObservableProperty] private IReadOnlyList<string> _problems = Array.Empty<string>();

    /// <summary>Text to search the operands by code or name.</summary>
    [ObservableProperty] private string _findText = "";

    /// <summary>The last evaluation, or null after an edit.</summary>
    public MeritEvaluation? LastEvaluation { get; private set; }

    public bool HasProblems => Problems.Count > 0;

    public string ProblemsText => string.Join(Environment.NewLine, Problems);

    partial void OnProblemsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ProblemsText));
    }

    /// <summary>The operands matching <see cref="FindText"/>, one line each: code, name, description.</summary>
    public IReadOnlyList<string> FindResults => FindText.Trim().Length == 0 ? Array.Empty<string>()
        : Registry.Search(FindText).Select(d => $"{d.Code}  {d.Name}: {d.Description}").ToArray();

    partial void OnFindTextChanged(string value) => OnPropertyChanged(nameof(FindResults));

    public int OperandCount => Document.RowCount;

    // ------------------------------------------------------------------ model

    /// <summary>The definition of a row's type.</summary>
    public MeritOperandDefinition DefinitionOf(SheetRow row) => _byTypeName[row.Type.Name];

    /// <summary>The merit function the sheet holds.</summary>
    public MeritFunction ToFunction() => new(Document.Rows.Select(row =>
    {
        int Int(string key) => row.ValueOf(key) is { IsNumber: true } v ? (int)v.Number : 0;
        double Num(string key) => row.ValueOf(key) is { IsNumber: true } v ? v.Number : 0;
        return new MeritOperandRow
        {
            Code = DefinitionOf(row).Code,
            Comment = row.ValueOf(S.CommentKey)?.Text ?? "",
            Int1 = Int(S.Int1Key),
            Int2 = Int(S.Int2Key),
            Data = Enumerable.Range(1, MeritOperandRow.DataCount).Select(k => Num(S.DataKey(k))).ToArray(),
            Target = Num(S.TargetKey),
            Weight = Num(S.WeightKey),
        };
    }));

    // ------------------------------------------------------------------ files

    /// <summary>The rows as the files store them; null for an empty merit function.</summary>
    public OpticsMeritFunctionSettings? ToSettings() =>
        Document.RowCount == 0 ? null : MeritFunctionSettings.ToSettings(ToFunction());

    /// <summary>Loads a file's merit function; null (a file from before OP-54, or one saved with no
    /// operands) is an empty one, so a project never opens with another's rows. Unknown codes
    /// become OOFF and are named in <paramref name="notes"/>. Clears the undo history.</summary>
    public void LoadSettings(OpticsMeritFunctionSettings? saved, ICollection<string> notes) =>
        Load(MeritFunctionSettings.FromSettings(saved, Registry, notes), notes);

    /// <summary>A new project's merit function: empty.</summary>
    public void Reset()
    {
        Load(new MeritFunction(), new List<string>());
        LoadWizardSettings(null);   // OP-56
    }

    /// <summary>Replaces the rows with <paramref name="function"/>'s, without an undo step.</summary>
    public void Load(MeritFunction function, ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(function);
        _applying = true;
        try
        {
            var doc = Document;
            if (doc.RowCount > 0) doc.DeleteRows(Enumerable.Range(0, doc.RowCount));
            for (int i = 0; i < function.Rows.Count; i++)
            {
                var r = function.Rows[i];
                var d = Registry.Get(r.Code);
                var row = doc.AddRow(doc.Schema.FindRowType(S.TypeName(d)));
                void Set(int slot, CellValue value)
                {
                    if (row.Cells[slot].Definition is null) return;
                    var result = doc.SetValue(i, slot + 1, value);
                    if (!result.Succeeded) notes.Add($"Merit function row {i + 1}: {result.Message}");
                }
                Set(S.CommentSlot, CellValue.Of(r.Comment));
                Set(S.Int1Slot, CellValue.Of(r.Int1));
                Set(S.Int2Slot, CellValue.Of(r.Int2));
                for (int k = 1; k <= MeritOperandRow.DataCount; k++) Set(S.Data1Slot + k - 1, CellValue.Of(r.DataAt(k)));
                Set(S.TargetSlot, CellValue.Of(r.Target));
                Set(S.WeightSlot, CellValue.Of(r.Weight));
            }
            doc.History.Clear();
        }
        finally { _applying = false; }
        Invalidate();
        OnPropertyChanged(nameof(OperandCount));
    }

    private void OnSheetChanged(object? sender, EventArgs e)
    {
        if (_applying) return;
        Invalidate();
        OnPropertyChanged(nameof(OperandCount));
        _publish();
    }

    /// <summary>Clears the values and contributions: they are of the rows before the edit.</summary>
    private void Invalidate()
    {
        LastEvaluation = null;
        foreach (var row in Document.Rows)
            foreach (int slot in new[] { S.ValueSlot, S.ContributionSlot })
                if (row.Cells[slot].Definition is not null)
                {
                    Document.SetComputed(row.Index, slot + 1, CellValue.Of(""));
                    Document.SetNote(row.Index, slot + 1, null);
                }
        MeritValueText = Document.RowCount == 0 ? "No operands." : "Not evaluated: press Update.";
        Problems = Array.Empty<string>();
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Evaluates the merit function and writes each row's value and contribution
    /// (OpticStudio's Update).</summary>
    [RelayCommand]
    private void Update()
    {
        Invalidate();
        if (Document.RowCount == 0) { Status = "There are no operands to evaluate."; return; }
        var function = ToFunction();
        IMeritConfigurations configurations;
        try { configurations = _configurations(); }
        catch (ArgumentException e)
        {
            Problems = new[] { e.Message };
            MeritValueText = "Not evaluated: the lens cannot be built.";
            Status = "Update failed.";
            return;
        }
        var invalid = MeritEvaluator.Validate(function, Registry, configurations.Count);
        if (invalid.Count > 0)
        {
            Problems = invalid;
            MeritValueText = "Not evaluated: the merit function has problems.";
            Status = "Update failed.";
            return;
        }
        var evaluation = MeritEvaluator.Evaluate(function, configurations, Registry);
        foreach (var r in evaluation.Rows)
        {
            int index = r.Number - 1;
            if (Document.Rows[index].Cells[S.ValueSlot].Definition is null) continue;
            string value = r.Status switch
            {
                MeritRowStatus.Evaluated => Format(r.Value),
                MeritRowStatus.Failed => "failed",
                MeritRowStatus.Skipped => "skipped",
                _ => "",
            };
            Document.SetComputed(index, S.ValueSlot + 1, CellValue.Of(value));
            if (r.Error is not null) Document.SetNote(index, S.ValueSlot + 1, r.Error, isError: true);
            Document.SetComputed(index, S.ContributionSlot + 1,
                CellValue.Of(r.Counts ? r.Contribution.ToString("F3", CultureInfo.InvariantCulture) : ""));
        }
        LastEvaluation = evaluation;
        Problems = evaluation.Errors;
        int failed = evaluation.Rows.Count(r => r.Status == MeritRowStatus.Failed);
        MeritValueText = $"Merit function: {Format(evaluation.Value)}"
            + (failed > 0 ? $" ({failed} row{(failed == 1 ? "" : "s")} failed and {(failed == 1 ? "is" : "are")} penalised)" : "");
        Status = $"Updated: {evaluation.Rows.Count(r => r.Status == MeritRowStatus.Evaluated)} operands evaluated, {evaluation.RaysTraced} rays traced.";
    }

    private static string Format(double x) => x.ToString("G10", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ OP-56: the Optimization Wizard

    private OpticsMeritWizardSettings? _wizardSettings;

    /// <summary>The Optimization Wizard's saved settings (its Save Settings), saved with the
    /// project; null is the wizard's defaults. Setting it publishes the system.</summary>
    public OpticsMeritWizardSettings? WizardSettings
    {
        get => _wizardSettings;
        set
        {
            if (Equals(_wizardSettings, value)) return;
            _wizardSettings = value;
            OnPropertyChanged();
            _publish();
        }
    }

    /// <summary>Loads a file's wizard settings without publishing (null: the defaults).</summary>
    public void LoadWizardSettings(OpticsMeritWizardSettings? saved)
    {
        _wizardSettings = saved;
        OnPropertyChanged(nameof(WizardSettings));
    }

    /// <summary>A new Optimization Wizard dialog on this editor.</summary>
    public OpticsMeritWizardViewModel CreateWizard() => new(this);

    // OP-88: the non-sequential Optimization and Bitmap Wizards.
    /// <summary>Sets detector object n's pixel counts for the Bitmap Wizard's Resample Detector
    /// (the workspace routes it to the Non-Sequential Component Editor); null refuses it.</summary>
    public Func<int, int, int, string?>? ResampleNscDetector { get; set; }

    /// <summary>A new non-sequential Optimization / Bitmap Wizard dialog on this editor, reading
    /// the project's objects through the configurations (configuration 1's objects).</summary>
    public OpticsNscMeritWizardViewModel CreateNscWizard() => new(this, () =>
    {
        try
        {
            var c = _configurations();
            IReadOnlyList<OpticsNscObjectSettings> rows = c.SettingsFor(1)?.NonSequential?.Objects ?? new List<OpticsNscObjectSettings>();
            return (rows, c.Count);
        }
        catch (ArgumentException) { return (Array.Empty<OpticsNscObjectSettings>(), 1); }
    }, ResampleNscDetector);

    /// <summary>
    /// Generates the default merit function (<see cref="DefaultMeritWizard.Generate"/>) on the
    /// project's configurations and inserts it at <see cref="DefaultMeritOptions.StartAt"/> (null:
    /// the row after DMFS, else the end), renumbering rows that refer to later rows, as one undo
    /// step. Throws <see cref="ArgumentException"/> naming why nothing was inserted.
    /// </summary>
    public DefaultMeritResult ApplyDefaultMerit(DefaultMeritOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configurations = _configurations();
        int current = Math.Clamp(_currentConfiguration(), 1, configurations.Count);
        var result = DefaultMeritWizard.Generate(configurations, current, options);
        var existing = ToFunction();
        int startAt = options.StartAt ?? DefaultMeritWizard.DefaultStartAt(existing);
        Replace(DefaultMeritWizard.Insert(existing, result.Rows, startAt, Registry), "Optimization Wizard");
        Status = $"Optimization Wizard: {result.Rows.Count} rows inserted.";
        return result;
    }

    // OP-62
    /// <summary>Replaces the rows with <paramref name="function"/>'s as one undo step named
    /// <paramref name="description"/> (the Optimize window's High-Yield rows); returns why it
    /// was refused, or null.</summary>
    public string? ApplyFunction(MeritFunction function, string description)
    {
        ArgumentNullException.ThrowIfNull(function);
        try { Replace(function, description); }
        catch (InvalidOperationException e) { return e.Message; }
        Status = $"{description}: {function.Rows.Count} rows.";
        return null;
    }

    /// <summary>Replaces the rows with <paramref name="function"/>'s as one undo step named
    /// <paramref name="description"/>; publishes once.</summary>
    private void Replace(MeritFunction function, string description)
    {
        var doc = Document;
        _applying = true;
        try
        {
            using var group = doc.History.BeginGroup(description);
            if (doc.RowCount > 0) doc.DeleteRows(Enumerable.Range(0, doc.RowCount));
            for (int i = 0; i < function.Rows.Count; i++)
            {
                var r = function.Rows[i];
                var type = doc.Schema.FindRowType(S.TypeName(Registry.Get(r.Code)));
                var inserted = doc.InsertRows(i, 1, type);
                if (!inserted.Succeeded) throw new InvalidOperationException($"Merit function row {i + 1}: {inserted.Message}");
                var row = doc.Rows[i];
                void Set(int slot, CellValue value)
                {
                    if (row.Cells[slot].Definition is null) return;
                    var set = doc.SetValue(i, slot + 1, value);
                    if (!set.Succeeded) throw new InvalidOperationException($"Merit function row {i + 1}: {set.Message}");
                }
                Set(S.CommentSlot, CellValue.Of(r.Comment));
                Set(S.Int1Slot, CellValue.Of(r.Int1));
                Set(S.Int2Slot, CellValue.Of(r.Int2));
                for (int k = 1; k <= MeritOperandRow.DataCount; k++) Set(S.Data1Slot + k - 1, CellValue.Of(r.DataAt(k)));
                Set(S.TargetSlot, CellValue.Of(r.Target));
                Set(S.WeightSlot, CellValue.Of(r.Weight));
            }
        }
        finally { _applying = false; }
        Invalidate();
        OnPropertyChanged(nameof(OperandCount));
        _publish();
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
