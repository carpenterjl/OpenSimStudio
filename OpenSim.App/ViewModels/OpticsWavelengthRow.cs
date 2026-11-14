using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSim.App.ViewModels;

/// <summary>One row of the wavelength table as the panel edits it: micrometers and weight.</summary>
public partial class OpticsWavelengthRow : ObservableObject
{
    [ObservableProperty] private double _micrometers = 0.55;
    [ObservableProperty] private double _weight = 1;

    /// <summary>The 1-based wavelength number the panel shows; set by the view model.</summary>
    [ObservableProperty] private int _number = 1;
}
