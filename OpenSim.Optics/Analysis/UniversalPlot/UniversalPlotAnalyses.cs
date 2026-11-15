using System.Globalization;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;

namespace OpenSim.Optics.Analysis.UniversalPlot;

// OP-45: OpticStudio's Universal Plot 1-D and 2-D (docs/Optics-Conventions.md §65).

/// <summary>
/// The base of the two Universal Plots. Unlike <see cref="OpticsAnalysis{TSettings}"/> it hands
/// the compute the whole optics record, multi-configuration table included, not the current
/// configuration's record: a configuration cell can only be swept in the table, operands that read
/// across configurations need it, and the table is evaluated again at every point (the operand is
/// still evaluated in the current configuration, as every analysis is).
/// </summary>
public abstract class UniversalPlotAnalysisBase<TSettings> : IOpticsAnalysis where TSettings : class, new()
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public string Category => "Universal plot";
    public Type SettingsType => typeof(TSettings);
    public object CreateDefaultSettings() => new TSettings();

    /// <summary>The thread limit of the sweep (null: none); the result is the same bits at any value.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    public OpticsAnalysisResult Compute(OpticsAnalysisContext context, object settings, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (settings is not TSettings typed)
            throw new ArgumentException(
                $"Analysis '{Id}' takes settings of type {typeof(TSettings).Name}, not {settings?.GetType().Name ?? "null"}.",
                nameof(settings));
        cancellation.ThrowIfCancellationRequested();
        if (context.System.Surfaces is not { Count: > 0 }) return FieldScanCommon.NoSurfaces(Name);
        return Compute(context, typed, cancellation);
    }

    protected abstract OpticsAnalysisResult Compute(OpticsAnalysisContext context, TSettings settings, CancellationToken cancellation);

    internal static string N(double v) => StudyReport.Number(v, "g10");

    /// <summary>The settings pairs common to both plots.</summary>
    internal static List<(string, string)> OperandPairs(UniversalPlotDependent dependent, int current, int configurations)
    {
        var pairs = new List<(string, string)> { ("Operand", dependent.Label) };
        if (dependent.Columns.Count > 0) pairs.Add(("Columns", string.Join("; ", dependent.Columns)));
        pairs.Add(("Configuration", configurations == 1 ? "1 (the only one)" : $"{current} (the current one) of {configurations}"));
        return pairs;
    }

    internal static List<string> Assumptions(UniversalPlotDependent dependent, int current)
    {
        var list = new List<string>
        {
            "Each point is a copy of the system with the swept value written in; the project is not changed. Multi-configuration pickups, then the lens's solves and pickups, are evaluated again at every point, as the optimizer evaluates a trial design.",
            dependent.Line is null
                ? $"The operand is evaluated alone, as a one-row merit function (as OPEV does), in configuration {current}."
                : $"The system merit function is evaluated whole at every point, starting in configuration {current}.",
            "A point whose value cannot be computed is a gap: it has no value in the plot, the table marks it failed, and the reason is listed. OpticStudio instead stops with an error (help: \"If the optimization operand cannot be computed, an error message will be displayed and the plot will not be generated\"); this program stops only when no point can be computed.",
        };
        list.AddRange(dependent.Notes);
        return list;
    }

    /// <summary>Throws, naming every point, when no point could be computed.</summary>
    internal static void RequireOne(string who, IReadOnlyList<UniversalPlotPoint> points, Func<int, string> where)
    {
        if (points.Any(p => p.Succeeded)) return;
        const int shown = 20;
        var lines = points.Take(shown).Select((p, i) => $"{where(i)}: {p.Error}").ToList();
        if (points.Count > shown) lines.Add($"… and {points.Count - shown} more points.");
        throw new InvalidOperationException($"{who}: the operand could not be computed at any of the {points.Count} points:"
                                            + Environment.NewLine + string.Join(Environment.NewLine, lines));
    }
}

/// <summary>OpticStudio's Universal Plot 1-D (OP-45): an operand against one swept parameter, as a
/// line plot and a table. A point that cannot be computed is a gap in the line.</summary>
public sealed class UniversalPlot1DAnalysis : UniversalPlotAnalysisBase<UniversalPlot1DSettings>
{
    public const string SavedId = "UniversalPlot1D";
    public override string Id => SavedId;
    public override string Name => "Universal plot 1D";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, UniversalPlot1DSettings s, CancellationToken cancellation)
    {
        const string who = "Universal plot 1D";
        var system = context.System;
        var registry = MeritOperandRegistry.CreateDefault();
        var variable = UniversalPlotEngine.Resolve(system, new UniversalPlotAxis(s.Category, s.Parameter, s.Surface, s.Number, s.Start, s.Stop, s.Steps), context.Catalogs, who);
        var dependent = UniversalPlotEngine.Resolve(system, Operand(s.Operand, s.Line, s.Int1, s.Int2, s.Data1, s.Data2, s.Data3, s.Data4, s.Data5, s.Data6), registry, who);
        var xs = variable.Values;
        var points = UniversalPlotEngine.Evaluate(system, context.Catalogs, dependent, xs.Count, i => variable.Apply(system, xs[i]),
            registry, MaxDegreeOfParallelism, cancellation);
        RequireOne(who, points, i => $"Point {i + 1}, {variable.At(xs[i])}");

        int current = MultiConfiguration.CurrentConfiguration(system), configurations = MultiConfiguration.ConfigurationCount(system);
        string title = string.IsNullOrWhiteSpace(s.Title) ? $"{dependent.Label} vs {variable.Label}" : s.Title.Trim();
        var plot = new LinePlot(title, variable.AxisLabel, dependent.Label,
            new[] { new AnalysisCurve(dependent.Unit, xs, points.Select(p => p.Succeeded ? p.Value : double.NaN).ToArray()) });

        var pairs = new List<(string, string)>
        {
            ("Independent variable", variable.AxisLabel),
            ("From", N(xs[0])), ("To", N(xs[^1])), ("Steps", xs.Count.ToString(CultureInfo.InvariantCulture)),
        };
        pairs.AddRange(OperandPairs(dependent, current, configurations));
        var sections = new List<ReportSection>
        {
            new("Settings") { Pairs = pairs },
            new("Values")
            {
                TableHeaders = new[] { "Point", variable.AxisLabel, dependent.Label },
                TableRows = points.Select((p, i) => (IReadOnlyList<string>)new[]
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture), N(xs[i]), p.Succeeded ? N(p.Value) : "failed",
                }).ToArray(),
            },
        };
        var failed = points.Select((p, i) => (p, i)).Where(t => !t.p.Succeeded).ToList();
        if (failed.Count > 0)
            sections.Add(new ReportSection("Failed points")
            {
                Lines = failed.Select(t => $"Point {t.i + 1}, {variable.At(xs[t.i])}: {t.p.Error}").ToArray(),
            });
        return OpticsAnalysisResult.FromSections(title, sections, Assumptions(dependent, current), new AnalysisPlot[] { plot });
    }

    internal static UniversalPlotOperand Operand(string? code, int line, int int1, int int2, params double[] data) =>
        new(code ?? "", line, int1, int2, data);
}

/// <summary>OpticStudio's Universal Plot 2-D (OP-45): an operand over two swept parameters, as a
/// false-colour map or contours and a table. A point that cannot be computed has no data in the
/// map.</summary>
public sealed class UniversalPlot2DAnalysis : UniversalPlotAnalysisBase<UniversalPlot2DSettings>
{
    public const string SavedId = "UniversalPlot2D";
    public override string Id => SavedId;
    public override string Name => "Universal plot 2D";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, UniversalPlot2DSettings s, CancellationToken cancellation)
    {
        const string who = "Universal plot 2D";
        var style = s.ShowAs switch
        {
            UniversalPlotDisplay.FalseColor => AnalysisMapStyle.Image,
            UniversalPlotDisplay.Contour => AnalysisMapStyle.Contour,
            _ => throw new ArgumentException($"{who}: show as {s.ShowAs} is not built; choose FalseColor or Contour."),
        };
        if (!double.IsFinite(s.PlotMinimum) || !double.IsFinite(s.PlotMaximum))
            throw new ArgumentException($"{who}: the plot scale values must be finite numbers.");
        if (s.PlotMinimum != 0 && s.PlotMaximum != 0 && !(s.PlotMaximum > s.PlotMinimum))
            throw new ArgumentException($"{who}: the max plot scale ({N(s.PlotMaximum)}) must be above the min ({N(s.PlotMinimum)}).");

        var system = context.System;
        var registry = MeritOperandRegistry.CreateDefault();
        var xv = UniversalPlotEngine.Resolve(system, new UniversalPlotAxis(s.XCategory, s.XParameter, s.XSurface, s.XNumber, s.XStart, s.XStop, s.XSteps), context.Catalogs, who + ", x");
        var yv = UniversalPlotEngine.Resolve(system, new UniversalPlotAxis(s.YCategory, s.YParameter, s.YSurface, s.YNumber, s.YStart, s.YStop, s.YSteps), context.Catalogs, who + ", y");
        if (xv.Key == yv.Key)
            throw new ArgumentException($"{who}: x ({xv.Label}) and y ({yv.Label}) write the same cell; choose two different variables.");
        var dependent = UniversalPlotEngine.Resolve(system, UniversalPlot1DAnalysis.Operand(s.Operand, s.Line, s.Int1, s.Int2, s.Data1, s.Data2, s.Data3, s.Data4, s.Data5, s.Data6), registry, who);

        // The points in OpenSim.Core.Studies' full-factorial order over (x, y): y runs fastest.
        var plan = new OpenSim.Core.Studies.SweepPlan
        {
            Parameters = new[]
            {
                new OpenSim.Core.Studies.SweepParameter { Name = "x", Values = xv.Values },
                new OpenSim.Core.Studies.SweepParameter { Name = "y", Values = yv.Values },
            },
        };
        var sweep = plan.Points().ToArray();
        int nx = xv.Values.Count, ny = yv.Values.Count;
        var points = UniversalPlotEngine.Evaluate(system, context.Catalogs, dependent, sweep.Length,
            i => yv.Apply(xv.Apply(system, sweep[i]["x"]), sweep[i]["y"]), registry, MaxDegreeOfParallelism, cancellation);
        UniversalPlotPoint At(int ix, int iy) => points[ix * ny + iy];
        RequireOne(who, points, i => $"Point {i + 1}, {xv.At(sweep[i]["x"])}, {yv.At(sweep[i]["y"])}");

        // The map's columns run in increasing x and its rows in increasing y, whichever way the sweep ran.
        bool xUp = xv.Values[^1] > xv.Values[0], yUp = yv.Values[^1] > yv.Values[0];
        var grid = new double[ny, nx];
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++)
            {
                var p = At(xUp ? c : nx - 1 - c, yUp ? r : ny - 1 - r);
                grid[r, c] = p.Succeeded ? p.Value : double.NaN;
            }
        var field = new GridField(grid, Math.Min(xv.Values[0], xv.Values[^1]), Math.Max(xv.Values[0], xv.Values[^1]),
            Math.Min(yv.Values[0], yv.Values[^1]), Math.Max(yv.Values[0], yv.Values[^1]));

        int current = MultiConfiguration.CurrentConfiguration(system), configurations = MultiConfiguration.ConfigurationCount(system);
        string title = string.IsNullOrWhiteSpace(s.Title) ? $"{dependent.Label} vs {xv.Label} and {yv.Label}" : s.Title.Trim();
        var map = new AnalysisMapPlot(title, xv.AxisLabel, yv.AxisLabel, field, dependent.Unit)
        {
            Style = style,
            Minimum = style == AnalysisMapStyle.Image && s.PlotMinimum != 0 ? s.PlotMinimum : double.NaN,
            Maximum = style == AnalysisMapStyle.Image && s.PlotMaximum != 0 ? s.PlotMaximum : double.NaN,
        };

        var pairs = new List<(string, string)>
        {
            ("X variable", xv.AxisLabel), ("X from", N(xv.Values[0])), ("X to", N(xv.Values[^1])), ("X steps", nx.ToString(CultureInfo.InvariantCulture)),
            ("Y variable", yv.AxisLabel), ("Y from", N(yv.Values[0])), ("Y to", N(yv.Values[^1])), ("Y steps", ny.ToString(CultureInfo.InvariantCulture)),
            ("Show as", s.ShowAs.ToString()),
        };
        pairs.AddRange(OperandPairs(dependent, current, configurations));
        var sections = new List<ReportSection>
        {
            new("Settings") { Pairs = pairs },
            new($"Values of {dependent.Label}: rows {yv.Label}, columns {xv.Label}")
            {
                TableHeaders = new[] { $"{yv.AxisLabel} \\ {xv.AxisLabel}" }.Concat(xv.Values.Select(N)).ToArray(),
                TableRows = Enumerable.Range(0, ny).Select(iy => (IReadOnlyList<string>)new[] { N(yv.Values[iy]) }
                    .Concat(Enumerable.Range(0, nx).Select(ix => At(ix, iy) is { Succeeded: true } p ? N(p.Value) : "failed")).ToArray()).ToArray(),
            },
        };
        var failed = Enumerable.Range(0, points.Length).Where(i => !points[i].Succeeded).ToList();
        if (failed.Count > 0)
            sections.Add(new ReportSection("Failed points")
            {
                Lines = failed.Select(i => $"Point {i + 1}, {xv.At(sweep[i]["x"])}, {yv.At(sweep[i]["y"])}: {points[i].Error}").ToArray(),
            });
        var assumptions = Assumptions(dependent, current);
        if (style == AnalysisMapStyle.Contour && (s.PlotMinimum != 0 || s.PlotMaximum != 0))
            assumptions.Add("The min and max plot scale have no effect on contours (help, Universal Plot 2-D); they are ignored.");
        assumptions.Add("Contours are evenly spaced; OpticStudio's contour format string is not built.");
        return OpticsAnalysisResult.FromSections(title, sections, assumptions, new AnalysisPlot[] { map });
    }
}
