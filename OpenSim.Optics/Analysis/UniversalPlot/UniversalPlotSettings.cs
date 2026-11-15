using System.ComponentModel;

namespace OpenSim.Optics.Analysis.UniversalPlot;

// OP-45: Universal Plot 1D and 2D (OpticStudio 2025 R2 help, "Universal Plot 1-D" and "Universal
// Plot 2-D", saved under plans/sources/optics/op-45-help/). docs/Optics-Conventions.md §65.

/// <summary>Where an independent variable lives: "Select either surface, system,
/// multi-configuration, or NSC data as the independent variable" (help, Universal Plot 1-D).</summary>
public enum UniversalPlotCategory
{
    /// <summary>A cell of the Lens Data Editor: radius, curvature, thickness, conic or a parameter.</summary>
    Surface,
    /// <summary>System data: the aperture, a field, a wavelength, the apodization factor, temperature, pressure.</summary>
    System,
    /// <summary>A cell of the multi-configuration table.</summary>
    Configuration,
    /// <summary>Non-sequential object data. Not built: there is no non-sequential system.</summary>
    NonSequential,
}

/// <summary>
/// The parameter swept. The help lists "radius, curvature, thickness, conic, and parameter values
/// if surface parameters are chosen. For system parameters, the selections include the system
/// aperture, field and wavelength data, apodization factor, temperature, and pressure. For
/// configuration data, all multi-configuration operands are listed" (Universal Plot 1-D). The
/// help does not give OpticStudio's own order of the options, so these are named, not numbered.
/// </summary>
public enum UniversalPlotParameter
{
    // Surface data.
    /// <summary>The radius (lens units; 0 is a plane).</summary>
    Radius,
    /// <summary>The curvature 1/R (1/lens unit; 0 is a plane).</summary>
    Curvature,
    /// <summary>The thickness (lens units).</summary>
    Thickness,
    /// <summary>The conic constant.</summary>
    Conic,
    /// <summary>Parameter (or extra data) number <c>Number</c> of the surface's type.</summary>
    Parameter,

    // System data.
    /// <summary>The system aperture value, in the aperture type's unit.</summary>
    ApertureValue,
    /// <summary>The apodization factor.</summary>
    ApodizationFactor,
    /// <summary>The temperature (°C).</summary>
    Temperature,
    /// <summary>The air pressure (atmospheres).</summary>
    Pressure,
    /// <summary>The x value of field <c>Number</c>, in the field type's unit.</summary>
    FieldX,
    /// <summary>The y value of field <c>Number</c>, in the field type's unit.</summary>
    FieldY,
    /// <summary>The weight of field <c>Number</c>.</summary>
    FieldWeight,
    /// <summary>The vignetting decenter x (VDX) of field <c>Number</c>.</summary>
    FieldDecenterX,
    /// <summary>The vignetting decenter y (VDY) of field <c>Number</c>.</summary>
    FieldDecenterY,
    /// <summary>The vignetting compression x (VCX) of field <c>Number</c>.</summary>
    FieldCompressionX,
    /// <summary>The vignetting compression y (VCY) of field <c>Number</c>.</summary>
    FieldCompressionY,
    /// <summary>The vignetting angle (VAN, degrees) of field <c>Number</c>.</summary>
    FieldTilt,
    /// <summary>Wavelength <c>Number</c> (micrometers).</summary>
    Wavelength,
    /// <summary>The weight of wavelength <c>Number</c>.</summary>
    WavelengthWeight,

    // Configuration data.
    /// <summary>Row <c>Number</c> of the multi-configuration table, in configuration <c>Surface</c>.</summary>
    Operand,
}

/// <summary>How a Universal Plot 2-D is drawn. The help offers "surface plot, contour map, grey
/// scale, or false color map"; the surface plot and the grey scale are not built (the map plot
/// draws a false-colour image or contours).</summary>
public enum UniversalPlotDisplay
{
    FalseColor,
    Contour,
    GreyScale,
    Surface,
}

/// <summary>Settings of the <see cref="UniversalPlot1DAnalysis"/>, after the help's Universal Plot
/// 1-D dialog (UN1_* in MODIFYSETTINGS). The plot's y range (Min/Max Plot Value) and the saved
/// settings files (UPL) are not built.</summary>
public sealed record UniversalPlot1DSettings
{
    [DisplayName("Category")]
    [Description("Surface, System or Configuration data (NonSequential is not built).")]
    public UniversalPlotCategory Category { get; init; } = UniversalPlotCategory.Surface;

    [DisplayName("Parameter")]
    [Description("Surface: Radius, Curvature, Thickness, Conic, Parameter. System: ApertureValue, ApodizationFactor, Temperature, Pressure, FieldX … FieldTilt, Wavelength, WavelengthWeight. Configuration: Operand.")]
    public UniversalPlotParameter Parameter { get; init; } = UniversalPlotParameter.Thickness;

    [DisplayName("Surface")]
    [Description("The surface number (0 = object); for Configuration data, the configuration number.")]
    public int Surface { get; init; } = 1;

    [DisplayName("Number")]
    [Description("Parameter: the parameter number; FieldX … FieldTilt: the field number; Wavelength, WavelengthWeight: the wavelength number; Operand: the multi-configuration row. All from 1.")]
    public int Number { get; init; } = 1;

    [DisplayName("Start value")]
    [Description("The first value of the independent variable, in its own unit.")]
    public double Start { get; init; } = 1;

    [DisplayName("Stop value")]
    [Description("The last value of the independent variable.")]
    public double Stop { get; init; } = 10;

    [DisplayName("# of steps")]
    [Description("The number of values from start to stop, both included; at least 2.")]
    public int Steps { get; init; } = 10;

    [DisplayName("Operand")]
    [Description("A merit operand's four-letter code, or Merit for the system merit function.")]
    public string Operand { get; init; } = "EFFL";

    [DisplayName("Line")]
    [Description("With Merit: 0 for the merit function's value, or the row whose value is plotted.")]
    public int Line { get; init; }

    [DisplayName("Int1")]
    public int Int1 { get; init; }

    [DisplayName("Int2")]
    public int Int2 { get; init; } = 1;

    [DisplayName("Data1")]
    public double Data1 { get; init; }

    [DisplayName("Data2")]
    public double Data2 { get; init; }

    [DisplayName("Data3")]
    public double Data3 { get; init; }

    [DisplayName("Data4")]
    public double Data4 { get; init; }

    [DisplayName("Data5")]
    public double Data5 { get; init; }

    [DisplayName("Data6")]
    public double Data6 { get; init; }

    [DisplayName("Plot title")]
    [Description("The title of the plot and the text; empty for 'operand vs variable'.")]
    public string Title { get; init; } = "";
}

/// <summary>Settings of the <see cref="UniversalPlot2DAnalysis"/>, after the help's Universal Plot
/// 2-D dialog (UN2_* in MODIFYSETTINGS). The contour format string and the saved settings files
/// (UP2) are not built.</summary>
public sealed record UniversalPlot2DSettings
{
    [DisplayName("X category")]
    [Description("Surface, System or Configuration data (NonSequential is not built).")]
    public UniversalPlotCategory XCategory { get; init; } = UniversalPlotCategory.Surface;

    [DisplayName("X parameter")]
    [Description("As the 1D plot's Parameter.")]
    public UniversalPlotParameter XParameter { get; init; } = UniversalPlotParameter.Thickness;

    [DisplayName("X surface")]
    [Description("The surface number (0 = object); for Configuration data, the configuration number.")]
    public int XSurface { get; init; } = 1;

    [DisplayName("X number")]
    [Description("The parameter, field or wavelength number, or the multi-configuration row; from 1.")]
    public int XNumber { get; init; } = 1;

    [DisplayName("X start value")]
    public double XStart { get; init; } = 1;

    [DisplayName("X stop value")]
    public double XStop { get; init; } = 10;

    [DisplayName("X # of steps")]
    [Description("At least 2 (help: \"The minimum value is 2\").")]
    public int XSteps { get; init; } = 11;

    [DisplayName("Y category")]
    public UniversalPlotCategory YCategory { get; init; } = UniversalPlotCategory.Surface;

    [DisplayName("Y parameter")]
    public UniversalPlotParameter YParameter { get; init; } = UniversalPlotParameter.Conic;

    [DisplayName("Y surface")]
    public int YSurface { get; init; } = 1;

    [DisplayName("Y number")]
    public int YNumber { get; init; } = 1;

    [DisplayName("Y start value")]
    public double YStart { get; init; } = -1;

    [DisplayName("Y stop value")]
    public double YStop { get; init; } = 1;

    [DisplayName("Y # of steps")]
    [Description("At least 2.")]
    public int YSteps { get; init; } = 11;

    [DisplayName("Operand")]
    [Description("A merit operand's four-letter code, or Merit for the system merit function.")]
    public string Operand { get; init; } = "EFFL";

    [DisplayName("Line")]
    [Description("With Merit: 0 for the merit function's value, or the row whose value is plotted.")]
    public int Line { get; init; }

    [DisplayName("Int1")]
    public int Int1 { get; init; }

    [DisplayName("Int2")]
    public int Int2 { get; init; } = 1;

    [DisplayName("Data1")]
    public double Data1 { get; init; }

    [DisplayName("Data2")]
    public double Data2 { get; init; }

    [DisplayName("Data3")]
    public double Data3 { get; init; }

    [DisplayName("Data4")]
    public double Data4 { get; init; }

    [DisplayName("Data5")]
    public double Data5 { get; init; }

    [DisplayName("Data6")]
    public double Data6 { get; init; }

    [DisplayName("Show as")]
    [Description("FalseColor (an image) or Contour. GreyScale and Surface are not built.")]
    public UniversalPlotDisplay ShowAs { get; init; } = UniversalPlotDisplay.FalseColor;

    [DisplayName("Min plot scale")]
    [Description("The bottom of the colour scale; 0 for the smallest value. No effect on contours.")]
    public double PlotMinimum { get; init; }

    [DisplayName("Max plot scale")]
    [Description("The top of the colour scale; 0 for the largest value. No effect on contours.")]
    public double PlotMaximum { get; init; }

    [DisplayName("Plot title")]
    [Description("The title of the plot and the text; empty for 'operand vs variables'.")]
    public string Title { get; init; } = "";
}
