using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Optimization.Interactive;

namespace OpenSim.App.ViewModels;

// OP-60: the Quick Focus and Quick Adjust dialogs.

/// <summary>
/// Quick Focus (plan item OP-60; OpticStudio 2025 R2 help, "Quick Focus"): the criterion, Use
/// Centroid and the pupil sampling; Focus runs <see cref="QuickFocus.Run"/> on the project's
/// system off the UI thread and writes the thickness before the image into the lens through the
/// workspace (<c>apply</c>: one step of the Lens Data Editor's undo stack, so the editor's Undo
/// takes it back).
/// </summary>
public sealed partial class OpticsQuickFocusViewModel : ObservableObject
{
    private readonly Func<OpticsSettings> _system;
    private readonly Func<IReadOnlyList<OpticalCatalog>> _catalogs;
    private readonly Func<OpticsSettings, bool, string, string?> _apply;

    /// <param name="system">The project's optics record now.</param>
    /// <param name="catalogs">The active glass catalogs.</param>
    /// <param name="apply">Writes a record into the workspace as one undoable edit named by the
    /// text (the flag: whether the multi-configuration table changed); returns why it was refused, or null.</param>
    public OpticsQuickFocusViewModel(Func<OpticsSettings> system, Func<IReadOnlyList<OpticalCatalog>> catalogs,
        Func<OpticsSettings, bool, string, string?> apply)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _selectedCriterion = Criteria[0];
    }

    public IReadOnlyList<InteractiveChoice<QuickFocusCriterion>> Criteria { get; } = new[]
    {
        new InteractiveChoice<QuickFocusCriterion>("Spot size radial", QuickFocusCriterion.SpotRadius),
        new InteractiveChoice<QuickFocusCriterion>("Spot size x only", QuickFocusCriterion.SpotX),
        new InteractiveChoice<QuickFocusCriterion>("Spot size y only", QuickFocusCriterion.SpotY),
        new InteractiveChoice<QuickFocusCriterion>("Wavefront error", QuickFocusCriterion.Wavefront),
    };

    [ObservableProperty] private InteractiveChoice<QuickFocusCriterion> _selectedCriterion;

    /// <summary>The help's "Use Centroid".</summary>
    [ObservableProperty] private bool _useCentroid;

    /// <summary>Gaussian quadrature rings, 1 to 20.</summary>
    [ObservableProperty] private int _rings = 6;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FocusCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    [ObservableProperty] private string _status = "Adjusts the thickness of the surface before the image for the best RMS.";

    /// <summary>The last search's assumptions, one per line.</summary>
    [ObservableProperty] private string _details = "";

    /// <summary>The last result applied, or null.</summary>
    public QuickFocusResult? LastResult { get; private set; }

    private bool CanFocus() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanFocus))]
    private async Task FocusAsync()
    {
        var system = _system();
        var catalogs = _catalogs();
        var options = new QuickFocusOptions { Criterion = SelectedCriterion.Value, UseCentroid = UseCentroid, Rings = Rings };
        IsRunning = true;
        Status = "Focusing…";
        try
        {
            var applied = await Task.Run(() => QuickFocus.Run(system, catalogs, options));
            var r = applied.Result;
            string? refusal;
            try { refusal = _apply(applied.Settings, false, "Quick Focus"); }
            catch (ArgumentException ex) { refusal = ex.Message; }
            if (refusal is not null) { Status = $"Not applied: {refusal}"; return; }
            LastResult = r;
            string unit = QuickFocusResult.Unit(r.Criterion);
            Status = string.Create(CultureInfo.InvariantCulture,
                $"Surface {r.Surface} thickness {system.Surfaces![r.Surface].Thickness:g8} → {applied.Settings.Surfaces![r.Surface].Thickness:g8} (lens units); {QuickFocusResult.Label(r.Criterion)} {QuickFocusResult.Shown(r.Criterion, r.StartValue):g6} → {QuickFocusResult.Shown(r.Criterion, r.Value):g6} {unit}. The Lens Data Editor's Undo takes it back.");
            Details = string.Join(Environment.NewLine, r.Assumptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
            Details = "";
        }
        finally { IsRunning = false; }
    }
}

/// <summary>One entry of an interactive tool's picker.</summary>
public sealed record InteractiveChoice<T>(string Label, T Value);

/// <summary>
/// Quick Adjust (plan item OP-60; OpticStudio 2025 R2 help, "Quick Adjust"): the surface, radius
/// or thickness, the criterion and the evaluation surface; Adjust runs
/// <see cref="QuickAdjust.Run"/> off the UI thread and writes the cell through the workspace as
/// one undoable edit. The dialog may stay open while the lens is edited ("This tool can remain
/// open on the desktop"); each Adjust reads the lens afresh.
/// </summary>
public sealed partial class OpticsQuickAdjustViewModel : ObservableObject
{
    private readonly Func<OpticsSettings> _system;
    private readonly Func<IReadOnlyList<OpticalCatalog>> _catalogs;
    private readonly Func<OpticsSettings, bool, string, string?> _apply;

    public OpticsQuickAdjustViewModel(Func<OpticsSettings> system, Func<IReadOnlyList<OpticalCatalog>> catalogs,
        Func<OpticsSettings, bool, string, string?> apply)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _selectedParameter = Parameters[1];
        _selectedCriterion = Criteria[0];
    }

    public IReadOnlyList<InteractiveChoice<QuickAdjustParameter>> Parameters { get; } = new[]
    {
        new InteractiveChoice<QuickAdjustParameter>("Radius", QuickAdjustParameter.Radius),
        new InteractiveChoice<QuickAdjustParameter>("Thickness", QuickAdjustParameter.Thickness),
    };

    public IReadOnlyList<InteractiveChoice<QuickAdjustCriterion>> Criteria { get; } = new[]
    {
        new InteractiveChoice<QuickAdjustCriterion>("Spot radius", QuickAdjustCriterion.SpotRadius),
        new InteractiveChoice<QuickAdjustCriterion>("Spot x", QuickAdjustCriterion.SpotX),
        new InteractiveChoice<QuickAdjustCriterion>("Spot y", QuickAdjustCriterion.SpotY),
        new InteractiveChoice<QuickAdjustCriterion>("Angular radius", QuickAdjustCriterion.AngularRadius),
        new InteractiveChoice<QuickAdjustCriterion>("Angular x", QuickAdjustCriterion.AngularX),
        new InteractiveChoice<QuickAdjustCriterion>("Angular y", QuickAdjustCriterion.AngularY),
    };

    /// <summary>The surface to adjust (0 is the object).</summary>
    [ObservableProperty] private int _surface = 1;

    [ObservableProperty] private InteractiveChoice<QuickAdjustParameter> _selectedParameter;
    [ObservableProperty] private InteractiveChoice<QuickAdjustCriterion> _selectedCriterion;

    /// <summary>The surface the criterion is evaluated at; 0 for the image.</summary>
    [ObservableProperty] private int _evaluateSurface;

    [ObservableProperty] private int _rings = 6;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AdjustCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    [ObservableProperty] private string _status = "Adjusts one radius or thickness for the best RMS at a later surface (evaluation surface 0: the image).";

    [ObservableProperty] private string _details = "";

    public QuickAdjustResult? LastResult { get; private set; }

    private bool CanAdjust() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanAdjust))]
    private async Task AdjustAsync()
    {
        var system = _system();
        var catalogs = _catalogs();
        var options = new QuickAdjustOptions
        {
            Surface = Surface, Parameter = SelectedParameter.Value, Criterion = SelectedCriterion.Value,
            EvaluateSurface = EvaluateSurface <= 0 ? null : EvaluateSurface, Rings = Rings,
        };
        IsRunning = true;
        Status = "Adjusting…";
        try
        {
            var applied = await Task.Run(() => QuickAdjust.Run(system, catalogs, options));
            var r = applied.Result;
            string? refusal;
            try { refusal = _apply(applied.Settings, false, "Quick Adjust"); }
            catch (ArgumentException ex) { refusal = ex.Message; }
            if (refusal is not null) { Status = $"Not applied: {refusal}"; return; }
            LastResult = r;
            var cell = applied.Settings.Surfaces![r.Surface];
            string value = r.Parameter == QuickAdjustParameter.Radius
                ? (cell.Radius is double radius ? $"radius {radius.ToString("g8", CultureInfo.InvariantCulture)}" : "a plane")
                : $"thickness {cell.Thickness.ToString("g8", CultureInfo.InvariantCulture)}";
            string unit = QuickAdjustResult.Unit(r.Criterion);
            Status = string.Create(CultureInfo.InvariantCulture,
                $"Surface {r.Surface}: {value} (lens units); {QuickAdjustResult.Label(r.Criterion)} at surface {r.EvaluateSurface} {QuickAdjustResult.Shown(r.Criterion, r.StartCriterion):g6} → {QuickAdjustResult.Shown(r.Criterion, r.CriterionValue):g6} {unit}. The Lens Data Editor's Undo takes it back.");
            Details = string.Join(Environment.NewLine, r.Assumptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
            Details = "";
        }
        finally { IsRunning = false; }
    }
}
