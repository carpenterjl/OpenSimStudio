namespace OpenSim.Optics.Analysis;

/// <summary>
/// A plot as data, free of any drawing type, so an analysis can run in the command line as
/// well as in a window. The window maps each kind onto a renderer; the plot kinds optics needs
/// beyond lines (scatter, contour, polar, false-colour image) join as OP-7 builds them.
/// </summary>
public abstract record AnalysisPlot(string Title);

/// <summary>One curve of a <see cref="LinePlot"/>.</summary>
public sealed record AnalysisCurve(string Name, IReadOnlyList<double> X, IReadOnlyList<double> Y, bool Dashed = false);

/// <summary>Curves against one x axis.</summary>
public sealed record LinePlot(string Title, string XLabel, string YLabel, IReadOnlyList<AnalysisCurve> Curves)
    : AnalysisPlot(Title)
{
    public bool LogX { get; init; }
    public bool LogY { get; init; }
}

/// <summary>One labelled point of an <see cref="AnalysisScatterPlot"/> (a glass on a glass map).</summary>
public sealed record AnalysisPoint(double X, double Y, string Label);

/// <summary>One series of an <see cref="AnalysisScatterPlot"/>: points drawn with one marker and colour.</summary>
public sealed record AnalysisScatterSeries(string Name, IReadOnlyList<AnalysisPoint> Points);

/// <summary>Labelled points against two axes at independent scales (the glass maps, OP-14).
/// The window draws it with the scatter plot control of OP-7.</summary>
public sealed record AnalysisScatterPlot(string Title, string XLabel, string YLabel, IReadOnlyList<AnalysisScatterSeries> Series)
    : AnalysisPlot(Title);

/// <summary>How the window draws an <see cref="AnalysisMapPlot"/>.</summary>
public enum AnalysisMapStyle
{
    /// <summary>False-colour image with a movable cross-section (OP-7's image plot).</summary>
    Image,
    /// <summary>Filled contours with lines (OP-7's contour plot).</summary>
    Contour,
}

/// <summary>
/// A scalar sampled on a regular grid, drawn as a false-colour image or as contours: wavefront
/// and OPD maps, sag and curvature maps, footprints, interferograms. Non-finite samples mean "no
/// data" (outside the pupil or the clear aperture) and are left blank. The grid follows
/// <see cref="OpenSim.Core.Plotting.GridField"/>'s node convention (samples on the corners).
/// </summary>
/// <param name="Field">The samples and their extent, in the units of the axis labels.</param>
/// <param name="ValueUnit">The unit of the samples ("waves", "mm", "1/mm"), shown on the colour bar.</param>
public sealed record AnalysisMapPlot(string Title, string XLabel, string YLabel,
    OpenSim.Core.Plotting.GridField Field, string ValueUnit) : AnalysisPlot(Title)
{
    public AnalysisMapStyle Style { get; init; } = AnalysisMapStyle.Image;

    /// <summary>The bottom and top of the colour scale; NaN for the smallest or largest sample.</summary>
    public double Minimum { get; init; } = double.NaN;
    public double Maximum { get; init; } = double.NaN;

    /// <summary>Contour levels for <see cref="AnalysisMapStyle.Contour"/>, or null for evenly spaced ones.</summary>
    public IReadOnlyList<double>? Levels { get; init; }
}
