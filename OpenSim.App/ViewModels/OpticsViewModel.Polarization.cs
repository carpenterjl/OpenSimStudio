using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Core.Model;
using OpenSim.Optics;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The system polarization (plan item OP-51) as the system data panel edits it: unpolarized or a
/// Jones vector (magnitudes, phases in degrees in OpticStudio's convention), the reference rule
/// that turns it into a launch field, and the thin-film phase conversion.
/// </summary>
public partial class OpticsViewModel
{
    public IReadOnlyList<OpticsChoice<PolarizationReference>> PolarizationReferenceChoices { get; } =
        OpticsChoice<PolarizationReference>.From(PolarizationReferences.Names);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolarizationTakesJones))]
    private bool _unpolarized;

    [ObservableProperty] private double _polarizationJx = 1;
    [ObservableProperty] private double _polarizationJy;
    [ObservableProperty] private double _polarizationXPhaseDegrees;
    [ObservableProperty] private double _polarizationYPhaseDegrees;

    [ObservableProperty]
    private OpticsChoice<PolarizationReference> _selectedPolarizationReference =
        Choice(PolarizationReferences.Names, PolarizationReference.XAxis);

    [ObservableProperty] private bool _convertThinFilmPhase = true;

    /// <summary>True when the Jones vector is used (not unpolarized).</summary>
    public bool PolarizationTakesJones => !Unpolarized;

    partial void OnSelectedPolarizationReferenceChanged(OpticsChoice<PolarizationReference> value)
    {
        // A picker pushes a transient null while its list rebuilds; the rule is never unset.
        if (value is null) SelectedPolarizationReference = PolarizationReferenceChoices[0];
    }

    /// <summary>The panel's polarization as the engines take it.</summary>
    private SystemPolarization BuildPolarization() => new()
    {
        Unpolarized = Unpolarized, Jx = PolarizationJx, Jy = PolarizationJy,
        XPhaseDegrees = PolarizationXPhaseDegrees, YPhaseDegrees = PolarizationYPhaseDegrees,
        Reference = SelectedPolarizationReference.Value, ConvertThinFilmPhase = ConvertThinFilmPhase,
    };

    private void LoadPolarization(SystemPolarization p)
    {
        Unpolarized = p.Unpolarized;
        PolarizationJx = p.Jx;
        PolarizationJy = p.Jy;
        PolarizationXPhaseDegrees = p.XPhaseDegrees;
        PolarizationYPhaseDegrees = p.YPhaseDegrees;
        SelectedPolarizationReference = Choice(PolarizationReferenceChoices, p.Reference);
        ConvertThinFilmPhase = p.ConvertThinFilmPhase;
    }

    /// <summary>The file form; the reference by saved name.</summary>
    private OpticsPolarizationSettings PolarizationToSettings() => SystemDataSettings.PolarizationToSettings(BuildPolarization());

    /// <summary>Sets the panel from a file; null (a file from before OP-51) is the default
    /// polarization, not the panel's current one.</summary>
    private void LoadPolarizationSettings(OpticsPolarizationSettings? saved, ICollection<string> notes) =>
        LoadPolarization(SystemDataSettings.PolarizationFromSettings(saved, notes));
}
