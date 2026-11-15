using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenSim.Core.Model;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Analysis;

// OP-97: command-line optics. The one place the command line and the app meet for an analysis
// report: the snapshot an analysis computes from, the settings a saved project gives it, and the
// report composed from the results (OpticsAnalysisReport.Compose, the app's Save report). The
// app's OpticsAnalysesViewModel builds the same snapshot (Snapshot) and composes with the same
// function, so the same lens gives the same report bytes from either (docs/Optics-Conventions.md §46).

/// <summary>
/// Running registered analyses outside a window, for the command line and scripts: the snapshot
/// of a saved optics record, the settings the project saved for an analysis, settings overrides
/// by name, the computes in order, and the composite report.
/// </summary>
public static class OpticsBatch
{
    /// <summary>
    /// The snapshot an analysis computes from, as the app's analysis windows take it: the optics
    /// record without its window layout (<see cref="OpticsSettings.Analyses"/> null) and the lens's
    /// active catalogs among <paramref name="loadedCatalogs"/>
    /// (<see cref="GlassLookup.ActiveCatalogs"/>).
    /// </summary>
    public static OpticsAnalysisContext Context(string projectName, OpticsSettings system, IReadOnlyList<OpticalCatalog> loadedCatalogs)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(loadedCatalogs);
        var snapshot = system with { Analyses = null };
        return new OpticsAnalysisContext(projectName, snapshot)
        {
            Catalogs = GlassLookup.ActiveCatalogs(loadedCatalogs, system.Materials?.ActiveCatalogs),
        };
    }

    /// <summary>The settings a new window on <paramref name="analysis"/> opens with in the app: the
    /// ones the project saved as last used (<see cref="OpticsAnalysesSettings.LastSettings"/>), or
    /// the analysis's defaults. <paramref name="note"/> says when saved settings could not be read.</summary>
    public static object SavedSettings(IOpticsAnalysis analysis, OpticsSettings system, out string? note)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(system);
        note = null;
        if (system.Analyses?.LastSettings is { } last && last.TryGetValue(analysis.Id, out var saved))
            return OpticsAnalysisSettingsCodec.Load(analysis, saved, out note);
        return analysis.CreateDefaultSettings();
    }

    /// <summary>
    /// Settings read from a JSON object (the project file's form, camelCase names, enums by name).
    /// Strict, unlike a project load: a property the settings record does not have, or a value that
    /// does not parse, throws <see cref="ArgumentException"/> naming the analysis.
    /// </summary>
    public static object SettingsFromJson(IOpticsAnalysis analysis, string json)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize(json, analysis.SettingsType, Strict)
                   ?? throw new ArgumentException($"The settings for '{analysis.Name}' are empty.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The settings for '{analysis.Name}' ({analysis.Id}) do not read: {ex.Message}", ex);
        }
    }

    /// <summary>The settings a record has, by the name <see cref="WithOverrides"/> takes, with their types.</summary>
    public static IReadOnlyList<(string Name, Type Type)> SettingNames(IOpticsAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return Settable(analysis.SettingsType).Select(p => (p.Name, p.PropertyType)).ToArray();
    }

    /// <summary>
    /// <paramref name="settings"/> with the named settings replaced: names are the settings
    /// record's property names (any case), values in the invariant culture, an enum by its name, a
    /// boolean as true or false. A name the record does not have, or a value of the wrong kind,
    /// throws <see cref="ArgumentException"/> naming the analysis and the setting.
    /// </summary>
    public static object WithOverrides(IOpticsAnalysis analysis, object settings, IEnumerable<KeyValuePair<string, string>> overrides)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(overrides);
        var properties = Settable(analysis.SettingsType);
        var node = JsonSerializer.SerializeToNode(settings, analysis.SettingsType, OpticsAnalysisSettingsCodec.SerializerOptions)?.AsObject()
                   ?? throw new ArgumentException($"The settings of '{analysis.Name}' are not a record.");
        bool any = false;
        foreach (var (name, text) in overrides)
        {
            var property = properties.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"'{analysis.Name}' ({analysis.Id}) has no setting '{name}'. Its settings: "
                                               + string.Join(", ", properties.Select(p => p.Name)) + ".");
            node[JsonName(property)] = Value(analysis, property, text.Trim());
            any = true;
        }
        if (!any) return settings;
        return node.Deserialize(analysis.SettingsType, Strict)
               ?? throw new ArgumentException($"The settings of '{analysis.Name}' could not be rebuilt.");
    }

    /// <summary>
    /// Computes each analysis in order on <paramref name="context"/>. Each entry is titled as the
    /// app titles a window: the analysis's name, numbered "(2)", "(3)" when it appears again. A
    /// failed compute is an entry without a result and the status "Failed: …", as a failed window
    /// shows; cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static IReadOnlyList<OpticsAnalysisReportEntry> Run(OpticsAnalysisContext context,
        IReadOnlyList<(IOpticsAnalysis Analysis, object Settings)> runs, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runs);
        var entries = new List<OpticsAnalysisReportEntry>();
        var titles = new List<string>();
        foreach (var (analysis, settings) in runs)
        {
            cancellation.ThrowIfCancellationRequested();
            int same = runs.Take(entries.Count).Count(r => ReferenceEquals(r.Analysis, analysis));
            string title = same == 0 ? analysis.Name : $"{analysis.Name} ({same + 1})";
            while (titles.Contains(title)) title = $"{analysis.Name} ({++same + 1})";
            titles.Add(title);
            try
            {
                var result = analysis.Compute(context, settings, cancellation);
                entries.Add(new OpticsAnalysisReportEntry(title, result, "Up to date"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { entries.Add(new OpticsAnalysisReportEntry(title, null, "Failed: " + ex.Message)); }
        }
        return entries;
    }

    /// <summary>The composite report of <paramref name="entries"/> for the context's project and
    /// mode: <see cref="OpticsAnalysisReport.Compose"/>, the function the app's Save report uses.</summary>
    public static StudyReport Report(OpticsAnalysisContext context, IReadOnlyList<OpticsAnalysisReportEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(context);
        return OpticsAnalysisReport.Compose(context.ProjectName, context.Mode, entries);
    }

    // ---------------------------------------------------------------- helpers

    private static readonly JsonSerializerOptions Strict = new(OpticsAnalysisSettingsCodec.SerializerOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = true,
    };

    private static PropertyInfo[] Settable(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .ToArray();

    private static string JsonName(PropertyInfo p) =>
        p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
        ?? OpticsAnalysisSettingsCodec.SerializerOptions.PropertyNamingPolicy?.ConvertName(p.Name) ?? p.Name;

    private static JsonNode? Value(IOpticsAnalysis analysis, PropertyInfo property, string text)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        bool nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null || !property.PropertyType.IsValueType;
        if (nullable && text.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        var ci = CultureInfo.InvariantCulture;
        string Bad(string kind) => $"Setting '{property.Name}' of '{analysis.Name}' needs {kind}, not '{text}'.";
        if (type == typeof(string)) return JsonValue.Create(text);
        if (type == typeof(bool))
            return bool.TryParse(text, out bool b) ? JsonValue.Create(b) : throw new ArgumentException(Bad("true or false"));
        if (type.IsEnum)
        {
            var names = Enum.GetNames(type);
            string? match = names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            return match is not null ? JsonValue.Create(match) : throw new ArgumentException(Bad("one of " + string.Join(", ", names)));
        }
        if (type == typeof(int))
            return int.TryParse(text, NumberStyles.Integer, ci, out int i) ? JsonValue.Create(i) : throw new ArgumentException(Bad("a whole number"));
        if (type == typeof(long))
            return long.TryParse(text, NumberStyles.Integer, ci, out long l) ? JsonValue.Create(l) : throw new ArgumentException(Bad("a whole number"));
        if (type == typeof(double))
            return double.TryParse(text, NumberStyles.Float, ci, out double d) && double.IsFinite(d) ? JsonValue.Create(d) : throw new ArgumentException(Bad("a finite number"));
        if (type == typeof(float))
            return float.TryParse(text, NumberStyles.Float, ci, out float f) && float.IsFinite(f) ? JsonValue.Create(f) : throw new ArgumentException(Bad("a finite number"));
        throw new ArgumentException($"Setting '{property.Name}' of '{analysis.Name}' is a {type.Name}, which the command line cannot set.");
    }
}

/// <summary>
/// The data of an analysis plot as CSV (OP-97), through <see cref="PlotCsv"/>'s format (RFC 4180,
/// invariant culture, shortest round-trip numbers, '#' metadata lines): a line plot and a scatter
/// plot as long-form points ("series,x,y", a scatter point's label in a fourth column), a map as
/// a matrix with the column x values in the header and each row's y first, rows in ascending y.
/// Deterministic: the same plot gives the same bytes.
/// </summary>
public static class AnalysisPlotCsv
{
    public static string Write(AnalysisPlot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);
        switch (plot)
        {
            case LinePlot line:
            {
                var comments = new List<string> { line.Title, $"x: {line.XLabel}", $"y: {line.YLabel}" };
                if (line.LogX) comments.Add("x axis logarithmic");
                if (line.LogY) comments.Add("y axis logarithmic");
                var rows = new List<IReadOnlyList<string>>();
                foreach (var curve in line.Curves)
                {
                    if (curve.X.Count != curve.Y.Count)
                        throw new ArgumentException($"Plot '{line.Title}', curve '{curve.Name}': {curve.X.Count} x values against {curve.Y.Count} y values.");
                    for (int i = 0; i < curve.X.Count; i++)
                        rows.Add(new[] { curve.Name, PlotCsv.Number(curve.X[i]), PlotCsv.Number(curve.Y[i]) });
                }
                return PlotCsv.Table(new[] { "series", line.XLabel, line.YLabel }, rows, comments);
            }
            case AnalysisScatterPlot scatter:
            {
                var rows = scatter.Series.SelectMany(s => s.Points.Select(p =>
                    (IReadOnlyList<string>)new[] { s.Name, PlotCsv.Number(p.X), PlotCsv.Number(p.Y), p.Label }));
                return PlotCsv.Table(new[] { "series", scatter.XLabel, scatter.YLabel, "label" }, rows,
                    new[] { scatter.Title, $"x: {scatter.XLabel}", $"y: {scatter.YLabel}" });
            }
            case AnalysisMapPlot map:
                return PlotCsv.Grid(map.Field, comments: new[]
                {
                    map.Title, $"x: {map.XLabel}", $"y: {map.YLabel}", $"values: {map.ValueUnit}",
                    "NaN marks a sample with no data (outside the pupil or the aperture)."
                });
            default:
                throw new NotSupportedException($"Plot '{plot.Title}' is a {plot.GetType().Name}, which has no CSV form yet.");
        }
    }
}
