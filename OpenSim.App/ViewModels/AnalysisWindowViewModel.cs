using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Analyses;
using OpenSim.Optics.Analysis;

namespace OpenSim.App.ViewModels;

/// <summary>
/// One open analysis window (OP-9): an analysis, the settings it computes with, its last
/// result, and the commands that update it and move it around the dock. Its computes run
/// through a <see cref="DebouncedRunner{T}"/>, so edits in quick succession compute once and a
/// compute overtaken by an edit is cancelled. The window belongs to
/// <see cref="OpticsAnalysesViewModel"/>, which owns the layout and tells it when the system changes.
/// </summary>
public sealed partial class AnalysisWindowViewModel : ObservableObject
{
    private readonly OpticsAnalysesViewModel _host;
    private readonly DebouncedRunner<OpticsAnalysisResult> _runner;

    internal AnalysisWindowViewModel(OpticsAnalysesViewModel host, IOpticsAnalysis analysis, object settings,
        string title, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _host = host;
        Analysis = analysis;
        _settings = settings;
        _title = title;
        SettingsEditor = AnalysisSettingsEditor.For(settings);
        _runner = new DebouncedRunner<OpticsAnalysisResult>(host.Debounce, delay);
        _runner.Started += (_, _) => { IsComputing = true; Status = "Computing…"; };
        _runner.Completed += (_, result) =>
        {
            IsComputing = false;
            Result = result;
            IsStale = false;
            Status = "Up to date";
            ComputeCount++;
        };
        _runner.Failed += (_, ex) =>
        {
            IsComputing = false;
            Result = null;
            Status = "Failed: " + ex.Message;
            _host.Log($"Analysis '{Title}' failed: {ex.Message}");
        };
        _runner.Cancelled += (_, _) => { IsComputing = false; Status = "Cancelled"; };
    }

    public IOpticsAnalysis Analysis { get; }

    /// <summary>The tab and window caption: the analysis's name, numbered when it is open twice.</summary>
    [ObservableProperty] private string _title;

    /// <summary>The settings the window computes with (an instance of the analysis's settings type).</summary>
    [ObservableProperty] private object _settings;

    /// <summary>The settings form; edits apply with <see cref="ApplySettingsCommand"/>.</summary>
    public AnalysisSettingsEditor SettingsEditor { get; }

    /// <summary>Recompute whenever the system changes. Off, a change marks the window stale and
    /// waits for Update.</summary>
    [ObservableProperty] private bool _autoUpdate = true;

    partial void OnAutoUpdateChanged(bool value)
    {
        if (value && IsStale) Request(immediate: false);
    }

    [ObservableProperty] private bool _isSettingsOpen;

    [ObservableProperty] private bool _isComputing;

    /// <summary>True when the system changed since the result was computed and auto-update is off.</summary>
    [ObservableProperty] private bool _isStale;

    [ObservableProperty] private string _status = "Not computed";

    /// <summary>The last finished compute; null before the first, or after a failure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(Plots))]
    [NotifyPropertyChangedFor(nameof(HasPlots))]
    private OpticsAnalysisResult? _result;

    public string ResultText => Result?.Text ?? "";

    public IReadOnlyList<AnalysisPlot> Plots => Result?.Plots ?? Array.Empty<AnalysisPlot>();

    public bool HasPlots => Plots.Count > 0;

    /// <summary>The settings form's last complaint; empty when the values applied.</summary>
    [ObservableProperty] private string _settingsError = "";

    /// <summary>Finished computes, for tests and the status line.</summary>
    public int ComputeCount { get; private set; }

    // OP-60
    /// <summary>Computes asked for (by a system change with auto-update on, Update, or new
    /// settings), whether or not a later one overtook them; for tests of the update-on-change link.</summary>
    public int RequestCount { get; private set; }

    /// <summary>The latest compute's run, for tests: completes when its result is in.</summary>
    internal Task CurrentRun => _runner.Current;

    /// <summary>Screen bounds while floating, kept by the view so a reload puts the window back.</summary>
    public double? FloatLeft { get; set; }
    public double? FloatTop { get; set; }
    public double? FloatWidth { get; set; }
    public double? FloatHeight { get; set; }

    /// <summary>The system changed: recompute, or mark stale when auto-update is off.</summary>
    internal void OnSystemChanged()
    {
        if (AutoUpdate) Request(immediate: false);
        else
        {
            IsStale = true;
            if (!IsComputing) Status = "Out of date: press Update";
        }
    }

    /// <summary>Starts a compute on the host's current snapshot.</summary>
    internal void Request(bool immediate)
    {
        RequestCount++;   // OP-60
        var context = _host.Snapshot();
        var settings = Settings;
        var analysis = Analysis;
        if (!IsComputing) Status = "Waiting…";
        _runner.Request(token => analysis.Compute(context, settings, token), immediate);
    }

    /// <summary>Stops a compute in progress.</summary>
    internal void Cancel() => _runner.Cancel();

    internal void UpdateDebounce(TimeSpan debounce) => _runner.Debounce = debounce;

    [RelayCommand]
    private void Update() => Request(immediate: true);

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    /// <summary>Builds the settings from the form and recomputes with them.</summary>
    [RelayCommand]
    private void ApplySettings()
    {
        if (!SettingsEditor.TryBuild(Settings, out var built, out var error))
        {
            SettingsError = error ?? "The settings could not be applied.";
            return;
        }
        SettingsError = "";
        Settings = built;
        _host.RememberSettings(this);
        Request(immediate: true);
    }

    /// <summary>Puts the analysis's defaults in the form and recomputes with them.</summary>
    [RelayCommand]
    private void ResetSettings()
    {
        Settings = Analysis.CreateDefaultSettings();
        SettingsEditor.Show(Settings);
        SettingsError = "";
        _host.RememberSettings(this);
        Request(immediate: true);
    }

    /// <summary>Applies new settings from code (a saved project, a script); recomputes.</summary>
    public void SetSettings(object settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GetType() != Analysis.SettingsType)
            throw new ArgumentException($"'{Analysis.Name}' takes {Analysis.SettingsType.Name} settings.", nameof(settings));
        Settings = settings;
        SettingsEditor.Show(settings);
        _host.RememberSettings(this);
        Request(immediate: false);
    }

    [RelayCommand] private void DockRight() => _host.Move(this, DockZone.Right, newPane: false);
    [RelayCommand] private void DockBottom() => _host.Move(this, DockZone.Bottom, newPane: false);
    [RelayCommand] private void SplitRight() => _host.Move(this, DockZone.Right, newPane: true);
    [RelayCommand] private void SplitBottom() => _host.Move(this, DockZone.Bottom, newPane: true);
    [RelayCommand] private void Float() => _host.Move(this, DockZone.Float, newPane: false);
    [RelayCommand] private void Close() => _host.Close(this);
}
