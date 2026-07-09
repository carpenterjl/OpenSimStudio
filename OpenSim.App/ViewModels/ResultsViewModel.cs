using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.App.Services;
using OpenSim.Core.Results;

namespace OpenSim.App.ViewModels;

/// <summary>
/// Result fields plus every post-processing display option (deform scale, colormap,
/// clamp, contours, section plane). Raises <see cref="DisplayOptionsChanged"/> exactly
/// once per user-visible change; <see cref="SceneViewModel"/> rebuilds the scene on it.
/// </summary>
public partial class ResultsViewModel : ObservableObject
{
    public ResultsViewModel(ProjectSession session)
    {
        session.ResultsProduced += (_, e) => SetResults(e);
        session.GeometryReplaced += (_, _) => ClearSilently();
    }

    public ObservableCollection<IResultField> ResultFields { get; } = new();

    [ObservableProperty] private IResultField? _selectedField;

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
            AnalysisType.Electrical => ResultFields.FirstOrDefault(f => f.Name == "Electric potential"),
            AnalysisType.AcElectrical => ResultFields.FirstOrDefault(f => f.Name == "Potential magnitude"),
            AnalysisType.Thermal or AnalysisType.JouleCoupled or AnalysisType.TransientThermal =>
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
        DisplayOptionsChanged?.Invoke(this, new DisplayOptionsChangedEventArgs { Burst = false });
    }

    partial void OnSelectedFieldChanged(IResultField? value)
    {
        if (_suppressDisplayEvents) return;
        // New field ⇒ new scale: reset the clamp without a second, redundant rebuild.
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
