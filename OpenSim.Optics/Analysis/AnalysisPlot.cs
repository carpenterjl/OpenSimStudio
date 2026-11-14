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
