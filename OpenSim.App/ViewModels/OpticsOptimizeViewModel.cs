using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Optimization;

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
    public OpticsOptimizeViewModel(Func<OpticsSettings> system, Func<IReadOnlyList<OpticalCatalog>> catalogs,
        Func<OpticsSettings, bool, string, string?> apply)
    {
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

    private static string Format(double merit) => merit.ToString("G8", CultureInfo.InvariantCulture);
}
