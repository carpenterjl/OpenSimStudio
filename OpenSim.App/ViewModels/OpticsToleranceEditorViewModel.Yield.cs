using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Tolerancing;
using S = OpenSim.App.ViewModels.OpticsToleranceEditorSchema;

namespace OpenSim.App.ViewModels;

// OP-66: the Tolerance Wizard, the Tolerance Summary, Quick Yield and the way into the Tolerance Data
// Viewer, inside the Tolerance Data Editor (docs/Optics-Conventions.md §51).

/// <summary>
/// The Tolerance Wizard's form (OpticStudio 2025 R2 help, "Tolerance Wizard"): one property per control,
/// converted to and from <see cref="ToleranceWizardOptions"/>. Stop At Surface 0 means the surface before
/// the image; an empty focus range means none (the wizard then writes Min = Max = 0 and says so).
/// </summary>
public sealed partial class ToleranceWizardForm : ObservableObject
{
    public IReadOnlyList<ToleranceChoice<ToleranceRadiusUnit>> RadiusUnits { get; } = new[]
    {
        new ToleranceChoice<ToleranceRadiusUnit>(ToleranceRadiusUnit.LensUnits, "Lens units"),
        new ToleranceChoice<ToleranceRadiusUnit>(ToleranceRadiusUnit.Percent, "Percent"),
        new ToleranceChoice<ToleranceRadiusUnit>(ToleranceRadiusUnit.Fringes, "Fringes"),
    };

    public IReadOnlyList<ToleranceChoice<ToleranceTiltUnit>> TiltUnits { get; } = new[]
    {
        new ToleranceChoice<ToleranceTiltUnit>(ToleranceTiltUnit.LensUnits, "Lens units (TIR)"),
        new ToleranceChoice<ToleranceTiltUnit>(ToleranceTiltUnit.Degrees, "Degrees"),
    };

    [ObservableProperty] private bool _radius;
    [ObservableProperty] private ToleranceChoice<ToleranceRadiusUnit>? _radiusUnit;
    [ObservableProperty] private double _radiusValue;
    [ObservableProperty] private bool _thickness;
    [ObservableProperty] private double _thicknessValue;
    [ObservableProperty] private bool _decenterX;
    [ObservableProperty] private double _decenterXValue;
    [ObservableProperty] private bool _decenterY;
    [ObservableProperty] private double _decenterYValue;
    [ObservableProperty] private bool _tiltX;
    [ObservableProperty] private ToleranceChoice<ToleranceTiltUnit>? _tiltXUnit;
    [ObservableProperty] private double _tiltXValue;
    [ObservableProperty] private bool _tiltY;
    [ObservableProperty] private ToleranceChoice<ToleranceTiltUnit>? _tiltYUnit;
    [ObservableProperty] private double _tiltYValue;
    [ObservableProperty] private bool _irregularity;
    [ObservableProperty] private double _irregularityFringes;
    [ObservableProperty] private bool _elementDecenterX;
    [ObservableProperty] private double _elementDecenterXValue;
    [ObservableProperty] private bool _elementDecenterY;
    [ObservableProperty] private double _elementDecenterYValue;
    [ObservableProperty] private bool _elementTiltX;
    [ObservableProperty] private double _elementTiltXDegrees;
    [ObservableProperty] private bool _elementTiltY;
    [ObservableProperty] private double _elementTiltYDegrees;
    [ObservableProperty] private bool _index;
    [ObservableProperty] private double _indexValue;
    [ObservableProperty] private bool _abbe;
    [ObservableProperty] private double _abbePercent;
    [ObservableProperty] private int _startAtRow;
    [ObservableProperty] private double _testWavelength;
    [ObservableProperty] private int _startSurface;
    [ObservableProperty] private int _stopSurface;
    [ObservableProperty] private bool _focusCompensator;
    [ObservableProperty] private string _focusCompensatorRange = "";

    public ToleranceWizardForm() => Load(new ToleranceWizardOptions());

    /// <summary>The options the form holds.</summary>
    /// <exception cref="ArgumentException">The focus range is not a number.</exception>
    public ToleranceWizardOptions ToOptions()
    {
        double? range = null;
        string t = (FocusCompensatorRange ?? "").Trim();
        if (t.Length > 0)
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new ArgumentException($"Focus compensator range: '{t}' is not a number; leave it empty for none.");
            range = v;
        }
        return new ToleranceWizardOptions
        {
            Radius = Radius, RadiusUnit = RadiusUnit?.Value ?? ToleranceRadiusUnit.LensUnits, RadiusValue = RadiusValue,
            Thickness = Thickness, ThicknessValue = ThicknessValue,
            DecenterX = DecenterX, DecenterXValue = DecenterXValue, DecenterY = DecenterY, DecenterYValue = DecenterYValue,
            TiltX = TiltX, TiltXUnit = TiltXUnit?.Value ?? ToleranceTiltUnit.LensUnits, TiltXValue = TiltXValue,
            TiltY = TiltY, TiltYUnit = TiltYUnit?.Value ?? ToleranceTiltUnit.LensUnits, TiltYValue = TiltYValue,
            Irregularity = Irregularity, IrregularityFringes = IrregularityFringes,
            ElementDecenterX = ElementDecenterX, ElementDecenterXValue = ElementDecenterXValue,
            ElementDecenterY = ElementDecenterY, ElementDecenterYValue = ElementDecenterYValue,
            ElementTiltX = ElementTiltX, ElementTiltXDegrees = ElementTiltXDegrees,
            ElementTiltY = ElementTiltY, ElementTiltYDegrees = ElementTiltYDegrees,
            Index = Index, IndexValue = IndexValue, Abbe = Abbe, AbbePercent = AbbePercent,
            StartAtRow = StartAtRow, TestWavelength = TestWavelength, StartSurface = StartSurface,
            StopSurface = StopSurface > 0 ? StopSurface : null,
            FocusCompensator = FocusCompensator, FocusCompensatorRange = range,
        };
    }

    /// <summary>Shows <paramref name="o"/> ("Reset" with the defaults).</summary>
    public void Load(ToleranceWizardOptions o)
    {
        Radius = o.Radius; RadiusUnit = RadiusUnits.First(c => c.Value == o.RadiusUnit); RadiusValue = o.RadiusValue;
        Thickness = o.Thickness; ThicknessValue = o.ThicknessValue;
        DecenterX = o.DecenterX; DecenterXValue = o.DecenterXValue; DecenterY = o.DecenterY; DecenterYValue = o.DecenterYValue;
        TiltX = o.TiltX; TiltXUnit = TiltUnits.First(c => c.Value == o.TiltXUnit); TiltXValue = o.TiltXValue;
        TiltY = o.TiltY; TiltYUnit = TiltUnits.First(c => c.Value == o.TiltYUnit); TiltYValue = o.TiltYValue;
        Irregularity = o.Irregularity; IrregularityFringes = o.IrregularityFringes;
        ElementDecenterX = o.ElementDecenterX; ElementDecenterXValue = o.ElementDecenterXValue;
        ElementDecenterY = o.ElementDecenterY; ElementDecenterYValue = o.ElementDecenterYValue;
        ElementTiltX = o.ElementTiltX; ElementTiltXDegrees = o.ElementTiltXDegrees;
        ElementTiltY = o.ElementTiltY; ElementTiltYDegrees = o.ElementTiltYDegrees;
        Index = o.Index; IndexValue = o.IndexValue; Abbe = o.Abbe; AbbePercent = o.AbbePercent;
        StartAtRow = o.StartAtRow; TestWavelength = o.TestWavelength; StartSurface = o.StartSurface; StopSurface = o.StopSurface ?? 0;
        FocusCompensator = o.FocusCompensator;
        FocusCompensatorRange = o.FocusCompensatorRange is double r ? r.ToString("R", CultureInfo.InvariantCulture) : "";
    }
}

public sealed partial class OpticsToleranceEditorViewModel
{
    private QuickYieldResult? _lastQuickYield;
    private bool _wizardHooked;

    /// <summary>The Tolerance Wizard's form; its settings are saved with the tolerance data.</summary>
    public ToleranceWizardForm Wizard { get; } = new();

    [ObservableProperty] private string _wizardStatus = "Set the default tolerances, then Apply wizard. The rows go in as one undo step.";

    /// <summary>The Tolerance Summary's text (help, "Tolerance Summary").</summary>
    [ObservableProperty] private string _summaryText = "";

    [ObservableProperty] private int _quickYieldSamples = new QuickYieldOptions().Samples;
    [ObservableProperty] private bool _quickYieldCrossTerms = new QuickYieldOptions().CrossTerms;
    [ObservableProperty] private string _quickYieldStatus = "Quick Yield fits a quadratic model of the criterion from compensated evaluations and samples it with the Monte Carlo statistics, seed and yield limit above.";
    [ObservableProperty] private string _quickYieldReportText = "";
    [ObservableProperty] private AnalysisPlot? _quickYieldHistogramPlot;
    [ObservableProperty] private AnalysisPlot? _quickYieldCurvePlot;

    /// <summary>The last Quick Yield result, or null.</summary>
    public QuickYieldResult? LastQuickYield => _lastQuickYield;

    /// <summary>Raised by Open data viewer with the viewer's view model; the window opens it.</summary>
    public event EventHandler<ToleranceDataViewerViewModel>? DataViewerRequested;

    private static readonly HashSet<string> QuickYieldSettingNames = new(StringComparer.Ordinal) { nameof(QuickYieldSamples), nameof(QuickYieldCrossTerms) };

    private void HookWizard()
    {
        if (_wizardHooked) return;
        _wizardHooked = true;
        Wizard.PropertyChanged += (_, _) => { if (!_applying) _publish(); };
    }

    private void LoadYieldSettings(OpticsToleranceSettings? saved, ICollection<string> notes)
    {
        bool was = _applying;
        _applying = true;
        try
        {
            Wizard.Load(ToleranceWizardSettings.FromSettings(saved?.Wizard, notes));
            var q = QuickYieldSettings.FromSettings(saved?.QuickYield);
            QuickYieldSamples = q.Samples;
            QuickYieldCrossTerms = q.CrossTerms;
        }
        finally { _applying = was; }
        HookWizard();
    }

    private OpticsToleranceWizardSettings? WizardSettingsToSave()
    {
        try { return ToleranceWizardSettings.ToSettings(Wizard.ToOptions()); }
        catch (ArgumentException) { return null; }
    }

    private OpticsToleranceQuickYieldSettings? QuickYieldSettingsToSave() =>
        QuickYieldSettings.ToSettings(new QuickYieldOptions { Samples = QuickYieldSamples, CrossTerms = QuickYieldCrossTerms });

    private void OnYieldPropertyChanged(string? name)
    {
        if (name is not null && QuickYieldSettingNames.Contains(name) && !_applying) _publish();
        if (name is nameof(LastRun) or nameof(LastMonteCarlo)) OpenDataViewerCommand.NotifyCanExecuteChanged();
        if (name == nameof(IsRunning))
        {
            RunQuickYieldCommand.NotifyCanExecuteChanged();
            OpenDataViewerCommand.NotifyCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------------ wizard

    /// <summary>Writes the wizard's rows into the table as one undo step (help: "Apply: Accept these settings and
    /// generate the default tolerances").</summary>
    [RelayCommand]
    private void ApplyWizard()
    {
        ToleranceWizardResult result;
        try { result = ToleranceWizard.Generate(_lens(), ToTable(), _catalogs(), Wizard.ToOptions()); }
        catch (ArgumentException ex) { WizardStatus = ex.Message; return; }
        int at = result.FirstRow - 1;
        var problems = new List<string>();
        _applying = true;
        try
        {
            using (Document.History.BeginGroup("Tolerance Wizard"))
            {
                if (at < Document.RowCount) Document.DeleteRows(Enumerable.Range(at, Document.RowCount - at));
                for (int k = 0; k < result.RowCount; k++)
                {
                    int i = at + k;
                    var r = result.Table.Rows[i];
                    var d = ToleranceOperandRegistry.Get(r.Code);
                    var insert = Document.InsertRows(i, 1, Document.Schema.FindRowType(S.TypeName(d)));
                    if (!insert.Succeeded) { problems.Add($"Row {i + 1}: {insert.Message}"); continue; }
                    WriteRow(i, r, problems);
                }
            }
        }
        finally { _applying = false; }
        OnPropertyChanged(nameof(OperandCount));
        _publish();
        WizardStatus = $"Tolerance Wizard: {result.RowCount} row{(result.RowCount == 1 ? "" : "s")} written from row {result.FirstRow}. Undo removes them."
            + (result.Notes.Count + problems.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, result.Notes.Concat(problems)) : "");
    }

    /// <summary>"Reset: Restore the settings to the default values".</summary>
    [RelayCommand]
    private void ResetWizard()
    {
        Wizard.Load(new ToleranceWizardOptions());
        WizardStatus = "The wizard's settings are the defaults again (the values the help's figure of the dialog shows).";
    }

    private void WriteRow(int i, ToleranceOperandRow r, List<string> problems)
    {
        var row = Document.Rows[i];
        void Set(int slot, CellValue value)
        {
            if (row.Cells[slot].Definition is null) return;
            var result = Document.SetValue(i, slot + 1, value);
            if (!result.Succeeded) problems.Add($"Row {i + 1}: {result.Message}");
        }
        if (r.Comment.Length > 0) Set(S.CommentSlot, CellValue.Of(r.Comment));
        Set(S.Int1Slot, CellValue.Of(r.Int1));
        Set(S.Int2Slot, CellValue.Of(r.Int2));
        Set(S.Int3Slot, CellValue.Of(r.Int3));
        Set(S.MinSlot, CellValue.Of(r.Min));
        Set(S.MaxSlot, CellValue.Of(r.Max));
    }

    // ------------------------------------------------------------------ summary

    /// <summary>The Tolerance Summary of the rows as they stand.</summary>
    public Core.PostProcessing.StudyReport SummaryReport() => ToleranceSummary.Build(_lens(), ToTable());

    [RelayCommand]
    private void ShowSummary() => SummaryText = SummaryReport().ToText();

    [RelayCommand]
    private void SaveSummary()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|HTML (*.html)|*.html|Text (*.txt)|*.txt",
            FileName = "Tolerance summary.md",
            Title = "Save the tolerance summary",
        };
        if (dialog.ShowDialog() != true) return;
        var report = SummaryReport();
        string ext = System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant();
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, ext == ".html" ? report.ToHtml() : ext == ".txt" ? report.ToText() : report.ToMarkdown());
            Status = $"Tolerance summary saved to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Status = $"Tolerance summary not saved: {ex.Message}"; }
    }

    // ------------------------------------------------------------------ quick yield

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunQuickYieldAsync()
    {
        ToleranceRunOptions run;
        MonteCarloOptions mc;
        try
        {
            run = RunOptions();
            mc = MonteCarloOptions();
        }
        catch (ArgumentException ex) { QuickYieldStatus = ex.Message; return; }
        var lens = _lens();
        var table = ToTable();
        var catalogs = _catalogs();
        _cancel = new CancellationTokenSource();
        var context = SynchronizationContext.Current;
        void Post(Action action) { if (context is null) action(); else context.Post(_ => action(), null); }
        run = run with { CancellationToken = _cancel.Token };
        var options = new QuickYieldOptions
        {
            Samples = QuickYieldSamples, CrossTerms = QuickYieldCrossTerms,
            Progress = p => Post(() => { if (IsRunning) QuickYieldStatus = $"Running: {p.EvaluationsDone} of {p.EvaluationCount} lens evaluations…"; }),
        };
        IsRunning = true;
        QuickYieldStatus = "Quick Yield…";
        try
        {
            var result = await Task.Run(() => QuickYield.Run(lens, table, catalogs, run, mc, options));
            ShowQuickYield(result);
            QuickYieldStatus = !result.Succeeded
                ? $"{result.Failures.Count} evaluation{(result.Failures.Count == 1 ? "" : "s")} failed, so no estimate is given (see the report)."
                : $"Done: {result.Evaluations} lens evaluations, {result.Samples.Count} samples"
                  + (result.Yield is { } y ? $"; predicted yield {N(100 * y.Fraction)} % ± {N(100 * result.YieldStandardError!.Value)} %" : "; set a yield limit for the predicted yield")
                  + ".";
        }
        catch (OperationCanceledException) { QuickYieldStatus = "Cancelled."; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { QuickYieldStatus = ex.Message; }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsRunning = false;
        }
    }

    private void ShowQuickYield(QuickYieldResult result)
    {
        _lastQuickYield = result;
        OnPropertyChanged(nameof(LastQuickYield));
        OpenDataViewerCommand.NotifyCanExecuteChanged();
        QuickYieldReportText = string.Join(Environment.NewLine, result.ReportLines());
        QuickYieldHistogramPlot = result.HistogramPlot(monteCarlo: _lastMonteCarlo);
        QuickYieldCurvePlot = result.YieldPlot(_lastMonteCarlo);
    }

    // ------------------------------------------------------------------ data viewer

    private bool CanOpenDataViewer() => !IsRunning && (_lastRun is not null || _lastMonteCarlo is not null || _lastQuickYield is not null);

    /// <summary>The Tolerance Data Viewer of the last sensitivity, Monte Carlo and Quick Yield results.</summary>
    public ToleranceDataViewerViewModel CreateDataViewer() => new(_lastRun, _lastMonteCarlo, _lastQuickYield);

    [RelayCommand(CanExecute = nameof(CanOpenDataViewer))]
    private void OpenDataViewer() => DataViewerRequested?.Invoke(this, CreateDataViewer());
}
