using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Analyses;
using OpenSim.App.Services;
using OpenSim.Core.Model;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Optics workspace's analysis windows (OP-9, dockable by decision OQ-2): the analyses on
/// offer, the windows open and where they are docked, and their saved form. It listens to the
/// session — a new snapshot of the optical system, or a project opened — and passes the change
/// to every open window, each of which recomputes off the UI thread. It also composes the
/// report page from what the windows show.
/// </summary>
public sealed partial class OpticsAnalysesViewModel : ObservableObject
{
    /// <summary>Default dock sizes, in device-independent pixels.</summary>
    public const double DefaultRightDockWidth = 420, DefaultBottomDockHeight = 280;

    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private readonly OpticsAnalysisRegistry _registry;

    /// <summary>Settings each analysis was last used with, by id, for the next window opened.</summary>
    private readonly Dictionary<string, object> _lastSettings = new(StringComparer.Ordinal);

    /// <summary>Saved settings of analyses this build does not have: written back unchanged.</summary>
    private readonly Dictionary<string, JsonElement> _unknownLastSettings = new(StringComparer.Ordinal);

    /// <summary>Saved windows of analyses this build does not have: not opened, but written back
    /// on save so a file passed through an older build does not lose them.</summary>
    private readonly List<OpticsAnalysisWindowSettings> _unknownWindows = new();

    private TimeSpan _debounce = TimeSpan.FromMilliseconds(250);

    public OpticsAnalysesViewModel(ProjectSession session, ILogService log, OpticsAnalysisRegistry registry)
    {
        _session = session;
        _log = log;
        _registry = registry;
        _selectedToOpen = registry.All.FirstOrDefault();
        Layout.Changed += (_, _) => OnPropertyChanged(nameof(HasWindows));
        session.OpticsChanged += (_, _) => OnSystemChanged();
        session.GeometryReplaced += (_, _) => OnSystemChanged();
    }

    /// <summary>The analyses the Open menu offers, in registry order.</summary>
    public IReadOnlyList<IOpticsAnalysis> Available => _registry.All;

    [ObservableProperty] private IOpticsAnalysis? _selectedToOpen;

    public DockLayout<AnalysisWindowViewModel> Layout { get; } = new();

    /// <summary>Every open window in saved order.</summary>
    public IReadOnlyList<AnalysisWindowViewModel> Windows => Layout.Items.ToList();

    public bool HasWindows => Layout.Items.Any();

    [ObservableProperty] private double _rightDockWidth = DefaultRightDockWidth;
    [ObservableProperty] private double _bottomDockHeight = DefaultBottomDockHeight;

    /// <summary>How long a window waits after a change for the next one before computing.</summary>
    public TimeSpan Debounce
    {
        get => _debounce;
        set
        {
            _debounce = value;
            foreach (var window in Layout.Items) window.UpdateDebounce(value);
        }
    }

    /// <summary>The wait the windows' debounce uses; a test replaces it with a gate.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

    internal void Log(string message) => _log.Append(message);

    /// <summary>The snapshot a compute starts from, taken on the UI thread.</summary>
    internal OpticsAnalysisContext Snapshot() =>
        new(_session.Project.Name, (_session.OpticsSystem ?? new OpticsSettings()) with { Analyses = null });

    /// <summary>Opens the analysis chosen in the panel's picker.</summary>
    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedToOpen is { } analysis) Open(analysis);
    }

    /// <summary>Opens a window on <paramref name="analysis"/> with the settings it was last used
    /// with, docked as a tab of the right zone (or as given), and computes it.</summary>
    public AnalysisWindowViewModel Open(IOpticsAnalysis analysis, DockZone zone = DockZone.Right, bool newPane = false)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var settings = _lastSettings.TryGetValue(analysis.Id, out var last)
            ? OpticsAnalysisSettingsCodec.Clone(analysis, last)
            : analysis.CreateDefaultSettings();
        var window = Create(analysis, settings);
        Layout.Dock(window, zone, newPane);
        window.Request(immediate: true);
        return window;
    }

    /// <summary>Moves a window to another zone: as a tab of the zone's last pane, or in a new pane.</summary>
    public void Move(AnalysisWindowViewModel window, DockZone zone, bool newPane)
    {
        if (!Layout.Contains(window)) return;
        Layout.Dock(window, zone, newPane);
    }

    /// <summary>Makes a window a tab of another window's pane.</summary>
    public void TabWith(AnalysisWindowViewModel window, AnalysisWindowViewModel other)
    {
        if (Layout.PaneOf(other) is { } pane) Layout.TabWith(window, pane);
        else Layout.Dock(window, DockZone.Float);
    }

    /// <summary>Closes a window; a compute in progress is cancelled and its settings remembered.</summary>
    public void Close(AnalysisWindowViewModel window)
    {
        window.Cancel();
        RememberSettings(window);
        Layout.Remove(window);
    }

    /// <summary>Closes every window, as for a new project.</summary>
    public void Reset()
    {
        foreach (var window in Layout.Items.ToList()) window.Cancel();
        Layout.Clear();
        _lastSettings.Clear();
        _unknownLastSettings.Clear();
        _unknownWindows.Clear();
        RightDockWidth = DefaultRightDockWidth;
        BottomDockHeight = DefaultBottomDockHeight;
    }

    internal void RememberSettings(AnalysisWindowViewModel window) =>
        _lastSettings[window.Analysis.Id] = window.Settings;

    /// <summary>Completes when every window's latest compute has reported, for tests.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(Layout.Items.Select(w => w.CurrentRun));

    private void OnSystemChanged()
    {
        foreach (var window in Layout.Items.ToList()) window.OnSystemChanged();
    }

    /// <summary>A new window, titled with the analysis's name, numbered after the windows
    /// already open on it and <paramref name="pending"/> (windows made but not yet docked, while
    /// a project loads).</summary>
    private AnalysisWindowViewModel Create(IOpticsAnalysis analysis, object settings,
        IEnumerable<AnalysisWindowViewModel>? pending = null)
    {
        var open = Layout.Items.Concat(pending ?? Enumerable.Empty<AnalysisWindowViewModel>()).ToList();
        int same = open.Count(w => ReferenceEquals(w.Analysis, analysis));
        string title = same == 0 ? analysis.Name : $"{analysis.Name} ({same + 1})";
        while (open.Any(w => w.Title == title)) title = $"{analysis.Name} ({++same + 1})";
        return new AnalysisWindowViewModel(this, analysis, settings, title, Delay);
    }

    // ---------------- Report ----------------

    /// <summary>The report page: every open window's last result, in window order.</summary>
    public StudyReport ComposeReport()
    {
        var mode = OpticsModes.TryParseSavedName(_session.OpticsSystem?.Mode, out var m) ? m : OpticsModes.All[0];
        var entries = Layout.Items.Select(w => new OpticsAnalysisReportEntry(w.Title, w.Result, w.Status)).ToList();
        return OpticsAnalysisReport.Compose(_session.Project.Name, mode, entries);
    }

    /// <summary>Writes the report page as Markdown or HTML.</summary>
    [RelayCommand]
    private void SaveReport()
    {
        if (!HasWindows)
        {
            _log.Append("Nothing to report: open an analysis first.");
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_session.Project.Name}-optics-report.md",
            Filter = "Markdown report|*.md|HTML report|*.html",
            Title = "Save the optics report"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var report = ComposeReport();
            bool html = dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                        || dialog.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
            System.IO.File.WriteAllText(dialog.FileName, html ? report.ToHtml() : report.ToMarkdown());
            _log.Append($"Optics report saved to {dialog.FileName} ({Layout.Items.Count()} analysis window(s)).");
        }
        catch (Exception ex) { _session.ReportError(ex); }
    }

    // ---------------- Project file ----------------

    /// <summary>The windows, their places and settings as the project file stores them; null
    /// when nothing is open and nothing is remembered, so an untouched project writes nothing.</summary>
    public OpticsAnalysesSettings? ToSettings()
    {
        var windows = new List<OpticsAnalysisWindowSettings>();
        foreach (var (window, placement) in Layout.Placements())
        {
            bool floating = placement.Zone == DockZone.Float;
            windows.Add(new OpticsAnalysisWindowSettings
            {
                Analysis = window.Analysis.Id,
                Dock = placement.Zone.ToString(),
                Pane = floating ? null : placement.Pane,
                Selected = floating ? null : placement.Selected,
                AutoUpdate = window.AutoUpdate ? null : false,
                Settings = OpticsAnalysisSettingsCodec.Save(window.Settings),
                FloatLeft = floating ? window.FloatLeft : null,
                FloatTop = floating ? window.FloatTop : null,
                FloatWidth = floating ? window.FloatWidth : null,
                FloatHeight = floating ? window.FloatHeight : null,
            });
        }
        windows.AddRange(_unknownWindows);

        var last = new Dictionary<string, JsonElement>(_unknownLastSettings, StringComparer.Ordinal);
        foreach (var (id, settings) in _lastSettings) last[id] = OpticsAnalysisSettingsCodec.Save(settings);

        bool defaultSizes = RightDockWidth == DefaultRightDockWidth && BottomDockHeight == DefaultBottomDockHeight;
        if (windows.Count == 0 && last.Count == 0 && defaultSizes) return null;
        return new OpticsAnalysesSettings
        {
            Windows = windows.Count == 0 ? null : windows,
            LastSettings = last.Count == 0 ? null : last,
            RightDockWidth = RightDockWidth == DefaultRightDockWidth ? null : RightDockWidth,
            BottomDockHeight = BottomDockHeight == DefaultBottomDockHeight ? null : BottomDockHeight,
        };
    }

    /// <summary>Reopens the saved windows in their places, each with its saved settings, and
    /// computes them. Null — every older file — closes every window. Returns notes for the log:
    /// an analysis this build lacks is named, kept for the next save, and not opened.</summary>
    public IReadOnlyList<string> LoadSettings(OpticsAnalysesSettings? saved)
    {
        Reset();
        var notes = new List<string>();
        if (saved is null) return notes;

        if (saved.RightDockWidth is { } width && double.IsFinite(width) && width > 0) RightDockWidth = width;
        if (saved.BottomDockHeight is { } height && double.IsFinite(height) && height > 0) BottomDockHeight = height;

        foreach (var (id, element) in saved.LastSettings ?? new Dictionary<string, JsonElement>())
        {
            if (_registry.TryGet(id, out var analysis))
            {
                _lastSettings[id] = OpticsAnalysisSettingsCodec.Load(analysis, element, out var note);
                if (note is not null) notes.Add(note);
            }
            else _unknownLastSettings[id] = element.Clone();
        }

        var placements = new List<(AnalysisWindowViewModel, DockPlacement)>();
        foreach (var entry in saved.Windows ?? new List<OpticsAnalysisWindowSettings>())
        {
            if (!_registry.TryGet(entry.Analysis, out var analysis))
            {
                notes.Add($"Analysis '{entry.Analysis}' is not one this version knows; its window is kept in the file but not opened.");
                _unknownWindows.Add(entry with { Settings = entry.Settings?.Clone() });
                continue;
            }
            var settings = OpticsAnalysisSettingsCodec.Load(analysis, entry.Settings, out var note);
            if (note is not null) notes.Add(note);
            var window = Create(analysis, settings, placements.Select(p => p.Item1));
            window.AutoUpdate = entry.AutoUpdate ?? true;
            window.FloatLeft = entry.FloatLeft;
            window.FloatTop = entry.FloatTop;
            window.FloatWidth = entry.FloatWidth;
            window.FloatHeight = entry.FloatHeight;
            var zone = Enum.TryParse<DockZone>(entry.Dock, ignoreCase: false, out var z) && Enum.IsDefined(z) ? z : DockZone.Bottom;
            placements.Add((window, new DockPlacement(zone, Math.Max(0, entry.Pane ?? 0), entry.Selected ?? false)));
        }
        Layout.Restore(placements);
        foreach (var (window, _) in placements) window.Request(immediate: true);
        return notes;
    }
}
