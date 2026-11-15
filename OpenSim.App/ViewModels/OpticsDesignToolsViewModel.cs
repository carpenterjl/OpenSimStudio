using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Catalogs;
using OpenSim.Optics.DesignTools;
using OpenSim.Optics.Materials;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Optimization;

namespace OpenSim.App.ViewModels;

// OP-61: the Design Conversion Tools dialog.

/// <summary>
/// The design conversion tools (plan item OP-61; OpticStudio 2025 R2 help, Optimize tab ›
/// Optimization Tools: "Find Best Asphere", "Convert Asphere Types", "Stock Lens Matching"), one
/// window with a section each. Each tool runs off the UI thread on the project's lens and shows its
/// report; the help's Keep (Keep and Exit, Save Best Combination) writes the result through the
/// workspace (<c>apply</c>: one step of the Lens Data Editor's undo stack, as the Optimize tool's
/// result is). Find Best Asphere and Stock Lens Matching can be stopped; a stopped run keeps
/// what it found.
/// </summary>
public sealed partial class OpticsDesignToolsViewModel : ObservableObject
{
    private readonly Func<OpticsSettings> _system;
    private readonly Func<IReadOnlyList<OpticalCatalog>> _catalogs;
    private readonly Func<IReadOnlyList<OpticalCatalog>> _glassLibrary;
    private readonly Func<OpticsSettings, bool, string, string?> _apply;
    private CancellationTokenSource? _cancel;
    private int _run;
    private OpticsSettings? _pending;
    private string? _pendingName;
    private LensCatalogLibrary? _library;

    /// <param name="system">The project's optics record now.</param>
    /// <param name="catalogs">The active glass catalogs (the merit function's).</param>
    /// <param name="glassLibrary">Every glass catalog (stock lens files are read with them).</param>
    /// <param name="apply">Writes a record into the workspace as one undoable edit named by the
    /// text (the flag: whether the multi-configuration table changed); returns why it was refused, or null.</param>
    /// <param name="libraryFolder">The stock lens catalog folder; null reads the user's choice (OP-16).</param>
    public OpticsDesignToolsViewModel(Func<OpticsSettings> system, Func<IReadOnlyList<OpticalCatalog>> catalogs,
        Func<IReadOnlyList<OpticalCatalog>> glassLibrary, Func<OpticsSettings, bool, string, string?> apply, string? libraryFolder = null)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _glassLibrary = glassLibrary ?? throw new ArgumentNullException(nameof(glassLibrary));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _libraryFolder = libraryFolder ?? LensCatalogLibrary.ChosenFolder();
        _selectedTarget = Targets[^1];
        _selectedKind = Kinds[0];
        _selectedSurfaces = SurfaceChoices[0];
    }

    // ---------------------------------------------------------------- Convert Asphere Types

    public IReadOnlyList<InteractiveChoice<AsphereTarget>> Targets { get; } =
        Enum.GetValues<AsphereTarget>().Select(t => new InteractiveChoice<AsphereTarget>(AsphereTypeConversion.Label(t), t)).ToArray();

    [ObservableProperty] private int _convertSurface = 1;
    [ObservableProperty] private InteractiveChoice<AsphereTarget> _selectedTarget;
    /// <summary>The help's Number of Terms "automatic".</summary>
    [ObservableProperty] private bool _automaticTerms = true;
    [ObservableProperty] private int _terms = 8;
    /// <summary>The help's rule: a non-zero or variable radius or conic is fitted too.</summary>
    [ObservableProperty] private bool _varyRadiusAndConic = true;

    // ---------------------------------------------------------------- Find Best Asphere

    public IReadOnlyList<InteractiveChoice<BestAsphereKind>> Kinds { get; } = new[]
    {
        new InteractiveChoice<BestAsphereKind>("Conic", BestAsphereKind.Conic),
        new InteractiveChoice<BestAsphereKind>("Even asphere", BestAsphereKind.EvenAsphere),
    };

    public IReadOnlyList<int> Orders { get; } = new[] { 4, 6, 8, 10, 12, 14, 16 };

    [ObservableProperty] private int _firstSurface = 1;
    /// <summary>0 for the surface before the image.</summary>
    [ObservableProperty] private int _lastSurface;
    [ObservableProperty] private InteractiveChoice<BestAsphereKind> _selectedKind;
    [ObservableProperty] private int _maxOrder = 8;

    /// <summary>The optimization cycles of every trial (both searches): automatic, or this many.</summary>
    [ObservableProperty] private bool _automaticCycles = true;
    [ObservableProperty] private int _cycleCount = 5;

    // ---------------------------------------------------------------- Stock Lens Matching

    public IReadOnlyList<InteractiveChoice<StockLensSurfaces>> SurfaceChoices { get; } = new[]
    {
        new InteractiveChoice<StockLensSurfaces>("All", StockLensSurfaces.All),
        new InteractiveChoice<StockLensSurfaces>("Variables", StockLensSurfaces.Variables),
    };

    /// <summary>The help's Vendor(s) list: "All" then the catalogs of the folder.</summary>
    public ObservableCollection<string> Vendors { get; } = new() { AllVendors };

    public const string AllVendors = "All";

    [ObservableProperty] private string _libraryFolder;
    [ObservableProperty] private string _selectedVendor = AllVendors;
    [ObservableProperty] private InteractiveChoice<StockLensSurfaces> _selectedSurfaces;
    [ObservableProperty] private int _showMatches = 5;
    [ObservableProperty] private double _eflTolerance = 25;
    [ObservableProperty] private double _epdTolerance = 25;
    [ObservableProperty] private bool _airThicknessCompensation;
    [ObservableProperty] private bool _matchShape = true;
    [ObservableProperty] private bool _matchOrientation = true;

    // ---------------------------------------------------------------- run state

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindBestAsphereCommand))]
    [NotifyCanExecuteChangedFor(nameof(MatchStockLensesCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadCatalogsCommand))]
    [NotifyCanExecuteChangedFor(nameof(KeepCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepCommand))]
    private bool _hasResult;

    [ObservableProperty] private string _status = "Convert an asphere to another form, try an asphere on each surface, or replace the lens elements with stock lenses.";

    /// <summary>The last tool's report.</summary>
    [ObservableProperty] private string _details = "";

    /// <summary>The last result kept, or null.</summary>
    public string? LastKept { get; private set; }

    private bool CanStart() => !IsRunning;

    private LocalOptimizationOptions Optimization() => new()
    {
        Cycles = AutomaticCycles ? LocalOptimizationCycles.Automatic : LocalOptimizationCycles.Fixed,
        CycleCount = Math.Max(1, CycleCount),
    };

    private void Offer(OpticsSettings? design, string name, IEnumerable<string> report, string status)
    {
        Details = string.Join(Environment.NewLine, report);
        _pending = design;
        _pendingName = name;
        HasResult = design is not null;
        Status = status;
    }

    /// <summary>Convert Asphere Types on the chosen surface; Keep writes it.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task ConvertAsync()
    {
        if (!AutomaticTerms && Terms < 1) { Status = $"The number of terms must be at least 1, not {Terms}."; return; }
        var system = _system();
        var catalogs = _catalogs();
        var options = new AsphereTypeConversionOptions
        {
            Surface = ConvertSurface, Target = SelectedTarget.Value, Terms = AutomaticTerms ? null : Terms, VaryRadiusAndConic = VaryRadiusAndConic,
        };
        await Run(async token =>
        {
            var result = await Task.Run(() => AsphereTypeConversion.Run(system, catalogs, options), token);
            Offer(result.Settings, "Convert Asphere Types", result.ReportLines(),
                string.Create(CultureInfo.InvariantCulture, $"Surface {result.Surface} converted to {AsphereTypeConversion.Label(result.Target)} ({result.Terms} terms), RMS sag residual {result.RmsResidual:g4}. Keep writes it into the lens."));
        });
    }

    /// <summary>Find Best Asphere over the surface range; Keep writes the best design.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task FindBestAsphereAsync()
    {
        var system = _system();
        var catalogs = _catalogs();
        int last = LastSurface > 0 ? LastSurface : (system.Surfaces?.Count ?? 2) - 2;
        await Run(async token =>
        {
            int run = _run;
            var progress = new Progress<DesignToolProgress>(p => Report(p, run));
            var options = new FindBestAsphereOptions
            {
                FirstSurface = FirstSurface, LastSurface = last, Kind = SelectedKind.Value, MaxOrder = MaxOrder,
                Optimization = Optimization(), CancellationToken = token, Progress = ((IProgress<DesignToolProgress>)progress).Report,
            };
            var result = await Task.Run(() => FindBestAsphere.Run(system, catalogs, options));
            Interlocked.Increment(ref _run);
            string status = result.BestSurface is int b
                ? string.Create(CultureInfo.InvariantCulture, $"Best: an asphere on surface {b}, merit {result.ReoptimizedStartMerit:g6} → {result.BestMerit:g6}. Keep writes it into the lens.")
                : "No surface in the range took an asphere.";
            if (result.Cancelled) status = "Stopped. " + status;
            Offer(result.BestSurface is null ? null : result.Best, "Find Best Asphere", result.ReportLines(), status);
        });
    }

    /// <summary>Reads the stock lens catalog folder (OP-16's library) and lists its catalogs.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void LoadCatalogs()
    {
        _library = LensCatalogLibrary.Open(LibraryFolder, _glassLibrary());
        Vendors.Clear();
        Vendors.Add(AllVendors);
        foreach (var c in _library.Catalogs) Vendors.Add(c.Name);
        SelectedVendor = AllVendors;
        int lenses = _library.Catalogs.Sum(c => c.Lenses.Count);
        Status = $"{_library.Catalogs.Count} catalog{(_library.Catalogs.Count == 1 ? "" : "s")}, {lenses} lens{(lenses == 1 ? "" : "es")} in {LibraryFolder}.";
        Details = string.Join(Environment.NewLine, _library.Problems);
    }

    /// <summary>Stock Lens Matching against the chosen catalogs; Keep writes the best combination.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task MatchStockLensesAsync()
    {
        if (_library is null) LoadCatalogs();
        var vendors = SelectedVendor == AllVendors ? _library!.Catalogs : _library!.Catalogs.Where(c => c.Name == SelectedVendor).ToArray();
        if (vendors.Count == 0) { Status = $"No stock lens catalog in {LibraryFolder}; put vendor .zmx folders or private catalogs there."; return; }
        var system = _system();
        var catalogs = _catalogs();
        await Run(async token =>
        {
            int run = _run;
            var progress = new Progress<DesignToolProgress>(p => Report(p, run));
            var options = new StockLensMatchingOptions
            {
                Surfaces = SelectedSurfaces.Value, ShowMatches = ShowMatches, EflTolerancePercent = EflTolerance, EpdTolerancePercent = EpdTolerance,
                AirThicknessCompensation = AirThicknessCompensation, Optimization = Optimization(), MatchShape = MatchShape, MatchOrientation = MatchOrientation,
                CancellationToken = token, Progress = ((IProgress<DesignToolProgress>)progress).Report,
            };
            var result = await Task.Run(() => StockLensMatching.Run(system, vendors, catalogs, options));
            Interlocked.Increment(ref _run);
            string status = result.BestChoice is { } best
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Best: {string.Join(", ", best.Lenses.Select(m => m.Lens.PartName))}, merit {result.NominalMerit:g6} → {best.Merit:g6}. Keep writes it into the lens.")
                : "No suitable stock lens was found.";
            if (result.Cancelled) status = "Stopped. " + status;
            Offer(result.Best, "Stock Lens Matching", result.ReportLines(), status);
        });
    }

    private void Report(DesignToolProgress p, int run)
    {
        if (!IsRunning || run != Volatile.Read(ref _run)) return;
        Status = string.Create(CultureInfo.InvariantCulture, $"{p.Tool}: {p.Done} of {p.Total} ({p.Step}), best merit {p.BestMerit:g6}…");
    }

    private async Task Run(Func<CancellationToken, Task> body)
    {
        var cts = new CancellationTokenSource();
        _cancel = cts;
        Interlocked.Increment(ref _run);
        IsRunning = true;
        HasResult = false;
        _pending = null;
        Status = "Running…";
        try { await body(cts.Token); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            Status = ex is OperationCanceledException ? "Stopped." : ex.Message;
            Details = "";
        }
        finally
        {
            Interlocked.Increment(ref _run);   // late progress reports of this run are ignored
            IsRunning = false;
            _cancel = null;
            cts.Dispose();
        }
    }

    private bool CanKeep() => !IsRunning && HasResult;

    /// <summary>The help's Keep and Exit: the last result written into the lens (undoable).</summary>
    [RelayCommand(CanExecute = nameof(CanKeep))]
    private void Keep()
    {
        if (_pending is null) return;
        string? refusal;
        try { refusal = _apply(_pending, MultiConfiguration.Variables(_system()).Count > 0, _pendingName ?? "Design tools"); }
        catch (ArgumentException ex) { refusal = ex.Message; }
        if (refusal is not null) { Status = $"Not applied: {refusal}"; return; }
        LastKept = _pendingName;
        Status = $"{_pendingName}: written into the lens. The Lens Data Editor's Undo takes it back.";
        _pending = null;
        HasResult = false;
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cancel?.Cancel();
}
