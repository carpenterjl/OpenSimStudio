using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Optimization.TestPlates;

namespace OpenSim.App.ViewModels;

// OP-17: the Test Plate Fitting dialog.

/// <summary>
/// Test Plate Fitting (plan item OP-17; OpticStudio 2025 R2 help, "Test Plate Fitting" and "Test
/// Plate Lists"): the list file (.TPD), the method of fit, the optimization cycles and the test
/// wavelength the fringes are reported at. Show List reads the file and shows it as the Test
/// Plate Lists tool does; Fit runs <see cref="TestPlateFitting.Run"/> on the project's lens off
/// the UI thread and writes the fitted design through the workspace (<c>apply</c>: one step of
/// the Lens Data Editor's undo stack, as the Optimize tool's result is). A stopped fit is not applied.
/// </summary>
public sealed partial class OpticsTestPlateFittingViewModel : ObservableObject
{
    private readonly Func<OpticsSettings> _system;
    private readonly Func<IReadOnlyList<OpticalCatalog>> _catalogs;
    private readonly Func<OpticsSettings, bool, string, string?> _apply;
    private CancellationTokenSource? _cancel;
    private int _run;

    /// <param name="system">The project's optics record now.</param>
    /// <param name="catalogs">The active glass catalogs.</param>
    /// <param name="apply">Writes a record into the workspace as one undoable edit named by the
    /// text (the flag: whether the multi-configuration table changed); returns why it was refused, or null.</param>
    public OpticsTestPlateFittingViewModel(Func<OpticsSettings> system, Func<IReadOnlyList<OpticalCatalog>> catalogs,
        Func<OpticsSettings, bool, string, string?> apply)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _selectedMethod = Methods[0];
    }

    public IReadOnlyList<InteractiveChoice<TestPlateFitMethod>> Methods { get; } = new[]
    {
        TestPlateFitMethod.TryAll, TestPlateFitMethod.BestToWorst, TestPlateFitMethod.WorstToBest,
        TestPlateFitMethod.LongToShort, TestPlateFitMethod.ShortToLong,
    }.Select(m => new InteractiveChoice<TestPlateFitMethod>(TestPlateFitting.Label(m), m)).ToArray();

    /// <summary>The test plate list (.TPD) to read.</summary>
    [ObservableProperty] private string _fileName = "";

    [ObservableProperty] private InteractiveChoice<TestPlateFitMethod> _selectedMethod;

    /// <summary>The help's "# Opt Cycles: Automatic".</summary>
    [ObservableProperty] private bool _automaticCycles = true;

    /// <summary>The cycles of each optimization when not automatic.</summary>
    [ObservableProperty] private int _cycleCount = 5;

    /// <summary>The wavelength the fringes are reported at, µm.</summary>
    [ObservableProperty] private double _testWavelength = new TestPlateFitOptions().TestWavelengthMicrometers;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FitCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowListCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    [ObservableProperty] private string _status = "Fits every variable radius to the nearest plate of a fabricator's list, re-optimizing the other variables after each.";

    /// <summary>The list or the fitting report, one line each.</summary>
    [ObservableProperty] private string _details = "";

    /// <summary>The last fit applied, or null.</summary>
    public TestPlateFitResult? LastResult { get; private set; }

    private bool CanStart() => !IsRunning;

    private TestPlateList ReadList()
    {
        if (string.IsNullOrWhiteSpace(FileName)) throw new FormatException("Choose a test plate list (.tpd) first.");
        if (!File.Exists(FileName)) throw new FormatException($"The test plate list '{FileName}' does not exist.");
        return TestPlateList.Read(FileName);
    }

    /// <summary>Reads the list and shows it as the Test Plate Lists tool does.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void ShowList()
    {
        try
        {
            var list = ReadList();
            Details = string.Join(Environment.NewLine, list.ReportLines());
            Status = $"{list.Plates.Count} plates in {Path.GetFileName(FileName)}.";
        }
        catch (FormatException ex) { Status = ex.Message; Details = ""; }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task FitAsync()
    {
        TestPlateList list;
        try { list = ReadList(); }
        catch (FormatException ex) { Status = ex.Message; Details = ""; return; }
        if (!AutomaticCycles && CycleCount < 1) { Status = $"The number of cycles must be at least 1, not {CycleCount}."; return; }
        var system = _system();
        var catalogs = _catalogs();
        var cts = new CancellationTokenSource();
        _cancel = cts;
        // Progress<T> posts its reports; one arriving after the run ended must not overwrite the result.
        int run = ++_run;
        var progress = new Progress<TestPlateFitProgress>(p =>
        {
            if (!IsRunning || run != Volatile.Read(ref _run)) return;
            Status = string.Create(CultureInfo.InvariantCulture,
                $"{TestPlateFitting.Label(p.Method)}: {p.Remaining} radi{(p.Remaining == 1 ? "us" : "i")} remaining, merit {p.Merit:g8}…");
        });
        var options = new TestPlateFitOptions
        {
            Method = SelectedMethod.Value,
            Optimization = new LocalOptimizationOptions
            {
                Cycles = AutomaticCycles ? LocalOptimizationCycles.Automatic : LocalOptimizationCycles.Fixed,
                CycleCount = Math.Max(1, CycleCount),
            },
            TestWavelengthMicrometers = TestWavelength,
            CancellationToken = cts.Token,
            Progress = ((IProgress<TestPlateFitProgress>)progress).Report,
        };
        IsRunning = true;
        Status = "Optimizing before fitting…";
        try
        {
            var result = await Task.Run(() => TestPlateFitting.Run(system, list, catalogs, options));
            Interlocked.Increment(ref _run);   // from here, late progress reports of this run are ignored
            Details = string.Join(Environment.NewLine, result.ReportLines());
            if (result.Cancelled) { Status = "Stopped: nothing was applied."; return; }
            string? refusal;
            try { refusal = _apply(result.Fitted, MultiConfiguration.Variables(system).Count > 0, "Test Plate Fitting"); }
            catch (ArgumentException ex) { refusal = ex.Message; }
            if (refusal is not null) { Status = $"Not applied: {refusal}"; return; }
            LastResult = result;
            int fitted = result.Steps.Count(s => s.Plate is not null), missed = result.Unmatched.Count;
            string unmatched = missed > 0 ? $", {missed} with no match (still variable)" : "";
            Status = string.Create(CultureInfo.InvariantCulture,
                $"{fitted} radi{(fitted == 1 ? "us" : "i")} fitted ({TestPlateFitting.Label(result.Method)}){unmatched}; merit {result.PreOptimizedMerit:g6} → {result.Merit:g6}. The Lens Data Editor's Undo takes it back.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
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

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cancel?.Cancel();
}
