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
