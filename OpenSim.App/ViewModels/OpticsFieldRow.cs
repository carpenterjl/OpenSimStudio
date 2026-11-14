using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Optics;

namespace OpenSim.App.ViewModels;

/// <summary>One row of the field table as the panel edits it: x and y in the field type's
/// unit (degrees, or lens units for a height), the weight and the five vignetting factors.</summary>
public partial class OpticsFieldRow : ObservableObject
{
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _weight = 1;
    [ObservableProperty] private double _decenterX;
    [ObservableProperty] private double _decenterY;
    [ObservableProperty] private double _compressionX;
    [ObservableProperty] private double _compressionY;
    [ObservableProperty] private double _tiltDegrees;

    /// <summary>The 1-based field number the panel shows; set by the view model.</summary>
    [ObservableProperty] private int _number = 1;

    /// <summary>The field point in meters or degrees: a height is scaled by
    /// <paramref name="metersPerUnit"/>, an angle is not.</summary>
    public FieldPoint ToFieldPoint(double metersPerUnit) => new()
    {
        X = X * metersPerUnit, Y = Y * metersPerUnit, Weight = Weight,
        DecenterX = DecenterX, DecenterY = DecenterY, CompressionX = CompressionX, CompressionY = CompressionY,
        TiltDegrees = TiltDegrees
    };
}
