using System.ComponentModel;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// OP-40: RMS vs Field, RMS vs Wavelength, RMS vs Focus and RMS Field Map (Ansys OpticStudio
// 2025 R2 help pages of those names, saved under plans/sources/optics/op-40-help/;
// docs/Optics-Conventions.md §34). The computation is RmsCore's.

/// <summary>Settings of the <see cref="RmsVsFieldAnalysis"/>.</summary>
public sealed record RmsVsFieldSettings
{
    [DisplayName("Method")]
    [Description("Gaussian quadrature (rings and arms; unvignetted pupils) or rectangular array (a square grid; vignetted pupils).")]
    public PupilIntegration Method { get; init; } = PupilIntegration.GaussianQuadrature;

    [DisplayName("Ray density")]
    [Description("Gaussian quadrature: rings, 1 to 20 (4 arms per ring). Rectangular array: rays across the grid, 2 to 1001.")]
    public int RayDensity { get; init; } = 6;

    [DisplayName("Field density")]
    [Description("Points from the axis to the largest defined field in the scan direction, evenly spaced; 2 to 100.")]
    public int FieldDensity { get; init; } = 20;

    [DisplayName("Data")]
    [Description("RMS wavefront error, RMS spot radius, x or y, or the Strehl ratio.")]
    public RmsData Data { get; init; } = RmsData.WavefrontError;

    [DisplayName("PSF sampling")]
    [Description("Strehl ratio only: an n × n grid of pupil nodes, zero-padded to 2n × 2n (as the Strehl ratio analysis); 16 to 1024.")]
    public int PsfSampling { get; init; } = RmsCore.DefaultPsfSampling;

    [DisplayName("Refer to")]
    [Description("Chief ray (piston removed) or centroid (piston and tilt removed).")]
    public RmsReference Reference { get; init; } = RmsReference.ChiefRay;

    [DisplayName("Orientation")]
    [Description("The field direction scanned.")]
    public FieldScan Scan { get; init; } = FieldScan.PlusY;

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for every wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Curves")]
    [Description("With every wavelength: each wavelength and the polychromatic curve, the polychromatic curve only, or the wavelengths only.")]
    public RmsWavelengthCurves Curves { get; init; } = RmsWavelengthCurves.MonochromaticAndPolychromatic;

    [DisplayName("Use dashes")]
    [Description("Draw every other curve dashed.")]
    public bool UseDashes { get; init; }

    [DisplayName("Show diffraction limit")]
    [Description("Spot: 1.22 λ times the on-axis working F/#. Wavefront: 0.072 waves. Strehl ratio: 0.8.")]
    public bool ShowDiffractionLimit { get; init; }

    [DisplayName("Remove vignetting factors")]
    [Description("Replace the fields' vignetting factors with surface apertures first; off traces through the factors.")]
    public bool RemoveVignettingFactors { get; init; }
}

/// <summary>Settings of the <see cref="RmsVsWavelengthAnalysis"/>.</summary>
public sealed record RmsVsWavelengthSettings
{
    [DisplayName("Method")]
    [Description("Gaussian quadrature (rings and arms; unvignetted pupils) or rectangular array (a square grid; vignetted pupils).")]
    public PupilIntegration Method { get; init; } = PupilIntegration.GaussianQuadrature;

    [DisplayName("Ray density")]
    [Description("Gaussian quadrature: rings, 1 to 20 (4 arms per ring). Rectangular array: rays across the grid, 2 to 1001.")]
    public int RayDensity { get; init; } = 6;

    [DisplayName("Wave density")]
    [Description("Wavelengths from the shortest to the longest in the table, evenly spaced; 2 to 100.")]
    public int WaveDensity { get; init; } = 20;

    [DisplayName("Data")]
    [Description("RMS wavefront error, RMS spot radius, x or y, or the Strehl ratio.")]
    public RmsData Data { get; init; } = RmsData.WavefrontError;

    [DisplayName("PSF sampling")]
    [Description("Strehl ratio only: an n × n grid of pupil nodes, zero-padded to 2n × 2n (as the Strehl ratio analysis); 16 to 1024.")]
    public int PsfSampling { get; init; } = RmsCore.DefaultPsfSampling;

    [DisplayName("Refer to")]
    [Description("Chief ray (piston removed) or centroid (piston and tilt removed).")]
    public RmsReference Reference { get; init; } = RmsReference.ChiefRay;

    [DisplayName("Field")]
    [Description("Row of the field table, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Use dashes")]
    [Description("Draw every other curve dashed.")]
    public bool UseDashes { get; init; }

    [DisplayName("Show diffraction limit")]
    [Description("Spot: 1.22 λ times the on-axis working F/# at each wavelength. Wavefront: 0.072 waves. Strehl ratio: 0.8.")]
    public bool ShowDiffractionLimit { get; init; }
}

/// <summary>Settings of the <see cref="RmsVsFocusAnalysis"/>.</summary>
public sealed record RmsVsFocusSettings
{
    [DisplayName("Method")]
    [Description("Gaussian quadrature (rings and arms; unvignetted pupils) or rectangular array (a square grid; vignetted pupils).")]
    public PupilIntegration Method { get; init; } = PupilIntegration.GaussianQuadrature;

    [DisplayName("Ray density")]
    [Description("Gaussian quadrature: rings, 1 to 20 (4 arms per ring). Rectangular array: rays across the grid, 2 to 1001.")]
    public int RayDensity { get; init; } = 6;

    [DisplayName("Focus density")]
    [Description("Focus shifts from the minimum to the maximum, evenly spaced; 2 to 100.")]
    public int FocusDensity { get; init; } = 21;

    [DisplayName("Data")]
    [Description("RMS wavefront error, RMS spot radius, x or y, or the Strehl ratio.")]
    public RmsData Data { get; init; } = RmsData.WavefrontError;

    [DisplayName("PSF sampling")]
    [Description("Strehl ratio only: an n × n grid of pupil nodes, zero-padded to 2n × 2n (as the Strehl ratio analysis); 16 to 1024.")]
    public int PsfSampling { get; init; } = RmsCore.DefaultPsfSampling;

    [DisplayName("Refer to")]
    [Description("Chief ray (piston removed) or centroid (piston and tilt removed).")]
    public RmsReference Reference { get; init; } = RmsReference.ChiefRay;

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the polychromatic RMS over every wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Field")]
    [Description("Row of the field table, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Min focus")]
    [Description("The smallest focus shift, in lens units, added to the thickness before the image surface.")]
    public double MinFocus { get; init; } = -0.1;

    [DisplayName("Max focus")]
    [Description("The largest focus shift, in lens units.")]
    public double MaxFocus { get; init; } = 0.1;

    [DisplayName("Use dashes")]
    [Description("Draw every other curve dashed.")]
    public bool UseDashes { get; init; }

    [DisplayName("Show diffraction limit")]
    [Description("Spot: 1.22 λ times the on-axis working F/# (primary wavelength when polychromatic). Wavefront: 0.072 waves. Strehl ratio: 0.8.")]
    public bool ShowDiffractionLimit { get; init; }
}

/// <summary>Settings of the <see cref="RmsFieldMapAnalysis"/>.</summary>
public sealed record RmsFieldMapSettings
{
    [DisplayName("Method")]
    [Description("Gaussian quadrature (rings and arms; unvignetted pupils) or rectangular array (a square grid; vignetted pupils).")]
    public PupilIntegration Method { get; init; } = PupilIntegration.GaussianQuadrature;

    [DisplayName("Ray density")]
    [Description("Gaussian quadrature: rings, 1 to 20 (4 arms per ring). Rectangular array: rays across the grid, 2 to 1001.")]
    public int RayDensity { get; init; } = 6;

    [DisplayName("Data")]
    [Description("RMS wavefront error, RMS spot radius, x or y, the Strehl ratio, or (rectangular array, one wavelength) the wavefront's peak to valley.")]
    public RmsMapData Data { get; init; } = RmsMapData.WavefrontError;

    [DisplayName("PSF sampling")]
    [Description("Strehl ratio only: an n × n grid of pupil nodes, zero-padded to 2n × 2n (as the Strehl ratio analysis); 16 to 1024.")]
    public int PsfSampling { get; init; } = RmsCore.DefaultPsfSampling;

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the polychromatic RMS over every wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Field")]
    [Description("The reference field row the map is centred on, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Refer to")]
    [Description("Chief ray (piston removed) or centroid (piston and tilt removed).")]
    public RmsReference Reference { get; init; } = RmsReference.ChiefRay;

    [DisplayName("X field size")]
    [Description("Half-width of the map in x, in field units (degrees or lens units); 0 for the largest radial field.")]
    public double XFieldSize { get; init; }

    [DisplayName("Y field size")]
    [Description("Half-width of the map in y, in field units (degrees or lens units); 0 for the largest radial field.")]
    public double YFieldSize { get; init; }

    [DisplayName("X field sampling")]
    [Description("Points across the map in x; 2 to 101.")]
    public int XFieldSampling { get; init; } = 11;

    [DisplayName("Y field sampling")]
    [Description("Points across the map in y; 2 to 101.")]
    public int YFieldSampling { get; init; } = 11;

    [DisplayName("Show as")]
    [Description("False-colour image or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Remove vignetting factors")]
    [Description("Replace the fields' vignetting factors with surface apertures first; off traces through the factors.")]
    public bool RemoveVignettingFactors { get; init; }
}

/// <summary>What the four RMS analyses share in presenting their results.</summary>
internal static class RmsPresentation
{
    public static string Wave(OpticalSystemData system, double micrometers) => $"{StudyReport.Number(micrometers, "g6")} µm";

    public static string FieldText(RmsSetup setup, double x, double y) =>
        $"field ({StudyReport.Number(setup.FieldShown(x), "g6")}, {StudyReport.Number(setup.FieldShown(y), "g6")}{(setup.FieldUnit == "°" ? "°" : " " + setup.FieldUnit)})";

    /// <summary>The curves of an analysis over wavelength rows: one per row and/or one
    /// polychromatic, as the settings say.</summary>
    public static List<(string Name, int[] Rows, bool Poly)> WavelengthCurves(OpticalSystemData system, int setting,
        RmsWavelengthCurves curves, string who)
    {
        var rows = FieldScanCommon.Rows(setting, system, who, zeroIsAll: true);
        var list = new List<(string, int[], bool)>();
        if (setting > 0)
        {
            list.Add((FieldScanCommon.WavelengthLabel(system, rows[0]), rows, false));
            return list;
        }
        if (curves != RmsWavelengthCurves.PolychromaticOnly || rows.Length == 1)
            foreach (int r in rows) list.Add((FieldScanCommon.WavelengthLabel(system, r), new[] { r }, false));
        if (curves != RmsWavelengthCurves.MonochromaticOnly && rows.Length > 1) list.Add(("Polychromatic", rows, true));
        return list;
    }

    public static IReadOnlyList<int> FieldRows(OpticalSystemData system, int setting, string who)
    {
        int count = system.Fields.Points.Count;
        if (setting < 0 || setting > count)
            throw new ArgumentException($"{who}: the field must be 0 (all) or 1 to {count} (it is {setting}).");
        return setting == 0 ? Enumerable.Range(0, count).ToArray() : new[] { setting - 1 };
    }

    public static string FieldRowLabel(RmsSetup setup, int row)
    {
        var p = setup.System.Fields.Points[row];
        return $"Field {row + 1} ({StudyReport.Number(setup.FieldShown(p.X), "g6")}, {StudyReport.Number(setup.FieldShown(p.Y), "g6")}{(setup.FieldUnit == "°" ? "°" : " " + setup.FieldUnit)})";
    }

    /// <summary>The result: a table (x column, one column per curve), the failures, the plot.</summary>
    public static OpticsAnalysisResult Result(string name, string title, string xLabel, IReadOnlyList<double> x,
        IReadOnlyList<string> curveNames, RmsValue[,] values, RmsQuantity q, bool dashes, double diffractionLimit,
        IReadOnlyList<double>? diffractionCurve, IReadOnlyList<(string Key, string Value)> pairs, List<string> assumptions)
    {
        int nc = curveNames.Count, nx = x.Count;
        var rows = new List<IReadOnlyList<string>>();
        var failures = new List<string>();
        var warnings = new List<string>();
        var plotted = new List<AnalysisCurve>();
        for (int i = 0; i < nx; i++)
        {
            var row = new List<string> { StudyReport.Number(x[i], "g8") };
            for (int c = 0; c < nc; c++) row.Add(RmsCore.Text(q, values[c, i]));
            if (diffractionCurve is not null) row.Add(FieldScanCommon.N(diffractionCurve[i], "g8"));
            rows.Add(row);
        }
        for (int c = 0; c < nc; c++)
        {
            var y = new double[nx];
            for (int i = 0; i < nx; i++)
            {
                var v = values[c, i];
                y[i] = v.Succeeded ? RmsCore.Shown(q, v.Value) : double.NaN;
                if (!v.Succeeded) failures.Add($"{curveNames[c]}: {v.Failure}");
                else if (v.RayFailures.Count > 0) failures.Add($"{curveNames[c]}, {StudyReport.Number(x[i], "g6")}: {v.Launched - v.Used} of {v.Launched} rays left out ({string.Join("; ", v.RayFailures)}).");
                warnings.AddRange(v.Warnings.Select(w => "Warning: " + w));
            }
            plotted.Add(new AnalysisCurve(curveNames[c], x, y, dashes && c % 2 == 1));
        }
        var headers = new List<string> { xLabel };
        headers.AddRange(curveNames.Select(n => n + RmsCore.UnitSuffix(q)));
        if (diffractionCurve is not null)
        {
            headers.Add("Diffraction limit" + RmsCore.UnitSuffix(q));
            plotted.Add(new AnalysisCurve("Diffraction limit", x, diffractionCurve, true));
        }
        else if (double.IsFinite(diffractionLimit))
        {
            plotted.Add(new AnalysisCurve("Diffraction limit", new[] { x[0], x[^1] }, new[] { diffractionLimit, diffractionLimit }, true));
        }
        var allPairs = pairs.ToList();
        if (double.IsFinite(diffractionLimit)) allPairs.Add(("Diffraction limit", RmsCore.WithUnit(q, diffractionLimit, "g6")));
        if (failures.Count == 0) failures.Add(NoFailures(q));
        failures.AddRange(warnings);
        assumptions.AddRange(warnings);
        var sections = new[]
        {
            new ReportSection(title) { Pairs = allPairs, TableHeaders = headers, TableRows = rows, Lines = failures },
        };
        return OpticsAnalysisResult.FromSections(name, sections, assumptions, new AnalysisPlot[]
        {
            new LinePlot(title, xLabel, RmsCore.AxisLabel(q), plotted),
        });
    }

    /// <summary>The line under the table when nothing failed.</summary>
    public static string NoFailures(RmsQuantity q) => q == RmsQuantity.Strehl
        ? "Every point has a value."
        : "Every point has a value and every ray launched gave one.";

    /// <summary>The diffraction limit line of a quantity at one wavelength (shown units), or NaN when not asked.</summary>
    public static double DiffractionLimit(bool show, RmsSetup setup, RmsQuantity q, int row, List<string> notes)
    {
        if (!show) return double.NaN;
        if (!RmsCore.IsSpot(q)) return RmsCore.FlatDiffractionLimit(q);
        return RmsCore.Shown(q, RmsCore.SpotDiffractionLimit(setup, row, null, notes));
    }

    public static string DiffractionAssumption(bool show, RmsQuantity q) => !show ? "No diffraction limit drawn."
        : RmsCore.IsSpot(q)
            ? "Diffraction limit (help, \"Show Diffraction Limit\"): 1.22 times the working F/# on axis times the wavelength (primary when polychromatic), the change of F/# with field ignored; an approximate indicator only."
            : q == RmsQuantity.Strehl
                ? "Diffraction limit (help, \"Show Diffraction Limit\"): a Strehl ratio of 0.8; an approximate indicator only."
                : "Diffraction limit (help, \"Show Diffraction Limit\"): 0.072 waves; an approximate indicator only.";
}

/// <summary>OpticStudio's RMS vs. Field (OP-40): the RMS wavefront error or spot radius at
/// evenly spaced fields from the axis to the largest defined field in one direction, per
/// wavelength and polychromatic (<see cref="RmsCore"/>).</summary>
public sealed class RmsVsFieldAnalysis : OpticsAnalysis<RmsVsFieldSettings>
{
    public const string SavedId = "RmsVsField";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public RmsVsFieldAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "RMS vs field";
    public override string Category => "RMS";

    /// <summary>The thread limit of the evaluation (null: none); the result is the same bits at any value.</summary>
    internal int? MaxDegreeOfParallelism { get; init; }

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, RmsVsFieldSettings settings, CancellationToken cancellation)
    {
        const string who = "RMS vs field";
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return FieldScanCommon.NoSurfaces(Name);
        // The help: "A maximum of 100 field points is allowed."
        FieldScanCommon.CheckCount(settings.FieldDensity, 2, 100, who, "field density");
        var rule = RmsCore.Rule(settings.Method, settings.RayDensity, who);
        var q = RmsCore.Quantity(settings.Data);
        RmsCore.CheckPsfSampling(q, settings.PsfSampling, who);
        var curves = RmsPresentation.WavelengthCurves(system, settings.Wavelength, settings.Curves, who);
        var setup = RmsCore.Prepare(lens, system, settings.RemoveVignettingFactors, who, notes, cancellation);

        // The help: "the data will only be computed to the limits of the defined fields in the selected direction."
        var (dx, dy) = FieldScans.Direction(settings.Scan);
        double extent = setup.System.Fields.Points.Max(p => p.X * dx + p.Y * dy);
        if (!(extent > 0))
            throw new ArgumentException($"{who}: no defined field lies in the {FieldScans.Label(settings.Scan)} direction, so there is nothing to scan; add a field there or pick another orientation.");
        var h = FieldScanCommon.Even(settings.FieldDensity);
        var points = new List<RmsPoint>();
        foreach (var (name, rows, poly) in curves)
            foreach (double v in h)
            {
                double fx = v * extent * dx, fy = v * extent * dy;
                points.Add(new RmsPoint
                {
                    FieldX = fx, FieldY = fy, Rows = rows, Polychromatic = poly,
                    Label = $"{name}, {RmsPresentation.FieldText(setup, fx, fy)}",
                });
            }
        var values = RmsCore.EvaluateAll(setup, points, q, settings.Reference, rule, cancellation, MaxDegreeOfParallelism, settings.PsfSampling);
        var grid = new RmsValue[curves.Count, h.Length];
        for (int c = 0; c < curves.Count; c++) for (int i = 0; i < h.Length; i++) grid[c, i] = values[c * h.Length + i];

        int limitRow = settings.Wavelength > 0 ? settings.Wavelength - 1 : setup.System.Wavelengths.PrimaryIndex;
        double limit = RmsPresentation.DiffractionLimit(settings.ShowDiffractionLimit, setup, q, limitRow, notes);
        string axis = FieldScans.AlongX(settings.Scan) ? "X" : "Y";
        string xLabel = $"{FieldScans.Label(settings.Scan)} field ({setup.FieldUnit})";
        var x = h.Select(v => setup.FieldShown(v * extent)).ToArray();
        var assumptions = RmsCore.Assumptions(setup, q, settings.Reference, rule, settings.PsfSampling);
        assumptions.Add($"Fields from the axis to {StudyReport.Number(setup.FieldShown(extent), "g6")} {setup.FieldUnit} along {FieldScans.Label(settings.Scan)} ({axis}), the largest defined field in that direction; {h.Length} points, joined by straight lines.");
        assumptions.Add(RmsPresentation.DiffractionAssumption(settings.ShowDiffractionLimit, q));
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(notes);
        return RmsPresentation.Result(Name, $"{RmsCore.AxisLabel(q)} vs field", xLabel, x, curves.Select(c => c.Name).ToArray(), grid, q,
            settings.UseDashes, limit, null, new[] { ("Data", RmsCore.AxisLabel(q)), ("Refer to", settings.Reference == RmsReference.ChiefRay ? "chief ray" : "centroid") }, assumptions);
    }
}

/// <summary>OpticStudio's RMS vs. Wavelength (OP-40): the RMS at evenly spaced wavelengths from
/// the shortest to the longest in the table, one curve per field.</summary>
public sealed class RmsVsWavelengthAnalysis : OpticsAnalysis<RmsVsWavelengthSettings>
{
    public const string SavedId = "RmsVsWavelength";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public RmsVsWavelengthAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    /// <summary>The thread limit of the evaluation (null: none); the result is the same bits at any value.</summary>
    internal int? MaxDegreeOfParallelism { get; init; }

    public override string Id => SavedId;
    public override string Name => "RMS vs wavelength";
    public override string Category => "RMS";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, RmsVsWavelengthSettings settings, CancellationToken cancellation)
    {
        const string who = "RMS vs wavelength";
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return FieldScanCommon.NoSurfaces(Name);
        // The help: "A maximum of 100 points is allowed."
        FieldScanCommon.CheckCount(settings.WaveDensity, 2, 100, who, "wave density");
        var rule = RmsCore.Rule(settings.Method, settings.RayDensity, who);
        var q = RmsCore.Quantity(settings.Data);
        RmsCore.CheckPsfSampling(q, settings.PsfSampling, who);
        var fields = RmsPresentation.FieldRows(system, settings.Field, who);
        var setup = RmsCore.Prepare(lens, system, removeVignettingFactors: false, who, notes, cancellation);

        // The help: "The wavelength range is determined by the minimum and maximum wavelengths defined."
        double low = setup.System.Wavelengths.Wavelengths.Min(w => w.Micrometers);
        double high = setup.System.Wavelengths.Wavelengths.Max(w => w.Micrometers);
        var lambdas = low == high ? new[] { low } : FieldScanCommon.Even(settings.WaveDensity, high - low).Select(d => low + d).ToArray();
        if (lambdas.Length > 1) lambdas[^1] = high;
        if (low == high) notes.Add("Every wavelength in the table is the same, so there is one point.");

        var points = new List<RmsPoint>();
        foreach (int f in fields)
            foreach (double um in lambdas)
            {
                var p = setup.System.Fields.Points[f];
                points.Add(new RmsPoint
                {
                    FieldX = p.X, FieldY = p.Y, Micrometers = um,
                    Label = $"{RmsPresentation.FieldRowLabel(setup, f)}, {RmsPresentation.Wave(setup.System, um)}",
                });
            }
        var values = RmsCore.EvaluateAll(setup, points, q, settings.Reference, rule, cancellation, MaxDegreeOfParallelism, settings.PsfSampling);
        var grid = new RmsValue[fields.Count, lambdas.Length];
        for (int c = 0; c < fields.Count; c++) for (int i = 0; i < lambdas.Length; i++) grid[c, i] = values[c * lambdas.Length + i];

        double[]? limitCurve = null;
        double limit = double.NaN;
        if (settings.ShowDiffractionLimit)
        {
            if (RmsCore.IsSpot(q))
                limitCurve = lambdas.Select(um => RmsCore.Shown(q, RmsCore.SpotDiffractionLimit(setup, null, um, notes))).ToArray();
            else limit = RmsCore.FlatDiffractionLimit(q);
        }
        var assumptions = RmsCore.Assumptions(setup, q, settings.Reference, rule, settings.PsfSampling);
        assumptions.Add($"Wavelengths from {StudyReport.Number(low, "g6")} to {StudyReport.Number(high, "g6")} µm (the table's shortest and longest, in the system's air), {lambdas.Length} points; each is monochromatic, referred to its own chief ray.");
        assumptions.Add(RmsPresentation.DiffractionAssumption(settings.ShowDiffractionLimit, q)
            + (settings.ShowDiffractionLimit && RmsCore.IsSpot(q) ? " Here at each wavelength, a curve." : ""));
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(notes);
        return RmsPresentation.Result(Name, $"{RmsCore.AxisLabel(q)} vs wavelength", "Wavelength (µm)", lambdas,
            fields.Select(f => RmsPresentation.FieldRowLabel(setup, f)).ToArray(), grid, q, settings.UseDashes, limit, limitCurve,
            new[] { ("Data", RmsCore.AxisLabel(q)), ("Refer to", settings.Reference == RmsReference.ChiefRay ? "chief ray" : "centroid") }, assumptions);
    }
}

/// <summary>OpticStudio's RMS vs. Focus (OP-40): the RMS at evenly spaced focus shifts added to
/// the thickness before the image surface, one curve per field.</summary>
public sealed class RmsVsFocusAnalysis : OpticsAnalysis<RmsVsFocusSettings>
{
    public const string SavedId = "RmsVsFocus";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public RmsVsFocusAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    /// <summary>The thread limit of the evaluation (null: none); the result is the same bits at any value.</summary>
    internal int? MaxDegreeOfParallelism { get; init; }

    public override string Id => SavedId;
    public override string Name => "RMS vs focus";
    public override string Category => "RMS";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, RmsVsFocusSettings settings, CancellationToken cancellation)
    {
        const string who = "RMS vs focus";
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return FieldScanCommon.NoSurfaces(Name);
        // The help: "A maximum of 100 points is allowed."
        FieldScanCommon.CheckCount(settings.FocusDensity, 2, 100, who, "focus density");
        if (!double.IsFinite(settings.MinFocus) || !double.IsFinite(settings.MaxFocus) || !(settings.MaxFocus > settings.MinFocus))
            throw new ArgumentException($"{who}: the max focus must be a number above the min focus (they are {settings.MinFocus} and {settings.MaxFocus}).");
        var rule = RmsCore.Rule(settings.Method, settings.RayDensity, who);
        var q = RmsCore.Quantity(settings.Data);
        RmsCore.CheckPsfSampling(q, settings.PsfSampling, who);
        var fields = RmsPresentation.FieldRows(system, settings.Field, who);
        var rows = FieldScanCommon.Rows(settings.Wavelength, system, who, zeroIsAll: true);
        bool poly = settings.Wavelength == 0 && rows.Length > 1;
        var setup = RmsCore.Prepare(lens, system, removeVignettingFactors: false, who, notes, cancellation);
        var unit = setup.Unit;

        var focus = FieldScanCommon.Even(settings.FocusDensity, settings.MaxFocus - settings.MinFocus).Select(d => settings.MinFocus + d).ToArray();
        focus[^1] = settings.MaxFocus;
        string waveText = poly ? "polychromatic" : FieldScanCommon.WavelengthLabel(setup.System, rows[0]);
        var points = new List<RmsPoint>();
        foreach (int f in fields)
            foreach (double z in focus)
            {
                var p = setup.System.Fields.Points[f];
                points.Add(new RmsPoint
                {
                    FieldX = p.X, FieldY = p.Y, Rows = rows, Polychromatic = poly, Focus = OpticsConventions.ToMeters(z, unit),
                    Label = $"{RmsPresentation.FieldRowLabel(setup, f)}, {waveText}, focus {StudyReport.Number(z, "g6")} {ParaxialReport.UnitName(unit)}",
                });
            }
        var values = RmsCore.EvaluateAll(setup, points, q, settings.Reference, rule, cancellation, MaxDegreeOfParallelism, settings.PsfSampling);
        var grid = new RmsValue[fields.Count, focus.Length];
        for (int c = 0; c < fields.Count; c++) for (int i = 0; i < focus.Length; i++) grid[c, i] = values[c * focus.Length + i];

        int limitRow = poly ? setup.System.Wavelengths.PrimaryIndex : rows[0];
        double limit = RmsPresentation.DiffractionLimit(settings.ShowDiffractionLimit, setup, q, limitRow, notes);
        var assumptions = RmsCore.Assumptions(setup, q, settings.Reference, rule, settings.PsfSampling);
        assumptions.Add($"Focus shifts from {StudyReport.Number(settings.MinFocus, "g6")} to {StudyReport.Number(settings.MaxFocus, "g6")} {ParaxialReport.UnitName(unit)}, {focus.Length} points, each added to the thickness of surface {setup.Lens.LastSurfaceIndex} (the surface before the image), as the help says; with an odd number of mirrors that thickness is negative, so a negative shift moves the image away from the last surface.");
        assumptions.Add($"Wavelength: {waveText}.");
        assumptions.Add(RmsPresentation.DiffractionAssumption(settings.ShowDiffractionLimit, q));
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(notes);
        return RmsPresentation.Result(Name, $"{RmsCore.AxisLabel(q)} vs focus", $"Focus shift ({ParaxialReport.UnitName(unit)})", focus,
            fields.Select(f => RmsPresentation.FieldRowLabel(setup, f)).ToArray(), grid, q, settings.UseDashes, limit, null,
            new[] { ("Data", RmsCore.AxisLabel(q)), ("Wavelength", waveText), ("Refer to", settings.Reference == RmsReference.ChiefRay ? "chief ray" : "centroid") }, assumptions);
    }
}

/// <summary>OpticStudio's RMS Field Map (OP-40): the RMS over a rectangular grid of field
/// positions centred on a reference field, as a false-colour image or contours.</summary>
public sealed class RmsFieldMapAnalysis : OpticsAnalysis<RmsFieldMapSettings>
{
    public const string SavedId = "RmsFieldMap";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public RmsFieldMapAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    /// <summary>The thread limit of the evaluation (null: none); the result is the same bits at any value.</summary>
    internal int? MaxDegreeOfParallelism { get; init; }

    public override string Id => SavedId;
    public override string Name => "RMS field map";
    public override string Category => "RMS";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, RmsFieldMapSettings settings, CancellationToken cancellation)
    {
        const string who = "RMS field map";
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return FieldScanCommon.NoSurfaces(Name);
        FieldScanCommon.CheckCount(settings.XFieldSampling, 2, 101, who, "x field sampling");
        FieldScanCommon.CheckCount(settings.YFieldSampling, 2, 101, who, "y field sampling");
        var rule = RmsCore.Rule(settings.Method, settings.RayDensity, who);
        var rows = FieldScanCommon.Rows(settings.Wavelength, system, who, zeroIsAll: true);
        bool poly = settings.Wavelength == 0 && rows.Length > 1;
        var q = settings.Data switch
        {
            RmsMapData.WavefrontError => RmsQuantity.WavefrontRms,
            RmsMapData.SpotRadius => RmsQuantity.SpotRadius,
            RmsMapData.SpotX => RmsQuantity.SpotX,
            RmsMapData.SpotY => RmsQuantity.SpotY,
            RmsMapData.WavefrontPeakToValley => RmsQuantity.WavefrontPeakToValley,
            RmsMapData.StrehlRatio => RmsQuantity.Strehl,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Data, "Unknown RMS map data."),
        };
        RmsCore.CheckPsfSampling(q, settings.PsfSampling, who);
        if (q == RmsQuantity.WavefrontPeakToValley)
        {
            // The help: "If the method selected is rectangular array, then a rough estimate of the
            // PTV is also available"; "PTV require[s] a single wavelength to be selected".
            if (settings.Method != PupilIntegration.RectangularArray)
                throw new ArgumentException($"{who}: the wavefront peak to valley needs the rectangular array method (the help offers it with that method only).");
            if (poly)
                throw new ArgumentException($"{who}: the wavefront peak to valley needs a single wavelength; polychromatic computations are not supported.");
        }
        int count = system.Fields.Points.Count;
        if (settings.Field < 1 || settings.Field > count)
            throw new ArgumentException($"{who}: the reference field must be 1 to {count} (it is {settings.Field}).");
        if (!double.IsFinite(settings.XFieldSize) || !double.IsFinite(settings.YFieldSize) || settings.XFieldSize < 0 || settings.YFieldSize < 0)
            throw new ArgumentException($"{who}: the field sizes must be zero (automatic) or positive (they are {settings.XFieldSize} and {settings.YFieldSize}).");
        var setup = RmsCore.Prepare(lens, system, settings.RemoveVignettingFactors, who, notes, cancellation);

        double radial = setup.System.Fields.Points.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
        double hwx = settings.XFieldSize > 0 ? setup.FieldFromShown(settings.XFieldSize) : radial;
        double hwy = settings.YFieldSize > 0 ? setup.FieldFromShown(settings.YFieldSize) : radial;
        if (!(hwx > 0) || !(hwy > 0))
            throw new ArgumentException($"{who}: every defined field is on the axis, so an automatic field size is zero; give the x and y field sizes.");
        var centre = setup.System.Fields.Points[settings.Field - 1];
        int nx = settings.XFieldSampling, ny = settings.YFieldSampling;
        double X(int c) => c == nx - 1 ? centre.X + hwx : centre.X - hwx + 2 * hwx * c / (nx - 1);
        double Y(int r) => r == ny - 1 ? centre.Y + hwy : centre.Y - hwy + 2 * hwy * r / (ny - 1);
        string waveText = poly ? "polychromatic" : FieldScanCommon.WavelengthLabel(setup.System, rows[0]);
        var points = new List<RmsPoint>();
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++)
                points.Add(new RmsPoint
                {
                    FieldX = X(c), FieldY = Y(r), Rows = rows, Polychromatic = poly,
                    Label = $"{RmsPresentation.FieldText(setup, X(c), Y(r))}, {waveText}",
                });
        var values = RmsCore.EvaluateAll(setup, points, q, settings.Reference, rule, cancellation, MaxDegreeOfParallelism, settings.PsfSampling);

        var map = new double[ny, nx];
        var failures = new List<string>();
        var warnings = new List<string>();
        var tableRows = new List<IReadOnlyList<string>>();
        for (int r = ny - 1; r >= 0; r--)
        {
            var line = new List<string> { StudyReport.Number(setup.FieldShown(Y(r)), "g6") };
            for (int c = 0; c < nx; c++)
            {
                var v = values[r * nx + c];
                map[r, c] = v.Succeeded ? RmsCore.Shown(q, v.Value) : double.NaN;
                line.Add(RmsCore.Text(q, v));
                if (!v.Succeeded) failures.Add(v.Failure!);
                else if (v.RayFailures.Count > 0) failures.Add($"{points[r * nx + c].Label}: {v.Launched - v.Used} of {v.Launched} rays left out ({string.Join("; ", v.RayFailures)}).");
                warnings.AddRange(v.Warnings.Select(w => "Warning: " + w));
            }
            tableRows.Add(line);
        }
        var finite = values.Where(v => v.Succeeded).Select(v => RmsCore.Shown(q, v.Value)).ToArray();
        if (failures.Count == 0) failures.Add(RmsPresentation.NoFailures(q));
        failures.AddRange(warnings);
        string u = setup.FieldUnit == "°" ? "°" : setup.FieldUnit;
        var headers = new List<string> { $"Y \\ X field ({u})" };
        headers.AddRange(Enumerable.Range(0, nx).Select(c => StudyReport.Number(setup.FieldShown(X(c)), "g6")));
        var pairs = new List<(string, string)>
        {
            ("Data", RmsCore.AxisLabel(q)), ("Wavelength", waveText),
            ("Refer to", settings.Reference == RmsReference.ChiefRay ? "chief ray" : "centroid"),
            ("Reference field", RmsPresentation.FieldRowLabel(setup, settings.Field - 1)),
        };
        if (finite.Length > 0)
        {
            pairs.Add(("Minimum", RmsCore.WithUnit(q, finite.Min())));
            pairs.Add(("Maximum", RmsCore.WithUnit(q, finite.Max())));
        }
        var sections = new[]
        {
            new ReportSection($"{RmsCore.AxisLabel(q)} over the field") { Pairs = pairs, TableHeaders = headers, TableRows = tableRows, Lines = failures },
        };
        var assumptions = RmsCore.Assumptions(setup, q, settings.Reference, rule, settings.PsfSampling);
        assumptions.Add($"Field grid {nx} × {ny} centred on field {settings.Field}, half-widths {StudyReport.Number(setup.FieldShown(hwx), "g6")} and {StudyReport.Number(setup.FieldShown(hwy), "g6")} {setup.FieldUnit}"
            + (settings.XFieldSize > 0 && settings.YFieldSize > 0 ? "." : " (an automatic half-width is the largest radial defined field)."));
        if (q == RmsQuantity.WavefrontPeakToValley)
            assumptions.Add("Peak to valley over the rectangular array's rays after the reference's piston (and tilt) removal: a rough estimate that depends on two pupil points (help, \"RMS Field Map\").");
        assumptions.Add("Evaluated at the image surface only (the help's Surface setting, for intermediate images, is not built).");
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(notes);
        assumptions.AddRange(warnings);
        var plots = new List<AnalysisPlot>();
        if (finite.Length > 0)
            plots.Add(new AnalysisMapPlot($"{RmsCore.AxisLabel(q)}, {waveText}", $"X field ({u})", $"Y field ({u})",
                new GridField(map, setup.FieldShown(X(0)), setup.FieldShown(X(nx - 1)), setup.FieldShown(Y(0)), setup.FieldShown(Y(ny - 1))),
                q == RmsQuantity.Strehl ? "Strehl ratio" : RmsCore.Unit(q)) { Style = settings.ShowAs });
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}
