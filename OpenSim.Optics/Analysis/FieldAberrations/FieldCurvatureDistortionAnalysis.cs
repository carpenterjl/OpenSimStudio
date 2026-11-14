using System.ComponentModel;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// OP-37: Field Curvature and Distortion.

/// <summary>The reference a distortion is measured against (the help's "Distortion" setting;
/// SMIA-TV is in <see cref="GridDistortionAnalysis"/>).</summary>
public enum DistortionReference
{
    /// <summary>y_ref = f·tan θ (heights: linear in the object or paraxial image height).</summary>
    FTanTheta = 0,
    /// <summary>y_ref = f·θ; angle fields only.</summary>
    FTheta = 1,
    /// <summary>f·tan θ with the best-fit f.</summary>
    CalibratedFTanTheta = 2,
    /// <summary>f·θ with the best-fit f; angle fields only.</summary>
    CalibratedFTheta = 3,
}

/// <summary>Settings of the <see cref="FieldCurvatureDistortionAnalysis"/>.</summary>
public sealed record FieldCurvatureDistortionSettings
{
    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for every wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Field points")]
    [Description("Fields from the axis to the largest field along the scan, evenly spaced; 3 to 1001.")]
    public int FieldPoints { get; init; } = 21;

    [DisplayName("Scan")]
    [Description("The direction of the field scan; the tangential plane contains it.")]
    public FieldScan Scan { get; init; } = FieldScan.PlusY;

    [DisplayName("Distortion")]
    [Description("F-tan(θ), F-θ, or either with the best-fit (calibrated) focal length.")]
    public DistortionReference Distortion { get; init; } = DistortionReference.FTanTheta;

    [DisplayName("Distortion as length")]
    [Description("Show distortion as the length real minus reference height instead of a percentage.")]
    public bool DistortionAsLength { get; init; }

    [DisplayName("Ignore vignetting factors")]
    [Description("Clear the fields' vignetting factors first, so the chief ray goes through the pupil centre (the default).")]
    public bool IgnoreVignettingFactors { get; init; } = true;
}

/// <summary>One sample of the scan at one wavelength.</summary>
public sealed record FieldCurvaturePoint
{
    /// <summary>Normalised scan coordinate, 0 to 1.</summary>
    public required double H { get; init; }
    /// <summary>Tangential and sagittal focus from the image surface along its z, meters,
    /// positive along the direction of travel (<see cref="CoddingtonFoci.TangentialShift"/>).</summary>
    public required double Tangential { get; init; }
    public required double Sagittal { get; init; }
    /// <summary>Radial image-surface coordinate of the real chief ray, meters.</summary>
    public required double RealHeight { get; init; }
    /// <summary>The reference height f·g(H), meters.</summary>
    public required double ReferenceHeight { get; init; }
    /// <summary>100·(real − reference)/reference; at H = 0 its limit, 100·(k/f − 1).</summary>
    public required double DistortionPercent { get; init; }
}

/// <summary>The scan at one wavelength.</summary>
public sealed record FieldCurvatureCurve
{
    public required int Row { get; init; }
    public required IReadOnlyList<FieldCurvaturePoint> Points { get; init; }
    /// <summary>k = d(real height)/dg at the axis, by real chief rays at ±10⁻⁴ of the field.</summary>
    public required double SmallFieldSlope { get; init; }
    /// <summary>The f of the reference: k, or the best fit for a calibrated distortion.</summary>
    public required double ReferenceFocalLength { get; init; }
    /// <summary>Null when the field curvature was computed; otherwise why Coddington's equations
    /// could not be traced (the foci are then NaN and only the distortion is given).</summary>
    public string? FieldCurvatureFailure { get; init; }
}

/// <summary>
/// The numbers of OpticStudio's Field Curvature and Distortion (Ansys OpticStudio 2025 R2 help,
/// "Field Curvature and Distortion", ansyshelp.ansys.com, saved in
/// <c>plans/sources/optics/op-37-help/</c>), docs/Optics-Conventions.md §27.
/// <para>
/// <b>Field curvature.</b> At each field of the scan the real chief ray is traced and
/// Coddington's equations along it (<see cref="CoddingtonTrace"/>) give the tangential and
/// sagittal foci; the curve is their distance from the image surface along its z axis. The help
/// does the same with "a parabasal ray trace in the X and Y directions"; Coddington's equations
/// are the limit of that trace.
/// </para>
/// <para>
/// <b>Distortion.</b> The help: "Distortion = 100 × (y_chief − y_ref)/y_ref", heights "the image
/// surface radial coordinate"; "The reference ray height is computed by tracing a real ray from a
/// very small field height, and then scaling"; F-tan(θ) "y_ref = f tan θ", F-theta "y_ref = fθ"
/// ("only meaningful in systems with the object at infinity, when field heights are measured in
/// angles"); calibrated: "the 'best-fit' focal length is used rather than the system focal
/// length". Here g(H) is tan θ or θ (θ the object-space chief ray's angle to the axis) for angle
/// fields and the field height for height fields; the reference is f·g with f = k, the slope of
/// the real chief ray's image height against g from rays at H = ±10⁻⁴ (central difference); the
/// calibrated f is the least-squares fit Σ r·g / Σ g² over the scan, this program's reading of
/// "best fit". With F-tan(θ) and a scan beyond 89° the scan stops at 89°, as the help does.
/// </para>
/// </summary>
public static class FieldCurvatureDistortion
{
    /// <summary>The normalised step of the small-field reference rays.</summary>
    public const double SmallField = 1e-4;

    internal static IReadOnlyList<FieldCurvatureCurve> Compute(FieldScanSetup setup, IReadOnlyList<int> rows, FieldScan scan,
        double[] h, DistortionReference reference, CancellationToken cancellation)
    {
        var system = setup.System;
        bool angle = FieldTypes.IsAngle(system.Fields.Type);
        bool theta = reference is DistortionReference.FTheta or DistortionReference.CalibratedFTheta;
        if (theta && !angle)
            throw new ArgumentException("Field curvature and distortion: F-theta distortion needs angle fields (the help: it is \"only meaningful in systems with the object at infinity, when field heights are measured in angles\"); use F-tan(θ) for height fields.");
        var (dx, dy) = FieldScans.Direction(scan);
        var sagittal = FieldScans.Sagittal(scan);
        var launcher = setup.Launcher;
        double scanScale = setup.ScanScale(scan);

        // g(H): the linear field variable, signed along the scan.
        double G(double hh, Ray launch)
        {
            if (!angle) return hh * scanScale;
            var d = launch.Direction;
            double along = FieldScans.AlongX(scan) ? d.X : d.Y;
            double across = FieldScans.AlongX(scan) ? d.Y : d.X;
            double sign = hh < 0 ? -1 : 1;
            double tan = Math.Sqrt(along * along + across * across) / d.Z;
            return sign * (theta ? Math.Atan(tan) : tan);
        }

        var curves = new List<FieldCurvatureCurve>();
        foreach (int row in rows)
        {
            var trace = setup.Trace(row);
            string wl = FieldScanCommon.WavelengthLabel(system, row);
            Ray Chief(double hh, out Ray launch)
            {
                cancellation.ThrowIfCancellationRequested();
                launch = launcher.Launch(trace, hh * dx, hh * dy, 0, 0);
                var final = trace.Trace(launch);
                if (!final.IsOk)
                    throw new InvalidOperationException($"Field curvature and distortion: the chief ray at H = {hh:g6} ({wl}) does not reach the image surface ({FieldScanCommon.Failure(final)}).");
                return final;
            }

            var plus = Chief(SmallField, out var launchPlus);
            var minus = Chief(-SmallField, out var launchMinus);
            double dg = G(SmallField, launchPlus) - G(-SmallField, launchMinus);
            double k = Math.Sqrt(Sq(plus.Position.X - minus.Position.X) + Sq(plus.Position.Y - minus.Position.Y)) / Math.Abs(dg);
            if (!(k > 0) || !double.IsFinite(k))
                throw new InvalidOperationException($"Field curvature and distortion: the image height does not change with field near the axis ({wl}), so there is no reference height to measure distortion against.");

            var raw = new List<(double H, double T, double S, double R, double G)>();
            string? curvatureFailure = null;
            foreach (double hh in h)
            {
                var launch = launcher.Launch(trace, hh * dx, hh * dy, 0, 0);
                cancellation.ThrowIfCancellationRequested();
                var path = trace.TracePath(launch);
                string where = $"field H = {hh:g6} along {FieldScans.Label(scan)}, {wl}";
                if (!path.Final.IsOk)
                    throw new InvalidOperationException($"Field curvature and distortion: the chief ray at {where} does not reach the image surface ({FieldScanCommon.Failure(path.Final)}).");
                double t = double.NaN, s = double.NaN;
                if (curvatureFailure is null)
                {
                    try
                    {
                        var foci = CoddingtonTrace.Trace(trace, path, sagittal, where);
                        (t, s) = (foci.TangentialShift, foci.SagittalShift);
                    }
                    catch (InvalidOperationException e) { curvatureFailure = e.Message; }
                }
                var p = path.Final.Position;
                raw.Add((hh, t, s, Math.Sqrt(p.X * p.X + p.Y * p.Y), Math.Abs(G(hh, launch))));
            }

            double f = k;
            if (reference is DistortionReference.CalibratedFTanTheta or DistortionReference.CalibratedFTheta)
            {
                double num = 0, den = 0;
                foreach (var r in raw) { num += r.R * r.G; den += r.G * r.G; }
                if (!(den > 0))
                    throw new InvalidOperationException("Field curvature and distortion: calibrated distortion needs at least one off-axis field in the scan.");
                f = num / den;
            }
            var points = raw.Select(r =>
            {
                double refHeight = f * r.G;
                double pct = r.G == 0 ? 100 * (k / f - 1) : 100 * (r.R - refHeight) / refHeight;
                return new FieldCurvaturePoint
                {
                    H = r.H, Tangential = curvatureFailure is null ? r.T : double.NaN, Sagittal = curvatureFailure is null ? r.S : double.NaN,
                    RealHeight = r.R, ReferenceHeight = refHeight, DistortionPercent = pct,
                };
            }).ToArray();
            curves.Add(new FieldCurvatureCurve
            {
                Row = row, Points = points, SmallFieldSlope = k, ReferenceFocalLength = f, FieldCurvatureFailure = curvatureFailure,
            });
        }
        return curves;
    }

    private static double Sq(double v) => v * v;
}

/// <summary>
/// OpticStudio's Field Curvature and Distortion (OP-37): tangential and sagittal focus against
/// field by Coddington traces along the real chief ray, and the distortion against field in
/// percent or as a length, for F-tan(θ), F-θ and their calibrated forms
/// (<see cref="FieldCurvatureDistortion"/>). The help's display settings (maximum scales, dashes,
/// the classic view) belong to the plot window; its "Ref. Field", "H/W Aspect" and SMIA-TV
/// distortion are not built here (SMIA-TV is listed by <see cref="GridDistortionAnalysis"/>).
/// </summary>
public sealed class FieldCurvatureDistortionAnalysis : OpticsAnalysis<FieldCurvatureDistortionSettings>
{
    public const string SavedId = "FieldCurvatureDistortion";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for the project's saved surfaces.</param>
    public FieldCurvatureDistortionAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Field curvature and distortion";
    public override string Category => "Aberrations";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, FieldCurvatureDistortionSettings settings,
        CancellationToken cancellation)
    {
        const string who = "Field curvature and distortion";
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return FieldScanCommon.NoSurfaces(Name);
        FieldScanCommon.CheckCount(settings.FieldPoints, 3, 1001, who, "number of field points");
        FieldScanCommon.RefuseAfocal(system, who);
        var rows = FieldScanCommon.Rows(settings.Wavelength, system, who, zeroIsAll: true);
        if (settings.IgnoreVignettingFactors) system = VignettingTools.Clear(system);
        system = FieldScanCommon.ScanSystem(system, realImageHeightAsParaxial: true, notes);
        var setup = FieldScanCommon.Prepare(lens, system, notes, cancellation);
        double scale = setup.ScanScale(settings.Scan);
        if (!(scale > 0))
            throw new ArgumentException($"{who}: every field is on the axis, so there is no field to scan; add an off-axis field.");

        double top = 1;
        bool fTan = settings.Distortion is DistortionReference.FTanTheta or DistortionReference.CalibratedFTanTheta;
        if (fTan && system.Fields.Type == FieldType.Angle && scale > 89)
        {
            top = 89 / scale;
            notes.Add($"The largest field is {scale:g6}°; for F-tan(θ) distortion the scan stops at 89°, as OpticStudio's does.");
        }
        var h = FieldScanCommon.Even(settings.FieldPoints, top);
        var curves = FieldCurvatureDistortion.Compute(setup, rows, settings.Scan, h, settings.Distortion, cancellation);

        string u = setup.UnitName;
        string fieldLabel = setup.FieldLabel(settings.Scan);
        var fieldShown = h.Select(v => setup.FieldShown(v, settings.Scan)).ToArray();
        var curvature = new List<AnalysisCurve>();
        var distortion = new List<AnalysisCurve>();
        var sections = new List<ReportSection>();
        string distortionUnit = settings.DistortionAsLength ? u : "%";
        foreach (var c in curves)
        {
            string wl = FieldScanCommon.WavelengthLabel(system, c.Row);
            if (c.FieldCurvatureFailure is null)
            {
                curvature.Add(new AnalysisCurve($"T {wl}", c.Points.Select(p => setup.Shown(p.Tangential)).ToArray(), fieldShown));
                curvature.Add(new AnalysisCurve($"S {wl}", c.Points.Select(p => setup.Shown(p.Sagittal)).ToArray(), fieldShown, Dashed: true));
            }
            var dist = c.Points.Select(p => settings.DistortionAsLength ? setup.Shown(p.RealHeight - p.ReferenceHeight) : p.DistortionPercent).ToArray();
            distortion.Add(new AnalysisCurve(wl, dist, fieldShown));
            var rowsText = new List<IReadOnlyList<string>>();
            for (int i = 0; i < c.Points.Count; i++)
            {
                var p = c.Points[i];
                rowsText.Add(new[]
                {
                    FieldScanCommon.N(fieldShown[i], "g6"), FieldScanCommon.N(setup.Shown(p.Tangential)), FieldScanCommon.N(setup.Shown(p.Sagittal)),
                    FieldScanCommon.N(setup.Shown(p.RealHeight)), FieldScanCommon.N(setup.Shown(p.ReferenceHeight)),
                    FieldScanCommon.N(dist[i]),
                });
            }
            bool angle = FieldTypes.IsAngle(system.Fields.Type);
            sections.Add(new ReportSection($"Wavelength {c.Row + 1} ({wl})")
            {
                Pairs = new[]
                {
                    (angle ? $"Reference focal length ({u})" : "Reference magnification", FieldScanCommon.N(angle ? setup.Shown(c.ReferenceFocalLength) : c.ReferenceFocalLength)),
                    (angle ? $"Small-field focal length dr/dg ({u})" : "Small-field magnification dr/dg", FieldScanCommon.N(angle ? setup.Shown(c.SmallFieldSlope) : c.SmallFieldSlope)),
                    ("Maximum |distortion|", $"{FieldScanCommon.N(dist.Skip(1).Select(Math.Abs).DefaultIfEmpty(0).Max(), "g6")} {distortionUnit}"),
                },
                Lines = c.FieldCurvatureFailure is null ? Array.Empty<string>()
                    : new[] { $"Field curvature not computed: {c.FieldCurvatureFailure}" },
                TableHeaders = new[] { fieldLabel, $"Tangential ({u})", $"Sagittal ({u})", $"Real height ({u})", $"Reference height ({u})", $"Distortion ({distortionUnit})" },
                TableRows = rowsText,
            });
        }
        var plots = new List<AnalysisPlot>();
        if (curvature.Count > 0) plots.Add(new LinePlot("Field curvature", $"Focus from the image surface ({u})", fieldLabel, curvature));
        plots.Add(new LinePlot("Distortion", settings.DistortionAsLength ? $"Real − reference height ({u})" : "Distortion (%)", fieldLabel, distortion));
        var assumptions = new List<string>
        {
            "Field curvature: tangential (T) and sagittal (S) foci by Coddington's equations along the real chief ray (docs/Optics-Conventions.md §27), measured along the image surface's z axis from the chief ray's image point, positive in the direction the light travels.",
            $"Scan along {FieldScans.Label(settings.Scan)}: the tangential plane contains the scan; the system must be symmetric about that plane.",
            $"Distortion: {Label(settings.Distortion)}; heights are radial image-surface coordinates of the real chief ray; the reference is f·{(settings.Distortion is DistortionReference.FTheta or DistortionReference.CalibratedFTheta ? "θ" : FieldTypes.IsAngle(system.Fields.Type) ? "tan θ" : "(field height)")} with f from real chief rays at H = ±{FieldCurvatureDistortion.SmallField:g2}"
                + (settings.Distortion is DistortionReference.CalibratedFTanTheta or DistortionReference.CalibratedFTheta ? ", then refitted by least squares over the scan (calibrated)." : "."),
            "At the axis the percentage is the limit 100·(k/f − 1), zero unless calibrated.",
            settings.IgnoreVignettingFactors ? "Vignetting factors are ignored (cleared), so the chief ray goes through the pupil centre." : "Vignetting factors are kept: they move the chief ray in the pupil.",
            $"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.",
        };
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }

    internal static string Label(DistortionReference d) => d switch
    {
        DistortionReference.FTanTheta => "F-tan(θ)",
        DistortionReference.FTheta => "F-θ",
        DistortionReference.CalibratedFTanTheta => "calibrated F-tan(θ)",
        DistortionReference.CalibratedFTheta => "calibrated F-θ",
        _ => d.ToString(),
    };
}
