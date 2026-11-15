using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Sequential;

namespace OpenSim.App.ViewModels;

/// <summary>A cycles choice of the Optimize tool (OpticStudio 2025 R2 help, Optimize!, "Cycles").</summary>
public sealed record OptimizeCyclesChoice(string Label, LocalOptimizationCycles Mode, int Count = 0);

/// <summary>An algorithm choice of the Optimize tool.</summary>
public sealed record OptimizeAlgorithmChoice(string Label, LocalOptimizationAlgorithm Algorithm);

/// <summary>
/// The Optimize tool (plan item OP-57; OpticStudio 2025 R2 help, Optimize!): the algorithm, the
/// cycles and the number of threads; Start runs <see cref="LocalOptimizer"/> on the project's
/// system off the UI thread, with the merit function before and now and a log; Stop cancels and
/// keeps the best design so far. When a run ends (finished or stopped) its variables are written
/// into the project's lens through the workspace (<c>apply</c>: the Lens Data Editor's undo
/// stack, and the multi-configuration table when a table cell is a variable), as OpticStudio
/// updates the system when its dialog exits. Undo puts the variable cells back as they were.
/// </summary>
public sealed partial class OpticsOptimizeViewModel : ObservableObject
{
    private readonly Func<OpticsSettings> _system;
    private readonly Func<IReadOnlyList<OpticalCatalog>> _catalogs;
    private readonly Func<OpticsSettings, bool, string, string?> _apply;
    private CancellationTokenSource? _cancel;
    private LocalOptimizationResult? _last;

    /// <param name="system">The project's optics record now (with the multi-configuration table).</param>
    /// <param name="catalogs">The active glass catalogs.</param>
    /// <param name="apply">Writes a record into the workspace as one undoable edit named by the
    /// text; the flag says whether the multi-configuration table changed. Returns why it was
    /// refused, or null.</param>
    /// <param name="templateChanged">OP-59: called after the Glass Substitution Template is edited
    /// (not when it is loaded), so the workspace saves it; null for none.</param>
    public OpticsOptimizeViewModel(Func<OpticsSettings> system, Func<IReadOnlyList<OpticalCatalog>> catalogs,
        Func<OpticsSettings, bool, string, string?> apply, Action? templateChanged = null)
    {
        _templateChanged = templateChanged;
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _selectedAlgorithm = Algorithms[0];
        _selectedCycles = CycleChoices[0];
        _threads = Environment.ProcessorCount;
    }

    public IReadOnlyList<OptimizeAlgorithmChoice> Algorithms { get; } = new[]
    {
        new OptimizeAlgorithmChoice("Damped Least Squares", LocalOptimizationAlgorithm.DampedLeastSquares),
        new OptimizeAlgorithmChoice("Orthogonal Descent", LocalOptimizationAlgorithm.OrthogonalDescent),
    };

    public IReadOnlyList<OptimizeCyclesChoice> CycleChoices { get; } = new[]
    {
        new OptimizeCyclesChoice("Automatic", LocalOptimizationCycles.Automatic),
        new OptimizeCyclesChoice("1 cycle", LocalOptimizationCycles.Fixed, 1),
        new OptimizeCyclesChoice("5 cycles", LocalOptimizationCycles.Fixed, 5),
        new OptimizeCyclesChoice("10 cycles", LocalOptimizationCycles.Fixed, 10),
        new OptimizeCyclesChoice("50 cycles", LocalOptimizationCycles.Fixed, 50),
        new OptimizeCyclesChoice("Infinite", LocalOptimizationCycles.Infinite),
    };

    [ObservableProperty] private OptimizeAlgorithmChoice _selectedAlgorithm;
    [ObservableProperty] private OptimizeCyclesChoice _selectedCycles;

    /// <summary>The thread limit ("# of Cores"); the result does not depend on it.</summary>
    [ObservableProperty] private int _threads;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SubstituteGlassesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyGlassFlagsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConvertModelGlassesCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    /// <summary>No run is going: the settings can be changed.</summary>
    public bool IsIdle => !IsRunning;

    [ObservableProperty] private string _meritBeforeText = "—";
    [ObservableProperty] private string _meritNowText = "—";
    [ObservableProperty] private string _cyclesText = "—";
    [ObservableProperty] private string _status = "Flag cells Variable in the editors, build a merit function, then Start.";

    /// <summary>The log of the runs, newest last.</summary>
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>The last result applied, or null.</summary>
    public LocalOptimizationResult? LastResult => _last;

    private bool CanStart() => !IsRunning;
    private bool CanStop() => IsRunning;
    private bool CanUndo() => !IsRunning && _last is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var system = _system();
        var catalogs = _catalogs();
        _cancel = new CancellationTokenSource();
        var context = SynchronizationContext.Current;
        void Post(Action action) { if (context is null) action(); else context.Post(_ => action(), null); }
        var options = new LocalOptimizationOptions
        {
            Algorithm = SelectedAlgorithm.Algorithm,
            Cycles = SelectedCycles.Mode,
            CycleCount = Math.Max(1, SelectedCycles.Count),
            MaxDegreeOfParallelism = Math.Max(1, Threads),
            CancellationToken = _cancel.Token,
            Progress = p => Post(() =>
            {
                if (!IsRunning) return;   // a late report after the run ended
                MeritBeforeText = Format(p.StartMerit);
                MeritNowText = Format(p.Merit);
                CyclesText = p.Cycle.ToString(CultureInfo.InvariantCulture);
            }),
        };
        IsRunning = true;
        Status = "Optimizing…";
        MeritNowText = "—";
        CyclesText = "0";
        try
        {
            var result = await Task.Run(() => LocalOptimizer.Run(system, catalogs, options));
            MeritBeforeText = Format(result.StartMerit);
            MeritNowText = Format(result.Merit);
            CyclesText = result.Cycles.ToString(CultureInfo.InvariantCulture);
            foreach (var line in result.Log) Log.Add(line);
            string? refusal;
            try
            {
                refusal = _apply(result.ApplyTo(_system()), result.Variables.Any(v => v.Cell is not null), "Optimize");
            }
            catch (ArgumentException ex) { refusal = ex.Message; }
            if (refusal is not null)
            {
                Status = $"The result was not applied: {refusal}";
                Log.Add(Status);
                return;
            }
            _last = result;
            Status = result.Cancelled
                ? $"Stopped: the best design so far (merit {Format(result.Merit)}) is in the lens. Undo puts the variables back."
                : $"Done: merit {Format(result.StartMerit)} → {Format(result.Merit)}, applied to the lens. Undo puts the variables back.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
            Log.Add(ex.Message);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _cancel?.Cancel();
        Status = "Stopping: the best design so far will be kept…";
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_last is not { } last) return;
        string? refusal;
        try { refusal = _apply(last.UndoOn(_system()), last.Variables.Any(v => v.Cell is not null), "Undo optimization"); }
        catch (ArgumentException ex) { refusal = ex.Message; }
        if (refusal is not null) { Status = $"Not undone: {refusal}"; return; }
        _last = null;
        UndoCommand.NotifyCanExecuteChanged();
        MeritNowText = Format(last.StartMerit);
        Status = "The variables are back as they were before the run.";
        Log.Add(Status);
    }

    // ------------------------------------------------------------------ OP-59: glasses

    private readonly Action? _templateChanged;
    private bool _loadingTemplate;

    /// <summary>The surfaces with a glass: their model-glass variables and substitution flags,
    /// as read by <see cref="RefreshGlassesCommand"/>; edits reach the lens through
    /// <see cref="ApplyGlassFlagsCommand"/>.</summary>
    public ObservableCollection<OptimizeGlassRow> Glasses { get; } = new();

    /// <summary>The last glass substitution search applied, or null.</summary>
    public GlassSubstitutionResult? LastSubstitution { get; private set; }

    // The Glass Substitution Template (OpticStudio 2025 R2 help, "Glass Substitution Template
    // (global optimizers group)"), saved with the lens.
    [ObservableProperty] private bool _useTemplate;
    [ObservableProperty] private bool _excludeIncompleteData;
    [ObservableProperty] private bool _allowStandard = true;
    [ObservableProperty] private bool _allowPreferred = true;
    [ObservableProperty] private bool _allowObsolete = true;
    [ObservableProperty] private bool _allowSpecial = true;
    [ObservableProperty] private double? _maxRelativeCost;
    [ObservableProperty] private double? _maxClimaticResistance;
    [ObservableProperty] private double? _maxStainResistance;
    [ObservableProperty] private double? _maxAcidResistance;
    [ObservableProperty] private double? _maxAlkaliResistance;
    [ObservableProperty] private double? _maxPhosphateResistance;

    /// <summary>The catalogs substitution draws from, comma-separated; empty is every active catalog.</summary>
    [ObservableProperty] private string _templateCatalogs = "";

    private static readonly HashSet<string> TemplateProperties = new()
    {
        nameof(UseTemplate), nameof(ExcludeIncompleteData), nameof(AllowStandard), nameof(AllowPreferred), nameof(AllowObsolete),
        nameof(AllowSpecial), nameof(MaxRelativeCost), nameof(MaxClimaticResistance), nameof(MaxStainResistance),
        nameof(MaxAcidResistance), nameof(MaxAlkaliResistance), nameof(MaxPhosphateResistance), nameof(TemplateCatalogs),
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loadingTemplate && e.PropertyName is { } name && TemplateProperties.Contains(name)) _templateChanged?.Invoke();
    }

    /// <summary>The template as saved; null when it is the default (no template, nothing set),
    /// so a lens without one saves as before.</summary>
    public OpticsGlassSubstitutionSettings? TemplateToSettings()
    {
        var catalogs = (TemplateCatalogs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var t = new OpticsGlassSubstitutionSettings
        {
            UseTemplate = UseTemplate, ExcludeIncompleteData = ExcludeIncompleteData,
            AllowStandard = AllowStandard, AllowPreferred = AllowPreferred, AllowObsolete = AllowObsolete, AllowSpecial = AllowSpecial,
            MaxRelativeCost = MaxRelativeCost, MaxClimaticResistance = MaxClimaticResistance, MaxStainResistance = MaxStainResistance,
            MaxAcidResistance = MaxAcidResistance, MaxAlkaliResistance = MaxAlkaliResistance, MaxPhosphateResistance = MaxPhosphateResistance,
            Catalogs = catalogs.Count > 0 ? catalogs : null,
        };
        return t == new OpticsGlassSubstitutionSettings() ? null : t;
    }

    /// <summary>Shows a saved template; null is the default (no template).</summary>
    public void LoadTemplate(OpticsGlassSubstitutionSettings? saved)
    {
        var t = saved ?? new OpticsGlassSubstitutionSettings();
        _loadingTemplate = true;
        try
        {
            UseTemplate = t.UseTemplate; ExcludeIncompleteData = t.ExcludeIncompleteData;
            AllowStandard = t.AllowStandard; AllowPreferred = t.AllowPreferred; AllowObsolete = t.AllowObsolete; AllowSpecial = t.AllowSpecial;
            MaxRelativeCost = t.MaxRelativeCost; MaxClimaticResistance = t.MaxClimaticResistance; MaxStainResistance = t.MaxStainResistance;
            MaxAcidResistance = t.MaxAcidResistance; MaxAlkaliResistance = t.MaxAlkaliResistance; MaxPhosphateResistance = t.MaxPhosphateResistance;
            TemplateCatalogs = t.Catalogs is { Count: > 0 } c ? string.Join(", ", c) : "";
        }
        finally { _loadingTemplate = false; }
    }

    /// <summary>The project's record with this window's template in it.</summary>
    private OpticsSettings SystemWithTemplate() => _system() with { GlassSubstitution = TemplateToSettings() };

    /// <summary>Reads the surfaces with a glass from the lens.</summary>
    [RelayCommand]
    private void RefreshGlasses()
    {
        Glasses.Clear();
        var surfaces = _system().Surfaces ?? new List<OpticsSurfaceSettings>();
        for (int j = 1; j < surfaces.Count - 1; j++)
        {
            var s = surfaces[j];
            if (s is null) continue;
            bool mirror = string.Equals(s.Material?.Trim(), SavedSurfaces.Mirror, StringComparison.OrdinalIgnoreCase);
            if (mirror || (string.IsNullOrWhiteSpace(s.Material) && s.ModelGlass is null)) continue;
            Glasses.Add(new OptimizeGlassRow(j, SavedSurfaces.FormatMaterial(s), s.ModelGlass is not null)
            {
                VaryNd = GlassVariables.IsFlagged(s, GlassVariables.ModelNdColumn),
                VaryVd = GlassVariables.IsFlagged(s, GlassVariables.ModelVdColumn),
                VaryDeltaPgF = GlassVariables.IsFlagged(s, GlassVariables.ModelDeltaPgFColumn),
                Substitute = GlassVariables.IsFlagged(s, GlassVariables.SubstituteColumn),
            });
        }
        Status = Glasses.Count == 0 ? "The lens has no glasses." : $"{Glasses.Count} surface{(Glasses.Count == 1 ? "" : "s")} with a glass.";
    }

    private bool CanEditGlasses() => !IsRunning;

    /// <summary>Writes the rows' flags into the lens as one undoable edit.</summary>
    [RelayCommand(CanExecute = nameof(CanEditGlasses))]
    private void ApplyGlassFlags()
    {
        var system = _system();
        var surfaces = (system.Surfaces ?? new List<OpticsSurfaceSettings>()).ToList();
        foreach (var row in Glasses)
        {
            if (row.Surface >= surfaces.Count) { Status = $"Surface {row.Surface} is no longer in the lens; refresh the glasses."; return; }
            var s = surfaces[row.Surface];
            bool model = row.IsModel && s.ModelGlass is not null;
            s = GlassVariables.WithFlag(s, GlassVariables.ModelNdColumn, model && row.VaryNd);
            s = GlassVariables.WithFlag(s, GlassVariables.ModelVdColumn, model && row.VaryVd);
            s = GlassVariables.WithFlag(s, GlassVariables.ModelDeltaPgFColumn, model && row.VaryDeltaPgF);
            s = GlassVariables.WithFlag(s, GlassVariables.SubstituteColumn, row.Substitute);
            surfaces[row.Surface] = s;
        }
        string? refusal = _apply(system with { Surfaces = surfaces }, false, "Glass variables");
        Status = refusal is null ? "The glass flags are in the lens." : $"Not applied: {refusal}";
        Log.Add(Status);
    }

    /// <summary>Replaces every model glass by its nearest catalog glass (within the template).</summary>
    [RelayCommand(CanExecute = nameof(CanEditGlasses))]
    private void ConvertModelGlasses()
    {
        try
        {
            var result = ModelGlassConversion.ConvertAll(SystemWithTemplate(), _catalogs());
            foreach (var line in result.Log) Log.Add(line);
            if (result.Items.Count == 0) { Status = "There are no model glasses to convert."; return; }
            string? refusal = _apply(result.Converted, false, "Convert model glasses");
            if (refusal is not null) { Status = $"Not applied: {refusal}"; return; }
            RefreshGlasses();
            Status = $"{result.Items.Count} model glass{(result.Items.Count == 1 ? "" : "es")} converted; optimize again.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
            Log.Add(ex.Message);
        }
    }

    /// <summary>Glass substitution: every flagged surface tries every candidate glass, re-optimizing
    /// each time, and the best design is written into the lens. Stop keeps the best so far.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task SubstituteGlassesAsync()
    {
        var system = SystemWithTemplate();
        var catalogs = _catalogs();
        _cancel = new CancellationTokenSource();
        var context = SynchronizationContext.Current;
        void Post(Action action) { if (context is null) action(); else context.Post(_ => action(), null); }
        var options = new GlassSubstitutionOptions
        {
            Optimization = new LocalOptimizationOptions
            {
                Algorithm = SelectedAlgorithm.Algorithm,
                Cycles = SelectedCycles.Mode == LocalOptimizationCycles.Infinite ? LocalOptimizationCycles.Automatic : SelectedCycles.Mode,
                CycleCount = Math.Max(1, SelectedCycles.Count),
                MaxDegreeOfParallelism = Math.Max(1, Threads),
            },
            CancellationToken = _cancel.Token,
            Progress = p => Post(() =>
            {
                if (!IsRunning) return;
                MeritBeforeText = Format(p.StartMerit);
                MeritNowText = Format(p.BestMerit);
                CyclesText = $"{p.Trials} trials";
                Status = $"Round {p.Round}, surface {p.Surface}: tried {p.Glass}…";
            }),
        };
        IsRunning = true;
        Status = "Substituting glasses…";
        try
        {
            var result = await Task.Run(() => GlassSubstitution.Run(system, catalogs, options));
            foreach (var line in result.Log) Log.Add(line);
            foreach (var t in result.Trials.Where(t => t.Error is not null)) Log.Add($"Surface {t.Surface}, {t.Glass}: {t.Error}");
            MeritBeforeText = Format(result.StartMerit);
            MeritNowText = Format(result.Merit);
            var now = _system();
            bool table = LensVariables.Find(result.Best).Any(v => v.Cell is not null);
            string? refusal = _apply(now with { Surfaces = result.Best.Surfaces, MultiConfiguration = table ? result.Best.MultiConfiguration : now.MultiConfiguration },
                table, "Glass substitution");
            if (refusal is not null) { Status = $"The result was not applied: {refusal}"; Log.Add(Status); return; }
            LastSubstitution = result;
            _last = null;   // the editors' undo takes the substitution back
            RefreshGlasses();
            Status = $"{(result.Cancelled ? "Stopped" : "Done")}: merit {Format(result.StartMerit)} → {Format(result.Merit)}; "
                + string.Join(", ", result.Glasses.Select(g => $"surface {g.Surface} {g.After}")) + ". The Lens Data Editor's undo takes it back.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
            Log.Add(ex.Message);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsRunning = false;
            UndoCommand.NotifyCanExecuteChanged();
        }
    }


    private static string Format(double merit) => merit.ToString("G8", CultureInfo.InvariantCulture);
}

// OP-59
/// <summary>One surface with a glass in the Optimize window's glass list: which model-glass
/// parameters vary and whether glass substitution may replace it.</summary>
public sealed partial class OptimizeGlassRow : ObservableObject
{
    public OptimizeGlassRow(int surface, string material, bool isModel)
    {
        Surface = surface;
        Material = material;
        IsModel = isModel;
    }

    public int Surface { get; }

    /// <summary>The Material cell's text.</summary>
    public string Material { get; }

    /// <summary>A model glass: its nd, Vd and ΔPgF may vary.</summary>
    public bool IsModel { get; }

    [ObservableProperty] private bool _varyNd;
    [ObservableProperty] private bool _varyVd;
    [ObservableProperty] private bool _varyDeltaPgF;
    [ObservableProperty] private bool _substitute;
}
