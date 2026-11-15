using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Sequential;

namespace OpenSim.App.ViewModels;

// OP-60: the Slider and the Visual Optimizer.

/// <summary>A surface value the Slider can drag (OpticStudio 2025 R2 help, "Slider", Parameter:
/// "radius, curvature, thickness, conic, and parameter values").</summary>
public sealed record SliderParameter(string Label, LensVariableKind Kind, string Column, bool AsRadius = false);

/// <summary>What the Slider and the Visual Optimizer share: the default range and the step of Animate.</summary>
public static class InteractiveRanges
{
    /// <summary>Animate's steps across the range (this program's choice; the help gives none).</summary>
    public const int StepsPerSweep = 50;

    /// <summary>The range a slider opens with (this program's choice; the help gives none): the
    /// value ± 10% of its size, or ± 1 (± 0.01 for a curvature, per lens unit) when it is 0.</summary>
    public static (double Start, double Stop) Default(double value, LensVariableKind kind)
    {
        double half = value != 0 ? 0.1 * Math.Abs(value) : kind == LensVariableKind.Curvature ? 0.01 : 1;
        return (value - half, value + half);
    }

    /// <summary>The next value of Animate: one step up the range, back to the start past the stop.</summary>
    public static double Next(double value, double start, double stop)
    {
        double step = (stop - start) / StepsPerSweep;
        double next = value + step;
        return (step >= 0 ? next > stop + 1e-9 * Math.Abs(step) : next < stop - 1e-9 * Math.Abs(step)) ? start : next;
    }

    public static string Format(double value) => value.ToString("g8", CultureInfo.InvariantCulture);
}

/// <summary>
/// The Slider (plan item OP-60; OpticStudio 2025 R2 help, "Slider"): drags one surface value
/// between a start and a stop while the open analysis windows update. "When the slider control
/// is activated, a copy is made of the data to be modified": here each move publishes a trial
/// record (the editors' record with the value changed) to the session (<c>preview</c>), so every
/// analysis window with auto-update on recomputes through its debounce, and the editors keep the
/// original. Save ("Keep") writes the value into the lens through the workspace as one undoable
/// edit; Exit, or choosing another surface or parameter, publishes the editors' record again,
/// which restores the original ("the old data is automatically restored, unless the Save button
/// has been pressed").
/// </summary>
public sealed partial class OpticsSliderViewModel : ObservableObject
{
    private readonly Func<OpticsSettings> _system;
    private readonly Action<OpticsSettings> _preview;
    private readonly Func<OpticsSettings, bool, string, string?> _apply;
    private bool _loading;

    /// <param name="system">The editors' record now.</param>
    /// <param name="preview">Publishes a trial record to the session without changing the editors.</param>
    /// <param name="apply">Writes a record into the workspace as one undoable edit; returns why it was refused, or null.</param>
    public OpticsSliderViewModel(Func<OpticsSettings> system, Action<OpticsSettings> preview, Func<OpticsSettings, bool, string, string?> apply)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        var list = new List<SliderParameter>
        {
            new("Radius", LensVariableKind.Curvature, SavedSurfaces.RadiusColumn, AsRadius: true),
            new("Curvature", LensVariableKind.Curvature, SavedSurfaces.RadiusColumn),
            new("Thickness", LensVariableKind.Thickness, SavedSurfaces.ThicknessColumn),
            new("Conic", LensVariableKind.Conic, SavedSurfaces.ConicColumn),
        };
        for (int n = 1; n <= 8; n++) list.Add(new($"Par {n}", LensVariableKind.Parameter, SavedSurfaces.ParameterColumn(n)));
        Parameters = list;
        _selectedParameter = list[2];
    }

    public IReadOnlyList<SliderParameter> Parameters { get; }

    [ObservableProperty] private int _surface = 1;
    [ObservableProperty] private SliderParameter _selectedParameter;
    [ObservableProperty] private double _start;
    [ObservableProperty] private double _stop;
    [ObservableProperty] private double _value;
    [ObservableProperty] private bool _isAnimating;
    [ObservableProperty] private string _status = "Choose a surface and a parameter, then Load.";

    /// <summary>True when a cell was read and the slider drives it.</summary>
    [ObservableProperty] private bool _hasCell;

    /// <summary>True while a trial value is published and not yet kept.</summary>
    public bool IsModified { get; private set; }

    partial void OnSurfaceChanged(int value) => Load();
    partial void OnSelectedParameterChanged(SliderParameter value) { if (value is not null) Load(); }

    partial void OnValueChanged(double value)
    {
        if (!_loading) Preview();
    }

    /// <summary>Restores any trial value, then reads the chosen cell and sets the default range.</summary>
    [RelayCommand]
    public void Load()
    {
        Restore();
        IsAnimating = false;
        try
        {
            double v = Read(_system());
            var (start, stop) = InteractiveRanges.Default(v, SelectedParameter.Kind);
            _loading = true;
            Start = start;
            Stop = stop;
            Value = v;
            HasCell = true;
            Status = $"Surface {Surface} {SelectedParameter.Label.ToLowerInvariant()}: {InteractiveRanges.Format(v)}. Drag to try values; Keep writes the value into the lens.";
        }
        catch (ArgumentException ex)
        {
            HasCell = false;
            Status = ex.Message;
        }
        finally { _loading = false; }
    }

    /// <summary>Saves the current value in the lens (one undoable edit) and keeps the slider open
    /// on it: the help's Save, and the first half of Keep and Exit.</summary>
    [RelayCommand]
    public void Keep()
    {
        if (!HasCell) return;
        string? refusal;
        try { refusal = _apply(Write(_system(), Value), false, $"Slider: surface {Surface} {SelectedParameter.Label.ToLowerInvariant()}"); }
        catch (ArgumentException ex) { refusal = ex.Message; }
        if (refusal is not null) { Status = $"Not kept: {refusal}"; return; }
        IsModified = false;
        Status = $"Kept: surface {Surface} {SelectedParameter.Label.ToLowerInvariant()} = {InteractiveRanges.Format(Value)}. The Lens Data Editor's Undo takes it back.";
    }

    /// <summary>Puts the original back (the help's Exit, before the window closes).</summary>
    [RelayCommand]
    public void Exit()
    {
        IsAnimating = false;
        Restore();
    }

    [RelayCommand]
    private void Animate() => IsAnimating = !IsAnimating && HasCell;

    /// <summary>One step of Animate (the window's timer calls it while <see cref="IsAnimating"/>).</summary>
    public void Step()
    {
        if (HasCell) Value = InteractiveRanges.Next(Value, Start, Stop);
    }

    private void Preview()
    {
        if (!HasCell) return;
        try
        {
            _preview(Write(_system(), Value));
            IsModified = true;
        }
        catch (ArgumentException ex) { Status = ex.Message; }
    }

    private void Restore()
    {
        if (!IsModified) return;
        IsModified = false;
        _preview(_system());
    }

    private OpticsSurfaceSettings SurfaceOf(OpticsSettings settings)
    {
        int count = settings.Surfaces?.Count ?? 0;
        if (Surface < 0 || Surface >= count)
            throw new ArgumentException($"Slider: surface {Surface} is not one of the surfaces 0 to {count - 1}.");
        return settings.Surfaces![Surface];
    }

    private LensVariable Variable => new()
    {
        Kind = SelectedParameter.Kind, Surface = Surface, Column = SelectedParameter.Column,
        Label = $"Surface {Surface} {SelectedParameter.Label.ToLowerInvariant()}",
    };

    private double Read(OpticsSettings settings)
    {
        var s = SurfaceOf(settings);
        var p = SelectedParameter;
        if (p.AsRadius) return s.Radius ?? throw new ArgumentException($"Slider: surface {Surface} is a plane, which has no finite radius; drag its curvature instead.");
        if (p.Kind == LensVariableKind.Thickness && s.ThicknessIsInfinite)
            throw new ArgumentException($"Slider: the thickness of surface {Surface} is infinite; give it a finite value first.");
        return LensVariables.Values(settings, new[] { Variable })[0];
    }

    private OpticsSettings Write(OpticsSettings settings, double x)
    {
        var s = SurfaceOf(settings);
        if (!double.IsFinite(x)) throw new ArgumentException($"Slider: the value {x} is not a number.");
        if (SelectedParameter.AsRadius)
        {
            if (x == 0) throw new ArgumentException("Slider: a radius of 0 is not a surface; keep 0 out of the range, or drag the curvature to pass through a plane.");
            var surfaces = settings.Surfaces!.ToList();
            surfaces[Surface] = s with { Radius = x };
            return settings with { Surfaces = surfaces };
        }
        return LensVariables.Apply(settings, new[] { Variable }, new[] { x });
    }
}

/// <summary>One slider of the Visual Optimizer: a variable, its range and its value.</summary>
public sealed partial class VisualOptimizerRow : ObservableObject
{
    private readonly Action<VisualOptimizerRow, bool> _changed;
    internal bool Loading;

    internal VisualOptimizerRow(IReadOnlyList<LensVariable> choices, LensVariable variable, Action<VisualOptimizerRow, bool> changed)
    {
        Choices = choices;
        _variable = variable;
        _changed = changed;
    }

    /// <summary>The variables of the lens (the help: "the variable status of each value must be set prior").</summary>
    public IReadOnlyList<LensVariable> Choices { get; }

    /// <summary>The help's "Parameter".</summary>
    [ObservableProperty] private LensVariable _variable;
    [ObservableProperty] private double _start;
    [ObservableProperty] private double _stop;
    [ObservableProperty] private double _value;

    partial void OnVariableChanged(LensVariable value) { if (!Loading && value is not null) _changed(this, true); }
    partial void OnValueChanged(double value) { if (!Loading) _changed(this, false); }
}

/// <summary>
/// The Visual Optimizer (plan item OP-60; OpticStudio 2025 R2 help, "Visual Optimizer"): "between
/// 1 and 8 individual slider controls", each on a variable of the lens, moved together; each move
/// publishes the editors' record with every slider's value (<see cref="LensVariables.Apply"/>) as a
/// trial, as the Slider does. "Radius variables are always varied as curvatures", so a radius
/// variable's slider is its curvature (labelled so). Keep writes the values into the lens through
/// the workspace as one undoable edit (and the multi-configuration table when a slider is on a
/// table cell); Exit restores the editors' record.
/// </summary>
public sealed partial class OpticsVisualOptimizerViewModel : ObservableObject
{
    /// <summary>The help: "The visual optimization dialog supports between 1 and 8 individual slider controls."</summary>
    public const int MaxSliders = 8;

    private readonly Func<OpticsSettings> _system;
    private readonly Action<OpticsSettings> _preview;
    private readonly Func<OpticsSettings, bool, string, string?> _apply;

    public OpticsVisualOptimizerViewModel(Func<OpticsSettings> system, Action<OpticsSettings> preview, Func<OpticsSettings, bool, string, string?> apply)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public ObservableCollection<VisualOptimizerRow> Rows { get; } = new();

    [ObservableProperty] private string _status = "Flag cells Variable in the editors, then Load.";

    public bool IsModified { get; private set; }

    /// <summary>Restores any trial, then makes one slider per variable (the first eight).</summary>
    [RelayCommand]
    public void Load()
    {
        Restore();
        Rows.Clear();
        IReadOnlyList<LensVariable> variables;
        double[] values;
        var system = _system();
        try
        {
            variables = LensVariables.Find(system);
            values = LensVariables.Values(system, variables);
        }
        catch (ArgumentException ex) { Status = ex.Message; return; }
        if (variables.Count == 0) { Status = "The lens has no variables: flag cells Variable in the editors first."; return; }
        for (int i = 0; i < Math.Min(MaxSliders, variables.Count); i++)
        {
            var row = new VisualOptimizerRow(variables, variables[i], OnRowChanged) { Loading = true };
            SetRange(row, values[i]);
            row.Loading = false;
            Rows.Add(row);
        }
        Status = variables.Count > MaxSliders
            ? $"{variables.Count} variables; sliders on the first {MaxSliders} (pick another variable on any slider)."
            : $"{variables.Count} slider(s). Drag to try values; Keep writes them into the lens.";
    }

    [RelayCommand]
    public void Keep()
    {
        if (Rows.Count == 0) return;
        var variables = Rows.Select(r => r.Variable).ToArray();
        string? refusal;
        try { refusal = _apply(LensVariables.Apply(_system(), variables, Rows.Select(r => r.Value).ToArray()), variables.Any(v => v.Cell is not null), "Visual Optimizer"); }
        catch (ArgumentException ex) { refusal = ex.Message; }
        if (refusal is not null) { Status = $"Not kept: {refusal}"; return; }
        IsModified = false;
        Status = "Kept: the values are in the lens. The Lens Data Editor's Undo takes them back.";
    }

    [RelayCommand]
    public void Exit() => Restore();

    private static void SetRange(VisualOptimizerRow row, double value)
    {
        var (start, stop) = InteractiveRanges.Default(value, row.Variable.Kind);
        row.Start = start;
        row.Stop = stop;
        row.Value = value;
    }

    private void OnRowChanged(VisualOptimizerRow row, bool variableChanged)
    {
        if (variableChanged)
        {
            if (Rows.Count(r => r.Variable == row.Variable) > 1)
            {
                Status = $"{row.Variable.Label} already has a slider.";
                return;
            }
            // A slider moved to another variable starts from that variable's value in the editors.
            Restore();
            row.Loading = true;
            try { SetRange(row, LensVariables.Values(_system(), new[] { row.Variable })[0]); }
            catch (ArgumentException ex) { Status = ex.Message; }
            finally { row.Loading = false; }
        }
        Preview();
    }

    private void Preview()
    {
        if (Rows.Select(r => r.Variable).Distinct().Count() != Rows.Count) return;
        try
        {
            _preview(LensVariables.Apply(_system(), Rows.Select(r => r.Variable).ToArray(), Rows.Select(r => r.Value).ToArray()));
            IsModified = true;
        }
        catch (ArgumentException ex) { Status = ex.Message; }
    }

    private void Restore()
    {
        if (!IsModified) return;
        IsModified = false;
        _preview(_system());
    }
}
