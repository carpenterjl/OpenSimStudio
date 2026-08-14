using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Rendering;
using OpenSim.App.Services;
using OpenSim.Core.Persistence;
using OpenSim.Core.PostProcessing;

namespace OpenSim.App.ViewModels;

/// <summary>What about the colormap changed — and therefore how much work redisplaying it is.</summary>
public enum ColormapChangeKind
{
    /// <summary>Only the colors moved: the mesh keeps its texture coordinates and the
    /// scene just swaps the gradient brush. Cheap enough to run mid-drag.</summary>
    Colors,

    /// <summary>The value → colormap-axis mapping moved (range, log, clamp): every vertex
    /// texture coordinate must be recomputed, so the scene rebuilds.</summary>
    Range
}

/// <summary>See <see cref="ColormapViewModel.ColormapChanged"/>.</summary>
public sealed class ColormapChangedEventArgs : EventArgs
{
    public required ColormapChangeKind Kind { get; init; }
}

/// <summary>
/// The editable colormap: which map is in use, its stops, and the value range it spans.
/// <para>
/// The two halves are deliberately separate. The MAP (stops, discrete banding) is pure
/// color and changing it costs one brush swap. The RANGE is the
/// <see cref="FieldScale"/> the field is normalized through, and changing it re-colors
/// every vertex. <see cref="ColormapChanged"/> says which happened so the scene can take
/// the cheap path when it is available — that is what makes dragging a stop live.
/// </para>
/// </summary>
public partial class ColormapViewModel : ObservableObject
{
    private readonly ColormapLibrary _library;
    private readonly ILogService _log;

    /// <summary>Guards the property writes made while projecting a new definition into the
    /// editor rows, so re-syncing never looks like a user edit.</summary>
    private bool _syncing;

    public ColormapViewModel(ColormapLibrary library, ILogService log)
    {
        _library = library;
        _log = log;
        foreach (var warning in library.LoadWarnings)
            log.Append(warning);

        Available = new ObservableCollection<ColormapDefinition>(library.Colormaps);
        // Rainbow is the map the app has always opened with; keeping it as the default
        // means enabling the editor changes no existing view.
        _current = Available.FirstOrDefault(m => m.Name == ColormapDefinition.Rainbow.Name)
                   ?? ColormapDefinition.Rainbow;
        _selectedPreset = _current;
        _previewBrush = ColormapBrushFactory.CreateBrush(_current);
        _saveName = _current.Name;
        SyncRows();
    }

    /// <summary>Built-in and user maps, as offered by the preset picker. This list holds
    /// the LIBRARY's maps and changes only on save/delete — never while editing, so the
    /// picker cannot push a transient null selection through its binding mid-edit.</summary>
    public ObservableCollection<ColormapDefinition> Available { get; }

    /// <summary>The stops of <see cref="Current"/>, as editor rows.</summary>
    public ObservableCollection<ColormapStopViewModel> Stops { get; } = new();

    /// <summary>The map picked in the combo. Goes blank once <see cref="Current"/> has been
    /// edited away from it — which both reads as "unsaved edit" and makes re-picking the same
    /// entry work as a revert.</summary>
    [ObservableProperty] private ColormapDefinition? _selectedPreset;

    /// <summary>The colormap actually in use, including unsaved edits.</summary>
    [ObservableProperty] private ColormapDefinition _current;

    [ObservableProperty] private ColormapStopViewModel? _selectedStop;

    /// <summary>Hex color of the selected stop, "#RRGGBB". Committed on change; anything
    /// unparseable is ignored (the box is being typed into), never silently reset.</summary>
    [ObservableProperty] private string _selectedStopHex = "#000000";

    [ObservableProperty] private Brush _previewBrush;

    /// <summary>Banded rather than continuous coloring — one flat color per stop interval.</summary>
    [ObservableProperty] private bool _discrete;

    /// <summary>Name the current map is saved under.</summary>
    [ObservableProperty] private string _saveName;

    // ---------------- The value range (the FieldScale half) ----------------

    /// <summary>Span the data's own range (as the app has always done) rather than the
    /// hand-entered <see cref="UserMin"/>/<see cref="UserMax"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualRange))]
    private bool _autoRange = true;

    /// <summary>Inverse of <see cref="AutoRange"/>, for enabling the manual range boxes.</summary>
    public bool ManualRange => !AutoRange;

    /// <summary>Absolute lower bound of the colormap in FIELD units (K, Pa, V…), not a
    /// fraction: a thermal study is read against real temperatures.</summary>
    [ObservableProperty] private double _userMin;

    [ObservableProperty] private double _userMax = 1;

    /// <summary>Spread the colors over decades instead of linearly — for fields whose
    /// interesting structure is orders of magnitude below the peak.</summary>
    [ObservableProperty] private bool _useLog;

    [ObservableProperty] private int _decades = 3;

    /// <summary>Raised whenever the displayed coloring must change; see
    /// <see cref="ColormapChangeKind"/> for how much work that is.</summary>
    public event EventHandler<ColormapChangedEventArgs>? ColormapChanged;

    /// <summary>
    /// The scale the result view should normalize through, given the data bounds and the
    /// Results panel's clamp slider. Precedence is user range &gt; clamp &gt; auto: a
    /// hand-entered range is an explicit statement about what the colors mean and must not
    /// be quietly re-scaled by a slider the user is not looking at.
    /// <para>
    /// With <see cref="AutoRange"/> on and no log, this is exactly the legacy linear range
    /// (data min → clamped max) — which is what keeps the other workspaces unchanged.
    /// </para>
    /// </summary>
    public FieldScale ResolveScale(double dataMin, double dataMax, double clampFraction)
    {
        var mode = UseLog ? FieldScaleMode.Logarithmic : FieldScaleMode.Linear;
        int decades = Math.Max(1, Decades);
        if (!AutoRange)
            return new FieldScale(mode, UserMin, UserMax, decades);

        double max = dataMin + clampFraction * (dataMax - dataMin);
        return mode == FieldScaleMode.Logarithmic
            ? new FieldScale(mode, max / Math.Pow(10, decades), max, decades)
            : new FieldScale(mode, dataMin, max, decades);
    }

    /// <summary>The gradient brush for the current map.</summary>
    public LinearGradientBrush CreateBrush() => ColormapBrushFactory.CreateBrush(Current);

    /// <summary>
    /// Adopts the data bounds into the manual range boxes. Called when the user turns Auto
    /// off, so the manual range starts where the automatic one ended instead of at a
    /// meaningless 0…1 that would flash the model one flat color.
    /// </summary>
    public void SeedRangeFromData(double dataMin, double dataMax)
    {
        if (!AutoRange || dataMax <= dataMin) return;
        _syncing = true;
        try
        {
            UserMin = dataMin;
            UserMax = dataMax;
        }
        finally { _syncing = false; }
    }

    // ---------------- Editing ----------------

    /// <summary>Moves an interior stop (the gradient strip's drag gesture). Endpoint drags
    /// are ignored rather than throwing: the marker is simply pinned.</summary>
    public void MoveStop(int index, double position)
    {
        if (index <= 0 || index >= Current.Stops.Count - 1) return;
        Apply(Current.WithStopMoved(index, position));
    }

    [RelayCommand]
    private void AddStop()
    {
        // Halfway into the widest gap: the new stop lands where there is room for it, and
        // it takes the color the map already has there, so adding one changes nothing yet.
        int widest = 0;
        double best = -1;
        for (int i = 0; i < Current.Stops.Count - 1; i++)
        {
            double span = Current.Stops[i + 1].Position - Current.Stops[i].Position;
            if (span > best) { best = span; widest = i; }
        }
        double position = 0.5 * (Current.Stops[widest].Position + Current.Stops[widest + 1].Position);
        Apply(Current.WithStopAdded(position));
        SelectedStop = Stops.FirstOrDefault(s => s.Position == position);
    }

    [RelayCommand]
    private void RemoveStop()
    {
        if (SelectedStop is not { IsEndpoint: false } stop)
        {
            _log.Append("Select an interior stop to remove — the end stops are pinned at 0 and 1.");
            return;
        }
        if (Current.Stops.Count <= 2)
        {
            _log.Append("A colormap needs at least two stops.");
            return;
        }
        Apply(Current.WithStopRemoved(stop.Index));
        SelectedStop = null;
    }

    /// <summary>
    /// A fixed swatch row for quick recoloring: the six spectral anchors most colormaps are
    /// built from, plus black/white and the warm/cool pair a diverging map needs. Anything
    /// else goes through the hex box — a full color picker is a named non-goal for v1.
    /// </summary>
    public IReadOnlyList<Brush> Palette { get; } = new[]
    {
        "#000000", "#FFFFFF", "#3B4CC0", "#0000FF", "#00FFFF", "#00FF00",
        "#FFFF00", "#FF7F00", "#FF0000", "#B40426", "#800080", "#808080"
    }.Select(hex =>
    {
        TryParseHex(hex, out byte r, out byte g, out byte b);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return (Brush)brush;
    }).ToArray();

    [RelayCommand]
    private void PickSwatch(Brush? swatch)
    {
        if (swatch is SolidColorBrush { Color: var color })
            SetSelectedStopColor(color.R, color.G, color.B);
    }

    /// <summary>Recolors the selected stop (the swatch buttons and the hex box).</summary>
    public void SetSelectedStopColor(byte r, byte g, byte b)
    {
        if (SelectedStop is not { } stop) return;
        int index = stop.Index;
        Apply(Current.WithStopColor(index, r, g, b));
        SelectedStop = Stops.FirstOrDefault(s => s.Index == index);
    }

    [RelayCommand]
    private void Save()
    {
        string name = SaveName?.Trim() ?? "";
        if (name.Length == 0)
        {
            _log.Append("Give the colormap a name before saving.");
            return;
        }
        try
        {
            var saved = Current.WithName(name);
            _library.AddOrUpdate(saved);
            RefreshAvailable(saved.Name);
            _log.Append($"Colormap '{name}' saved.");
        }
        catch (Exception ex) { _log.Append($"Colormap save failed: {ex.Message}"); }
    }

    [RelayCommand]
    private void Delete()
    {
        try
        {
            _library.Remove(Current.Name);
            _log.Append($"Colormap '{Current.Name}' deleted.");
            RefreshAvailable(ColormapDefinition.Rainbow.Name);
        }
        catch (InvalidOperationException ex) { _log.Append(ex.Message); }
    }

    /// <summary>Re-reads the library into the picker and selects <paramref name="select"/>.</summary>
    private void RefreshAvailable(string select)
    {
        _syncing = true;
        try
        {
            Available.Clear();
            foreach (var map in _library.Colormaps)
                Available.Add(map);
        }
        finally { _syncing = false; }
        var picked = Available.FirstOrDefault(m => m.Name == select) ?? Available[0];
        SelectedPreset = picked;
        Current = picked;
    }

    /// <summary>Installs an edited definition. The picker goes blank because the map in use
    /// is no longer any saved entry.</summary>
    private void Apply(ColormapDefinition edited)
    {
        _syncing = true;
        try { SelectedPreset = null; }
        finally { _syncing = false; }
        Current = edited;
    }

    partial void OnSelectedPresetChanged(ColormapDefinition? value)
    {
        // A null arrives both when an edit blanks the picker and when WPF rebuilds the
        // ItemsSource; either way there is nothing to switch to.
        if (_syncing || value is null) return;
        Current = value;
    }

    partial void OnCurrentChanged(ColormapDefinition value)
    {
        _syncing = true;
        try
        {
            Discrete = value.Discrete;
            SaveName = value.Name;
        }
        finally { _syncing = false; }
        PreviewBrush = ColormapBrushFactory.CreateBrush(value);
        SyncRows();
        Raise(ColormapChangeKind.Colors);
    }

    /// <summary>
    /// Projects <see cref="Current"/> into the editor rows. When the stop COUNT is unchanged
    /// the existing rows are updated in place rather than replaced — see the remark on
    /// <see cref="ColormapStopViewModel"/>: replacing them mid-drag would destroy the thumb
    /// the mouse is captured on.
    /// </summary>
    private void SyncRows()
    {
        if (Stops.Count == Current.Stops.Count)
        {
            for (int i = 0; i < Stops.Count; i++)
                Stops[i].Stop = Current.Stops[i];
            return;
        }

        int keep = SelectedStop?.Index ?? -1;
        Stops.Clear();
        for (int i = 0; i < Current.Stops.Count; i++)
            Stops.Add(new ColormapStopViewModel(i, Current.Stops[i],
                isEndpoint: i == 0 || i == Current.Stops.Count - 1));
        _syncing = true;
        try
        {
            SelectedStop = keep >= 0 && keep < Stops.Count ? Stops[keep] : null;
        }
        finally { _syncing = false; }
    }

    partial void OnSelectedStopChanged(ColormapStopViewModel? value)
    {
        if (value is null) return;
        _syncing = true;
        try { SelectedStopHex = value.Hex; }
        finally { _syncing = false; }
    }

    partial void OnSelectedStopHexChanged(string value)
    {
        if (_syncing) return;
        if (!TryParseHex(value, out byte r, out byte g, out byte b)) return;   // mid-typing
        SetSelectedStopColor(r, g, b);
    }

    partial void OnDiscreteChanged(bool value)
    {
        if (_syncing || value == Current.Discrete) return;
        Apply(Current.WithDiscrete(value));
    }

    partial void OnAutoRangeChanged(bool value) => Raise(ColormapChangeKind.Range);
    partial void OnUserMinChanged(double value) => RaiseRangeIfManual();
    partial void OnUserMaxChanged(double value) => RaiseRangeIfManual();
    partial void OnUseLogChanged(bool value) => Raise(ColormapChangeKind.Range);
    partial void OnDecadesChanged(int value) { if (UseLog) Raise(ColormapChangeKind.Range); }

    private void RaiseRangeIfManual()
    {
        if (_syncing || AutoRange) return;   // seeding the boxes changes nothing on screen
        Raise(ColormapChangeKind.Range);
    }

    private void Raise(ColormapChangeKind kind)
    {
        if (_syncing) return;
        ColormapChanged?.Invoke(this, new ColormapChangedEventArgs { Kind = kind });
    }

    /// <summary>Parses "#RRGGBB" (or "RRGGBB"). Returns false for anything else — a half-typed
    /// value must leave the color alone rather than snap it to black.</summary>
    public static bool TryParseHex(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (text is null) return false;
        var span = text.Trim().AsSpan();
        if (span.Length > 0 && span[0] == '#') span = span[1..];
        if (span.Length != 6) return false;
        return byte.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
               && byte.TryParse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
               && byte.TryParse(span[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }
}
