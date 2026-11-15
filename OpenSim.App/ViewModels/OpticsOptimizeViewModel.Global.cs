using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Optimization.Global;

namespace OpenSim.App.ViewModels;

// OP-58: the Global and Hammer searches in the Optimize window.

/// <summary>One design of the best list (rank 1 is the best).</summary>
public sealed record GlobalDesignRow(int Rank, string Merit, string Constraints, int Trial, string Glasses, GlobalDesign Design);

public sealed partial class OpticsOptimizeViewModel
{
    /// <summary>The seed of both searches: the same seed and settings give the same designs at any number of threads.</summary>
    [ObservableProperty] private long _searchSeed = 1;

    /// <summary>Global: generations after the first population (0: multi-start only).</summary>
    [ObservableProperty] private int _globalGenerations = 10;

    /// <summary>Global: the population; null (empty) is max(8, 2 × variables).</summary>
    [ObservableProperty] private int? _globalPopulation;

    /// <summary>Both: the random curvatures and steps lie within ±this (1/lens unit); null is 3/|EFL| of the lens.</summary>
    [ObservableProperty] private double? _curvatureLimit;

    /// <summary>Both: stop after this many minutes (checked between generations or rounds); null for none.</summary>
    [ObservableProperty] private double? _searchTimeLimitMinutes;

    [ObservableProperty] private int _hammerRounds = 25;
    [ObservableProperty] private int _hammerTrialsPerRound = 8;

    /// <summary>Hammer: each step moves a variable by up to this fraction of its range.</summary>
    [ObservableProperty] private double _hammerStepFraction = 0.1;

    /// <summary>Hammer: with Substitute surfaces, the chance a trial replaces a glass instead.</summary>
    [ObservableProperty] private double _hammerGlassProbability = 0.5;

    [ObservableProperty] private string _searchProgressText = "—";

    /// <summary>The best designs of the last search, best first.</summary>
    public ObservableCollection<GlobalDesignRow> BestDesigns { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyBestDesignCommand))]
    private GlobalDesignRow? _selectedBestDesign;

    /// <summary>The last Global or Hammer search, or null.</summary>
    public GlobalSearchResult? LastSearch { get; private set; }

    partial void OnIsRunningChanged(bool value)
    {
        StartGlobalCommand.NotifyCanExecuteChanged();
        StartHammerCommand.NotifyCanExecuteChanged();
        ApplyBestDesignCommand.NotifyCanExecuteChanged();
    }

    private LocalOptimizationOptions SearchLocalOptions() => new()
    {
        Algorithm = SelectedAlgorithm.Algorithm,
        Cycles = SelectedCycles.Mode == LocalOptimizationCycles.Infinite ? LocalOptimizationCycles.Automatic : SelectedCycles.Mode,
        CycleCount = Math.Max(1, SelectedCycles.Count),
    };

    private TimeSpan? SearchTimeLimit() =>
        SearchTimeLimitMinutes is double m && m > 0 ? TimeSpan.FromMinutes(m) : null;

    /// <summary>Global search: random starts evolved by differential evolution, each locally
    /// optimized; the ten best designs are listed. The lens is not changed until a design is applied.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartGlobalAsync()
    {
        var system = _system();
        var catalogs = _catalogs();
        var local = SearchLocalOptions();
        int? population = GlobalPopulation is int p && p > 0 ? p : null;
        return RunSearchAsync("Global search", (token, progress) => GlobalSearch.Run(system, catalogs, new GlobalSearchOptions
        {
            Local = local, PopulationSize = population, Generations = GlobalGenerations, CurvatureLimit = CurvatureLimit is double c && c > 0 ? c : null,
            Seed = SearchSeed, TimeLimit = SearchTimeLimit(), MaxDegreeOfParallelism = Math.Max(1, Threads),
            CancellationToken = token, Progress = progress,
        }));
    }

    /// <summary>Hammer search: perturb and re-optimize the current design, replacing glasses on
    /// the Substitute surfaces; the ten best designs are listed.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartHammerAsync()
    {
        var system = SystemWithTemplate();
        var catalogs = _catalogs();
        var local = SearchLocalOptions();
        return RunSearchAsync("Hammer search", (token, progress) => HammerSearch.Run(system, catalogs, new HammerSearchOptions
        {
            Local = local, Rounds = HammerRounds, TrialsPerRound = HammerTrialsPerRound, StepFraction = HammerStepFraction,
            GlassChangeProbability = HammerGlassProbability, CurvatureLimit = CurvatureLimit is double c && c > 0 ? c : null,
            Seed = SearchSeed, TimeLimit = SearchTimeLimit(), MaxDegreeOfParallelism = Math.Max(1, Threads),
            CancellationToken = token, Progress = progress,
        }));
    }

    private readonly object _bestGate = new();

    private async Task RunSearchAsync(string name, Func<CancellationToken, Action<GlobalSearchProgress>, GlobalSearchResult> run)
    {
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        var context = SynchronizationContext.Current;
        void Post(Action action) { if (context is null) action(); else context.Post(_ => action(), null); }
        // A context that runs posts on the thread pool (xUnit's, unlike WPF's dispatcher) can run a
        // progress report late, at the same time as the final listing; the gate and the flag keep
        // a late report from interleaving with, or overwriting, the result.
        bool finished = false;
        void Progress(GlobalSearchProgress p) => Post(() =>
        {
            lock (_bestGate)
            {
                if (finished || !IsRunning) return;   // a late report after the run ended
                MeritBeforeText = Format(p.StartMerit);
                if (p.Best.Count > 0) MeritNowText = Format(p.Best[0].Merit);
                SearchProgressText = $"{(p.Kind == GlobalSearchKind.Global ? "Generation" : "Round")} {p.Iteration}: {p.Trials} trials, {p.FailedTrials} failed";
                ShowBest(p.Best);
            }
        });
        IsRunning = true;
        Status = $"{name} running… Stop keeps the designs found so far.";
        SearchProgressText = "Starting…";
        try
        {
            var result = await Task.Run(() => run(token, Progress));
            lock (_bestGate) finished = true;
            LastSearch = result;
            foreach (var line in result.Log) Log.Add(line);
            MeritBeforeText = Format(result.StartMerit);
            if (result.Best.Count > 0) MeritNowText = Format(result.Best[0].Merit);
            SearchProgressText = $"{result.Iterations} {(result.Kind == GlobalSearchKind.Global ? "generation" : "round")}{(result.Iterations == 1 ? "" : "s")}, "
                + $"{result.Trials} trials, {result.FailedTrials} failed";
            lock (_bestGate) ShowBest(result.Best);
            Status = result.Best.Count == 0
                ? $"{name} stopped before any design was found."
                : $"{name} {(result.Cancelled ? "stopped" : "done")}: {result.Best.Count} design{(result.Best.Count == 1 ? "" : "s")} listed, best merit {Format(result.Best[0].Merit)}. "
                  + "Select one and Apply design to write it into the lens.";
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

    private void ShowBest(IReadOnlyList<GlobalDesign> best)
    {
        BestDesigns.Clear();
        for (int i = 0; i < best.Count; i++)
        {
            var d = best[i];
            BestDesigns.Add(new GlobalDesignRow(i + 1, Format(d.Merit), d.ConstraintsMet ? "met" : "not met", d.Trial, string.Join(", ", d.Glasses), d));
        }
        SelectedBestDesign = BestDesigns.Count > 0 ? BestDesigns[0] : null;
    }

    private bool CanApplyBestDesign() => !IsRunning && SelectedBestDesign is not null && LastSearch is not null;

    /// <summary>Writes the selected design into the lens (one undoable edit of the editors).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyBestDesign))]
    private void ApplyBestDesign()
    {
        if (LastSearch is not { } search || SelectedBestDesign is not { } row) return;
        string? refusal;
        try
        {
            refusal = _apply(search.ApplyTo(_system(), row.Design), search.Variables.Any(v => v.Cell is not null),
                search.Kind == GlobalSearchKind.Global ? "Global design" : "Hammer design");
        }
        catch (ArgumentException ex) { refusal = ex.Message; }
        if (refusal is not null) { Status = $"Not applied: {refusal}"; Log.Add(Status); return; }
        _last = null;   // the editors' undo takes the design back
        UndoCommand.NotifyCanExecuteChanged();
        MeritNowText = row.Merit;
        if (search.SubstituteSurfaces.Count > 0) RefreshGlasses();
        Status = $"Design {row.Rank} (merit {row.Merit}, trial {row.Trial.ToString(CultureInfo.InvariantCulture)}) is in the lens; the Lens Data Editor's undo takes it back.";
        Log.Add(Status);
    }
}
