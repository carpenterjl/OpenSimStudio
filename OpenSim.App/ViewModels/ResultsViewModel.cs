using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Model;
using OpenSim.Core.Results;

namespace OpenSim.App.ViewModels;

/// <summary>
/// Result fields plus every post-processing display option (deform scale, colormap,
/// clamp, contours, section plane) and the playback timeline over a multi-frame solve.
/// Raises <see cref="DisplayOptionsChanged"/> exactly once per user-visible change;
/// <see cref="SceneViewModel"/> rebuilds the scene on it.
/// </summary>
public partial class ResultsViewModel : ObservableObject
{
    private readonly ProjectSession _session;

    public ResultsViewModel(ProjectSession session)
    {
        _session = session;
        session.ResultsProduced += (_, e) => SetResults(e);
        session.GeometryReplaced += (_, _) => ClearSilently();
        session.PropertyChanged += (_, e) =>
        {
            // The timeline is part of one workspace's viewport. Navigating away with the
            // head running would keep animating — and rebuilding — a scene the user can no
            // longer pause, so leaving the view stops playback.
            if (e.PropertyName is nameof(ProjectSession.ActiveWorkspace)
                                or nameof(ProjectSession.IsHomeActive))
                StopPlayback();
        };
        _playTimer.Tick += (_, _) => AdvancePlayback();
    }

    public ObservableCollection<IResultField> ResultFields { get; } = new();

    [ObservableProperty] private IResultField? _selectedField;

    /// <summary>
    /// The tensor components and invariants offered when the selected field is a tensor.
    /// The default is von Mises, which is what the field showed before components existed.
    /// </summary>
    public IReadOnlyList<TensorComponentOption> TensorComponents { get; } = TensorComponentOption.All;

    [ObservableProperty] private TensorComponentOption _selectedComponent = TensorComponentOption.All[0];

    /// <summary>The component picker only appears for a field that HAS components.</summary>
    public bool HasTensorComponents => SelectedField is ElementTensorField;

    /// <summary>
    /// What the viewport actually renders: the selected field, or a component view of it
    /// when the user picked one. Kept separate from <see cref="SelectedField"/> so the list
    /// selection, the frame-preserving lookup by name and the default-field rule all keep
    /// working on the field the solver emitted.
    /// </summary>
    public IResultField? DisplayField =>
        SelectedField is ElementTensorField tensor ? tensor.View(SelectedComponent.Component) : SelectedField;

    /// <summary>Min / max / average of the displayed field, both averages named.</summary>
    [ObservableProperty] private string _fieldStatistics = "";

    private void RefreshStatistics()
    {
        var field = DisplayField;
        var mesh = _session.AssembledMesh?.Mesh ?? _session.Body.Mesh;
        if (field is null || mesh is null || field.Count == 0)
        {
            FieldStatistics = "";
            return;
        }
        try
        {
            var s = OpenSim.Core.PostProcessing.FieldStatistics.Compute(field, mesh);
            FieldStatistics =
                $"min {Format(s.Min, field.Unit)}   max {Format(s.Max, field.Unit)}\n" +
                $"average {Format(s.Mean, field.Unit)} over {s.Count} {(field.Location == FieldLocation.Node ? "nodes" : "elements")}, " +
                $"volume-weighted {Format(s.VolumeWeightedMean, field.Unit)}";
        }
        catch (Exception)
        {
            // A field whose length does not match the displayed mesh (a stale frame during
            // a rebuild) is not worth an error dialog — just show nothing until it settles.
            FieldStatistics = "";
        }
    }

    private static string Format(double value, string unit) => $"{value:g4} {unit}".TrimEnd();

    /// <summary>
    /// The context every export carries in its preamble, so a CSV read later still says what
    /// was solved. Assembled here because only the app knows the project and analysis.
    /// </summary>
    private ResultReportContext BuildReportContext() => new()
    {
        ProjectName = _session.Project.Name,
        BodyName = _session.AssembledMesh is null ? _session.Body.Name : "assembly",
        Analysis = _session.SelectedAnalysis.Label,
        Material = _session.AssembledMesh is null ? _session.Body.Material : null,
        BoundaryConditions = _session.AssembledMesh?.BoundaryConditions
                             ?? (IReadOnlyList<BoundaryCondition>)_session.Body.BoundaryConditions,
        FrameLabel = HasFrames ? SelectedFrameLabel : null,
        Notes = new[]
        {
            "safety factor is capped at 15 where the stress vanishes",
            "no timestamp: this export is a deterministic function of the solve"
        }
    };

    private OpenSim.Core.Model.FeMesh? ReportMesh =>
        _session.AssembledMesh?.Mesh ?? _session.Body.Mesh;

    /// <summary>Exports one row per field: min, max and both averages — the report table.</summary>
    [RelayCommand]
    private void ExportSummaryCsv()
    {
        if (ReportMesh is not { } mesh || ResultFields.Count == 0)
        {
            _session.StatusText = "Solve first — there are no results to export.";
            return;
        }
        SaveCsv("result-summary.csv", "Export result summary (CSV)",
            () => ResultReportCsv.WriteSummary(ResultFields, mesh, BuildReportContext()),
            $"{ResultFields.Count} field(s)");
    }

    /// <summary>Exports every value of the displayed field, with its node/element position.</summary>
    [RelayCommand]
    private void ExportFieldCsv()
    {
        if (ReportMesh is not { } mesh || DisplayField is not { } field)
        {
            _session.StatusText = "Select a result field to export.";
            return;
        }
        SaveCsv("result-field.csv", $"Export {field.Name} (CSV)",
            () => ResultReportCsv.WriteField(field, mesh, BuildReportContext()),
            $"{field.Count} value(s) of '{field.Name}'");
    }

    private void SaveCsv(string suggestedName, string title, Func<string> render, string what)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, render(), System.Text.Encoding.UTF8);
            _session.StatusText = $"Exported {what} to {System.IO.Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            _session.ReportError(ex);
        }
    }

    // Multi-frame results (time steps / modes / frequency points)
    public ObservableCollection<ResultFrame> Frames { get; } = new();

    [ObservableProperty] private int _selectedFrameIndex;

    [ObservableProperty] private string _frameAxis = "Frame";

    /// <summary>The frame picker only appears when there is something to scrub.</summary>
    public bool HasFrames => Frames.Count > 1;

    /// <summary>Upper bound for the frame slider.</summary>
    public int FrameMaxIndex => Math.Max(0, Frames.Count - 1);

    public string SelectedFrameLabel =>
        SelectedFrameIndex >= 0 && SelectedFrameIndex < Frames.Count
            ? Frames[SelectedFrameIndex].Label
            : string.Empty;

    // ---------------- Timeline ----------------
    //
    // The timeline drags a continuous AXIS VALUE (seconds, hertz), not a frame index: a
    // transient solve whose step changes mid-run has frames that are not evenly spaced, so
    // an index-proportional thumb would move at a rate unrelated to the physics. The frame
    // index stays the single source of truth for what is rendered — the time is mapped onto
    // it by FrameTimeline, and the two are kept in sync in both directions so the older
    // index scrubber in the Results panel keeps working unchanged.

    /// <summary>Wall-clock seconds one 1x pass over the whole axis takes.</summary>
    private const double SweepSeconds = 5;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    /// <summary>Real elapsed time between playback ticks. Playback advances by measured
    /// wall-clock, not by the nominal interval, so a scene rebuild that overruns a tick
    /// drops frames instead of playing the transient in slow motion.</summary>
    private readonly System.Diagnostics.Stopwatch _playClock = new();

    /// <summary>Frame axis values, ascending — the timeline's coordinate system.</summary>
    private double[] _frameValues = Array.Empty<double>();

    /// <summary>Guards the time to index to time round trip against re-entry.</summary>
    private bool _syncingTime;

    private bool _renderQueued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTimeLabel))]
    private double _currentFrameTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonLabel))]
    private bool _isPlaying;

    [ObservableProperty] private double _playSpeed = 1;
    [ObservableProperty] private bool _loopPlayback = true;

    /// <summary>Playback rates as multiples of the 5-second full-axis sweep.</summary>
    public IReadOnlyList<double> PlaySpeedOptions { get; } = new[] { 0.25, 0.5, 1.0, 2.0, 4.0 };

    /// <summary>Axis unit of <see cref="ResultFrame.Value"/> ("s", "Hz"); null when the
    /// axis is dimensionless (mode number) or when the frames carry no unit.</summary>
    public string? FrameUnit { get; private set; }

    public double FrameTimeMin => _frameValues.Length > 0 ? _frameValues[0] : 0;
    public double FrameTimeMax => _frameValues.Length > 0 ? _frameValues[^1] : 0;

    /// <summary>Slider tick positions: the frames themselves. With snap-to-tick this gives
    /// a time-proportional thumb AND exact frame landing in one property — the dragged
    /// value is always a real frame value, so nothing has to be rounded back afterwards.</summary>
    public DoubleCollection FrameTicks { get; private set; } = new();

    public IReadOnlyList<TimelineTickMark> TimelineTickLabels { get; private set; } =
        Array.Empty<TimelineTickMark>();

    public string CurrentTimeLabel => FrameTimeline.Format(CurrentFrameTime, FrameUnit);

    public string PlayButtonLabel => IsPlaying ? "❚❚" : "▶";

    /// <summary>The timeline needs an axis with real extent. Frames that all share one
    /// value (a degenerate axis) keep the index scrubber and hide the timeline rather than
    /// showing a slider whose ends coincide.</summary>
    public bool HasTimeline => _frameValues.Length > 1 && FrameTimeMax > FrameTimeMin;

    /// <summary>Starts or stops playback. Pressing play while parked at the end replays
    /// from the start instead of doing nothing.</summary>
    [RelayCommand]
    private void TogglePlay()
    {
        if (IsPlaying) { StopPlayback(); return; }
        if (!HasTimeline) return;
        if (CurrentFrameTime >= FrameTimeMax) CurrentFrameTime = FrameTimeMin;
        IsPlaying = true;
        _playClock.Restart();
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        _playTimer.Stop();
        _playClock.Reset();
        IsPlaying = false;
    }

    private void AdvancePlayback()
    {
        double elapsed = _playClock.Elapsed.TotalSeconds;
        _playClock.Restart();
        if (!HasTimeline) { StopPlayback(); return; }

        double delta = (FrameTimeMax - FrameTimeMin) * PlaySpeed * elapsed / SweepSeconds;
        CurrentFrameTime = FrameTimeline.Advance(CurrentFrameTime, delta,
            FrameTimeMin, FrameTimeMax, LoopPlayback, out bool wrapped);
        if (wrapped && !LoopPlayback) StopPlayback();
    }

    /// <summary>Rebuilds the timeline for the current frame set. Called whenever the frame
    /// collection changes, which also ends any playback of the frames that just went away.</summary>
    private void RebuildTimeline()
    {
        StopPlayback();
        _frameValues = Frames.Select(f => f.Value).ToArray();
        FrameUnit = Frames.Count > 0 ? Frames[0].Unit : null;

        // Frames are emitted in axis order by contract. If a solver ever breaks that, fall
        // back to a plain index axis instead of throwing out of a property setter: the
        // labels visibly become frame numbers, which is a legible symptom, and the scrubber
        // keeps working.
        for (int i = 1; i < _frameValues.Length; i++)
            if (_frameValues[i] < _frameValues[i - 1])
            {
                _frameValues = Enumerable.Range(0, Frames.Count).Select(v => (double)v).ToArray();
                FrameUnit = null;
                break;
            }

        FrameTicks = new DoubleCollection(_frameValues);
        FrameTicks.Freeze();
        TimelineTickLabels = FrameTimeline.TickLabels(_frameValues, FrameUnit)
            .Select(t => TimelineTickMark.At(t.Fraction, t.Label))
            .ToArray();

        _syncingTime = true;
        try
        {
            CurrentFrameTime = SelectedFrameIndex >= 0 && SelectedFrameIndex < _frameValues.Length
                ? _frameValues[SelectedFrameIndex]
                : 0;
        }
        finally { _syncingTime = false; }

        OnPropertyChanged(nameof(FrameUnit));
        OnPropertyChanged(nameof(FrameTimeMin));
        OnPropertyChanged(nameof(FrameTimeMax));
        OnPropertyChanged(nameof(FrameTicks));
        OnPropertyChanged(nameof(TimelineTickLabels));
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(CurrentTimeLabel));
    }

    partial void OnCurrentFrameTimeChanged(double value)
    {
        if (_syncingTime || _frameValues.Length == 0) return;
        int index = FrameTimeline.NearestFrameIndex(_frameValues, value);
        if (index == SelectedFrameIndex) return;
        // The flag keeps the index change from snapping the time back under a live drag.
        _syncingTime = true;
        try { SelectedFrameIndex = index; }
        finally { _syncingTime = false; }
    }

    /// <summary>Renders the newly selected frame immediately, but at most once per
    /// dispatcher pass. Scrubbing selects frames far faster than the scene can rebuild;
    /// routing it through the 140 ms burst debounce instead would show nothing until the
    /// drag stopped, which is the opposite of what a timeline is for.</summary>
    private void QueueFrameRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _renderQueued = false;
            DisplayOptionsChanged?.Invoke(this, new DisplayOptionsChangedEventArgs { Burst = false });
        }));
    }

    // Result display
    [ObservableProperty] private double _deformScale = 1;
    [ObservableProperty] private bool _useViridis;

    /// <summary>Fraction of [min, max] used as the colormap maximum. Dragging below 1
    /// spends the whole gradient on the low range (values above saturate at the top
    /// color) so fine gradients survive a single hotspot. Resets on field change.</summary>
    [ObservableProperty] private double _resultClampFraction = 1.0;

    // Contours + section plane (post-processing overlays)
    [ObservableProperty] private bool _showContours;
    [ObservableProperty] private bool _sectionEnabled;
    [ObservableProperty] private OpenSim.Core.PostProcessing.SectionAxis _sectionAxis;
    [ObservableProperty] private double _sectionOffsetFraction = 0.5;
    [ObservableProperty] private bool _sectionFlip;

    public IReadOnlyList<OpenSim.Core.PostProcessing.SectionAxis> SectionAxisOptions { get; } = new[]
    {
        OpenSim.Core.PostProcessing.SectionAxis.X,
        OpenSim.Core.PostProcessing.SectionAxis.Y,
        OpenSim.Core.PostProcessing.SectionAxis.Z
    };

    /// <summary>Raised when the scene must re-render (field or display option changed).
    /// <see cref="DisplayOptionsChangedEventArgs.Burst"/> marks slider-driven changes
    /// that arrive in rapid bursts and may be debounced; selection changes are not.</summary>
    public event EventHandler<DisplayOptionsChangedEventArgs>? DisplayOptionsChanged;

    /// <summary>Guards property writes that must not trigger a scene rebuild (clearing
    /// on geometry replacement, the clamp reset piggybacking on a field change).</summary>
    private bool _suppressDisplayEvents;

    private void SetResults(ResultsProducedEventArgs e)
    {
        _suppressDisplayEvents = true;
        try
        {
            Frames.Clear();
            if (e.Frames is { Count: > 1 })
                foreach (var frame in e.Frames)
                    Frames.Add(frame);
            FrameAxis = e.FrameAxis ?? "Frame";

            // Select the frame whose fields the solver designated as the default
            // (SolveOutput.Fields is the default frame's field list, by convention).
            int defaultIndex = 0;
            for (int i = 0; i < Frames.Count; i++)
                if (ReferenceEquals(Frames[i].Fields, e.Fields)) { defaultIndex = i; break; }
            SelectedFrameIndex = defaultIndex;

            ResultFields.Clear();
            foreach (var field in e.Fields)
                ResultFields.Add(field);
        }
        finally { _suppressDisplayEvents = false; }
        NotifyFrameShapeChanged();
        SelectedField = PickDefault(e);
    }

    private void NotifyFrameShapeChanged()
    {
        RebuildTimeline();
        OnPropertyChanged(nameof(HasFrames));
        OnPropertyChanged(nameof(FrameMaxIndex));
        OnPropertyChanged(nameof(SelectedFrameLabel));
    }

    private IResultField? PickDefault(ResultsProducedEventArgs e)
    {
        IResultField? preferred = e.PreferFieldName is null
            ? null
            : ResultFields.FirstOrDefault(f => f.Name == e.PreferFieldName);
        preferred ??= e.Analysis switch
        {
            AnalysisType.Static => ResultFields.FirstOrDefault(f => f.Name.Contains("Mises")),
            AnalysisType.Modal => ResultFields.FirstOrDefault(f => f.Name == "Mode shape"),
            AnalysisType.Electrical or AnalysisType.Electrostatic
                => ResultFields.FirstOrDefault(f => f.Name == "Electric potential"),
            AnalysisType.AcElectrical => ResultFields.FirstOrDefault(f => f.Name == "Potential magnitude"),
            AnalysisType.Thermal or AnalysisType.JouleCoupled or AnalysisType.TransientThermal
                or AnalysisType.EnvironmentThermal =>
                ResultFields.FirstOrDefault(f => f.Name == "Temperature"),
            _ => null
        };
        return preferred ?? ResultFields.FirstOrDefault();
    }

    /// <summary>Drops all results without raising <see cref="DisplayOptionsChanged"/> —
    /// used when the geometry is replaced, where the scene rebuilds anyway and a second
    /// build would be wasted.</summary>
    private void ClearSilently()
    {
        _suppressDisplayEvents = true;
        try
        {
            Frames.Clear();
            ResultFields.Clear();
            SelectedField = null;
            ResultClampFraction = 1.0;
        }
        finally { _suppressDisplayEvents = false; }
        NotifyFrameShapeChanged();
    }

    partial void OnSelectedFrameIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedFrameLabel));
        if (_suppressDisplayEvents || Frames.Count == 0) return;
        if (value < 0 || value >= Frames.Count)
        {
            // A frame ComboBox ItemsSource swap pushes a transient -1 through the
            // two-way SelectedIndex binding — coerce back, never render "no frame".
            SelectedFrameIndex = Math.Clamp(value, 0, Frames.Count - 1);
            return;
        }

        // Swap the field list to the picked frame, preserving the selected field by
        // name (frames of one solve carry identical field sets, by contract). The
        // clamp fraction is intentionally kept: scrubbing shouldn't undo a zoom-in.
        _suppressDisplayEvents = true;
        try
        {
            string? keepName = SelectedField?.Name;
            ResultFields.Clear();
            foreach (var field in Frames[value].Fields)
                ResultFields.Add(field);
            SelectedField = (keepName is null
                    ? null
                    : ResultFields.FirstOrDefault(f => f.Name == keepName))
                ?? ResultFields.FirstOrDefault();
        }
        finally { _suppressDisplayEvents = false; }

        // Keep the timeline's playback head on the frame that is actually shown, unless the
        // timeline is what moved it (a live drag owns the thumb position between frames).
        if (!_syncingTime && value < _frameValues.Length)
        {
            _syncingTime = true;
            try { CurrentFrameTime = _frameValues[value]; }
            finally { _syncingTime = false; }
        }
        QueueFrameRender();
    }

    partial void OnSelectedFieldChanged(IResultField? value)
    {
        OnPropertyChanged(nameof(HasTensorComponents));
        OnPropertyChanged(nameof(DisplayField));
        RefreshStatistics();
        if (_suppressDisplayEvents) return;
        // New field ⇒ new scale: reset the clamp without a second, redundant rebuild.
        _suppressDisplayEvents = true;
        ResultClampFraction = 1.0;
        _suppressDisplayEvents = false;
        DisplayOptionsChanged?.Invoke(this, new DisplayOptionsChangedEventArgs { Burst = false });
    }

    partial void OnSelectedComponentChanged(TensorComponentOption value)
    {
        OnPropertyChanged(nameof(DisplayField));
        RefreshStatistics();
        if (_suppressDisplayEvents || SelectedField is not ElementTensorField) return;
        _suppressDisplayEvents = true;
        ResultClampFraction = 1.0;
        _suppressDisplayEvents = false;
        DisplayOptionsChanged?.Invoke(this, new DisplayOptionsChangedEventArgs { Burst = false });
    }

    private void RaiseIfFieldShown()
    {
        if (!_suppressDisplayEvents && SelectedField is not null)
            DisplayOptionsChanged?.Invoke(this, new DisplayOptionsChangedEventArgs { Burst = true });
    }

    partial void OnResultClampFractionChanged(double value) => RaiseIfFieldShown();
    partial void OnDeformScaleChanged(double value) => RaiseIfFieldShown();
    partial void OnUseViridisChanged(bool value) => RaiseIfFieldShown();
    partial void OnShowContoursChanged(bool value) => RaiseIfFieldShown();
    partial void OnSectionEnabledChanged(bool value) => RaiseIfFieldShown();
    partial void OnSectionAxisChanged(OpenSim.Core.PostProcessing.SectionAxis value) => RaiseIfFieldShown();
    partial void OnSectionOffsetFractionChanged(double value) => RaiseIfFieldShown();
    partial void OnSectionFlipChanged(bool value) => RaiseIfFieldShown();
}

/// <summary>See <see cref="ResultsViewModel.DisplayOptionsChanged"/>.</summary>
public sealed class DisplayOptionsChangedEventArgs : EventArgs
{
    /// <summary>True for slider-driven changes (clamp/deform/section drag) that fire
    /// once per tick — the scene may debounce them. Field/frame selection is false and
    /// renders immediately.</summary>
    public bool Burst { get; init; }
}
