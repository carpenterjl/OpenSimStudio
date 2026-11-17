using System.ComponentModel;
using System.Globalization;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Photometry;

namespace OpenSim.Optics.NonSequential.Detectors;

/// <summary>The Detector Viewer's "Show As" (help, "Detector Viewer").</summary>
public enum NscDetectorShow
{
    /// <summary>False-colour image.</summary>
    Image = 0,
    /// <summary>Filled contours.</summary>
    Contour = 1,
    /// <summary>Cross section along a row (x across); for Detector Polar, along the arm at "Angle".</summary>
    CrossSectionRow = 2,
    /// <summary>Cross section along a column (y across).</summary>
    CrossSectionColumn = 3,
    /// <summary>Detector Color: true colour, drawn as its R, G and B channels.</summary>
    TrueColor = 4,
    /// <summary>Detector Color and Polar: pixel chromaticities on the CIE 1931 x, y chart.</summary>
    ChromaticityCie1931 = 5,
    /// <summary>Detector Color and Polar: pixel chromaticities on the CIE 1976 u′, v′ chart.</summary>
    ChromaticityCie1976 = 6,
    /// <summary>Detector Color with Record Spectral Data: flux against wavelength.</summary>
    FluxVsWavelength = 7,
}

/// <summary>The Detector Viewer's "Show Data" (help, "Detector Viewer"), plus the noise map.</summary>
public enum NscDetectorShowData
{
    IncoherentIrradiance = 0,
    CoherentIrradiance = 1,
    CoherentPhase = 2,
    RadiantIntensity = 3,
    RadiancePositionSpace = 4,
    RadianceAngleSpace = 5,
    /// <summary>This program's addition: each pixel's standard error divided by its flux (§73).</summary>
    RelativeNoise = 6,
}

/// <summary>"Scale": linear, or logarithmic over 5, 10 or 15 decades (the detectors' parameter 8).</summary>
public enum NscDetectorScale
{
    Linear = 0,
    Log5 = 1,
    Log10 = 2,
    Log15 = 3,
}

/// <summary>Settings of the <see cref="NscDetectorViewerAnalysis"/> (help, "Detector Viewer").</summary>
public sealed record NscDetectorViewerSettings
{
    [DisplayName("File"), Description("The full path of a .DDR, .DDC or .DDP detector data file (Save Detector Data); blank to trace the project's own sources onto its detectors.")]
    public string File { get; init; } = "";

    [DisplayName("Detector"), Description("The detector object to show when tracing the project (its object number); 0 for the first detector in the scene. Not used with a file.")]
    public int Detector { get; init; }

    [DisplayName("Show as"), Description("Image, contours, a row or column cross section, true colour, a chromaticity chart or flux vs. wavelength.")]
    public NscDetectorShow ShowAs { get; init; } = NscDetectorShow.Image;

    [DisplayName("Show data"), Description("Incoherent or coherent irradiance, coherent phase, radiant intensity, radiance in position or angle space, or the relative noise.")]
    public NscDetectorShowData ShowData { get; init; } = NscDetectorShowData.IncoherentIrradiance;

    [DisplayName("Row/Column"), Description("The row or column of a cross section, 1 for the first; 0 for the centre.")]
    public int RowOrColumn { get; init; }

    [DisplayName("Angle"), Description("Detector Polar: the azimuth (degrees from +x) of the cross section.")]
    public double Angle { get; init; }

    [DisplayName("Smoothing"), Description("How many times every pixel is replaced by the average of itself and its neighbours.")]
    public int Smoothing { get; init; }

    [DisplayName("Scale"), Description("Linear, or logarithmic over 5, 10 or 15 decades.")]
    public NscDetectorScale Scale { get; init; } = NscDetectorScale.Linear;

    [DisplayName("Max plot scale"), Description("The top of the colour scale (0: the data's maximum). With a log scale, the next power of 10 above it.")]
    public double MaxPlotScale { get; init; }

    [DisplayName("Min plot scale"), Description("The bottom of a linear colour scale (ignored with a log scale).")]
    public double MinPlotScale { get; init; }

    [DisplayName("Photometric"), Description("Lumens instead of watts: illuminance, luminous intensity and luminance (683·V(λ) per watt).")]
    public bool Photometric { get; init; }

    // OP-83: Playback ZRD on Detector (docs/Optics-Conventions.md §75)
    [DisplayName("Playback ZRD"), Description("The full path of a ray database (OpenSim format) traced with this system: its saved rays are played back onto the detector instead of tracing (Playback ZRD on Detector).")]
    public string PlaybackZrd { get; init; } = "";

    [DisplayName("Playback filter"), Description("With a playback ZRD: only segments that pass this filter string (or named filter) reach the detector.")]
    public string PlaybackFilter { get; init; } = "";

    // wire-16: the Ray Trace control's "Scatter NSC Rays" (help, "Ray Trace"), default off.
    [DisplayName("Scatter NSC rays"), Description("When tracing the project: rays scatter according to the scatter properties of the faces (OP-80's surface scattering). Off by default.")]
    public bool ScatterRays { get; init; }
}

/// <summary>
/// The Detector Viewer (help, "Detector Viewer"; plan item OP-79; docs/Optics-Conventions.md §73):
/// what a detector collected, as an image, contours or a cross section, on a linear or log
/// scale, smoothed, in radiometric or photometric units, with the beam information and a
/// per-pixel noise estimate; true colour and the CIE 1931 and 1976 chromaticity charts for colour
/// data. <see cref="Render"/> is the whole viewer on a data record; the analysis gets the record
/// by tracing the project's sources (<see cref="Sources.ProjectNscSources.Trace"/>) or from a DDR,
/// DDC or DDP file.
/// </summary>
public static class NscDetectorViewer
{
    private static string N(double v) => StudyReport.Number(v, "g6");

    /// <summary>The viewer's result for <paramref name="data"/>, lengths shown in <paramref name="unit"/>.</summary>
    public static OpticsAnalysisResult Render(NscDetectorData data, NscDetectorViewerSettings settings, LensUnit unit, string title = "Detector viewer")
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Smoothing < 0 || settings.Smoothing > 1000)
            throw new ArgumentException($"{title}: smoothing must be 0 to 1000 passes; it is {settings.Smoothing}.");
        return data switch
        {
            NscFlatDetectorData f => RenderFlat(f, settings, unit, title),
            NscPolarDetectorData p => RenderPolar(p, settings, title),
            _ => throw new NotSupportedException($"{title}: {data.TypeName} data cannot be shown."),
        };
    }

    // ================================================================== flat detectors

    private static OpticsAnalysisResult RenderFlat(NscFlatDetectorData d, NscDetectorViewerSettings s, LensUnit unit, string title)
    {
        string u = UnitName(unit);
        double L(double meters) => OpticsConventions.FromMeters(meters, unit);
        string flux = s.Photometric ? "lm" : "W";
        var assumptions = new List<string>
        {
            "Flux is in the launched rays' units, called W here (the source stand-in's watts); irradiance is per square meter, intensity per steradian.",
        };
        var sections = new List<ReportSection>();
        var plots = new List<AnalysisPlot>();
        var summary = new List<(string, string)>
        {
            ("Detector", d.ObjectNumber > 0 ? $"object {d.ObjectNumber}, {d.TypeName}" : d.TypeName + " (from file)"),
            ("Size", $"{N(2 * L(d.HalfWidthX))} × {N(2 * L(d.HalfWidthY))} {u}, {d.PixelsX} × {d.PixelsY} pixels"),
            ("Angle range", $"x {N(d.AngleXMin)}° to {N(d.AngleXMax)}°, y {N(d.AngleYMin)}° to {N(d.AngleYMax)}°"),
            ("Rays launched", d.LaunchedRays > 0 ? d.LaunchedRays.ToString(CultureInfo.InvariantCulture) : "unknown (data from file)"),
            ("Hits", $"{d.SpatialHits} spatial, {d.AngularHits} angular"),
            ("Total flux", $"{N(d.Flux.Sum())} W (angle space {N(d.AngleFlux.Sum())} W)"),
        };
        if (d.Luminous is not null) summary.Add(("Total luminous flux", $"{N(d.Luminous.Sum())} lm"));
        sections.Add(new ReportSection("Detector") { Pairs = summary });

        var (cx, cy, rx, ry, rr) = d.BeamInfo();
        sections.Add(new ReportSection("Beam info")
        {
            Pairs = new List<(string, string)>
            {
                ("Centroid x, y", $"{N(L(cx))}, {N(L(cy))} {u}"),
                ("RMS width x, y", $"{N(L(rx))}, {N(L(ry))} {u}"),
                ("RMS radius", $"{N(L(rr))} {u}"),
            },
            Lines = new[] { "Moments of the incoherent flux over the pixel centres." },
        });
        sections.Add(NoiseSection(d.Flux, d.FluxStandardError));

        if (d.IsColor && d.X is not null && d.Y is not null && d.Z is not null)
        {
            double X = d.X.Sum(), Y = d.Y.Sum(), Z = d.Z.Sum();
            var (x, y) = Photometric.Chromaticity(X, Y, Z);
            var (up, vp) = Photometric.Ucs1976(X, Y, Z);
            sections.Add(new ReportSection("Colour")
            {
                Pairs = new List<(string, string)>
                {
                    ("Tristimulus X, Y, Z", $"{N(X)}, {N(Y)}, {N(Z)} (Y in lumens when the rays carry watts)"),
                    ("Chromaticity x, y", $"{N(x)}, {N(y)}"),
                    ("Chromaticity u′, v′", $"{N(up)}, {N(vp)}"),
                },
            });
        }

        switch (s.ShowAs)
        {
            case NscDetectorShow.TrueColor:
                plots.AddRange(TrueColor(d, s, unit, out var colorLines));
                assumptions.AddRange(colorLines);
                return OpticsAnalysisResult.FromSections(title, sections, assumptions, plots);
            case NscDetectorShow.ChromaticityCie1931:
            case NscDetectorShow.ChromaticityCie1976:
                if (!d.IsColor || d.X is null || d.Y is null || d.Z is null)
                    throw new ArgumentException($"{title}: chromaticity charts need Detector Color data; this is {d.TypeName}.");
                plots.Add(Chart(d.X, d.Y, d.Z, s.ShowAs == NscDetectorShow.ChromaticityCie1976, title));
                assumptions.Add("Spectrum locus from the CIE 1931 2° colour-matching functions (CIE dataset 10.25039/CIE.DS.xvudnb9b), every 5 nm from 380 to 780 nm.");
                return OpticsAnalysisResult.FromSections(title, sections, assumptions, plots);
            case NscDetectorShow.FluxVsWavelength:
                if (d.Spectrum is null)
                    throw new ArgumentException($"{title}: flux vs. wavelength needs a Detector Color with Record Spectral Data on.");
                int bins = d.Spectrum.Length;
                double w = (d.SpectrumMaxMicrometers - d.SpectrumMinMicrometers) / bins;
                plots.Add(new LinePlot($"{title}, flux vs. wavelength", "wavelength (µm)", "flux (W)", new[]
                {
                    new AnalysisCurve("flux", Enumerable.Range(0, bins).Select(b => d.SpectrumMinMicrometers + (b + 0.5) * w).ToArray(), d.Spectrum.ToArray()),
                }));
                return OpticsAnalysisResult.FromSections(title, sections, assumptions, plots);
        }

        // ---- the scalar map
        bool angle = s.ShowData is NscDetectorShowData.RadiantIntensity or NscDetectorShowData.RadianceAngleSpace;
        var values = new double[d.PixelCount];
        string what, valueUnit;
        double area = d.PixelArea;
        double[] Need(double[]? a, string name) => a ?? throw new ArgumentException($"{title}: {name} is not available for this data ({d.TypeName}{(d.ObjectNumber == 0 ? ", from a file" : "")}).");
        switch (s.ShowData)
        {
            case NscDetectorShowData.IncoherentIrradiance:
            {
                var f = s.Photometric ? Need(d.Luminous, "the luminous flux") : d.Flux;
                for (int i = 0; i < values.Length; i++) values[i] = f[i] / area;
                what = s.Photometric ? "incoherent illuminance" : "incoherent irradiance"; valueUnit = $"{flux}/m²";
                break;
            }
            case NscDetectorShowData.RadiancePositionSpace:
            {
                var f = s.Photometric ? Need(d.Luminous, "the luminous flux") : d.Flux;
                for (int i = 0; i < values.Length; i++) values[i] = f[i] / area / (2 * Math.PI);
                what = s.Photometric ? "luminance (position space)" : "radiance (position space)"; valueUnit = $"{flux}/(m²·sr)";
                assumptions.Add("Radiance in position space is the irradiance divided by 2π, as the help defines it (\"assumes the detector is collecting data over a hemisphere\").");
                break;
            }
            case NscDetectorShowData.CoherentIrradiance:
                if (s.Photometric) throw new ArgumentException($"{title}: coherent illuminance is not built (the coherent sums carry no wavelength weighting); show it radiometrically.");
                Need(d.CoherentReal, "coherent data (Detector Rectangle only)");
                values = d.CoherentIrradiance();
                what = "coherent irradiance"; valueUnit = "W/m²";
                assumptions.Add(d.NormalizeCoherentPower && d.PixelCount > 1
                    ? "Normalize Coherent Power on: |ΣE|² per pixel, scaled so the detector's total equals its incoherent flux."
                    : "Coherent irradiance = incoherent irradiance × |ΣE|² / (ΣA)² per pixel (Normalize Coherent Power off, or one pixel).");
                assumptions.Add("All rays are summed coherently whatever their source or wavelength, as OpticStudio does; each ray's phase is its optical path carried as a local plane wave to the centre of the pixel struck.");
                break;
            case NscDetectorShowData.CoherentPhase:
                Need(d.CoherentReal, "coherent data (Detector Rectangle only)");
                if (s.Smoothing > 0) throw new ArgumentException($"{title}: smoothing a phase map is not meaningful; set smoothing to 0.");
                for (int i = 0; i < values.Length; i++) values[i] = d.CoherentPhase(i);
                what = "coherent phase"; valueUnit = "rad";
                break;
            case NscDetectorShowData.RadiantIntensity:
            {
                var f = s.Photometric ? Need(d.AngleLuminous, "the luminous flux") : d.AngleFlux;
                for (int iy = 0; iy < d.PixelsY; iy++)
                    for (int ix = 0; ix < d.PixelsX; ix++)
                        values[d.Index(ix, iy)] = f[d.Index(ix, iy)] / d.AngleSolidAngle(ix, iy);
                what = s.Photometric ? "luminous intensity" : "radiant intensity"; valueUnit = s.Photometric ? "cd" : "W/sr";
                assumptions.Add("Angle space: x angle = θ·l/√(1 − n²), y angle = θ·m/√(1 − n²), θ = |cos⁻¹ n| (help); each pixel's solid angle is ∫∫ (sin θ/θ) dx dy over it.");
                break;
            }
            case NscDetectorShowData.RadianceAngleSpace:
            {
                if (s.Photometric) Need(d.AngleLuminous, "the luminous flux");
                for (int iy = 0; iy < d.PixelsY; iy++)
                    for (int ix = 0; ix < d.PixelsX; ix++)
                    {
                        double r = d.RadianceAngle(ix, iy);
                        if (s.Photometric) r *= d.AngleFlux[d.Index(ix, iy)] > 0 ? d.AngleLuminous![d.Index(ix, iy)] / d.AngleFlux[d.Index(ix, iy)] : 0;
                        values[d.Index(ix, iy)] = r;
                    }
                what = s.Photometric ? "luminance (angle space)" : "radiance (angle space)"; valueUnit = $"{flux}/(m²·sr)";
                assumptions.Add("Radiance in angle space is the radiant intensity divided by the detector's area and the cosine of the angle at the pixel centre (help).");
                break;
            }
            case NscDetectorShowData.RelativeNoise:
                for (int i = 0; i < values.Length; i++) values[i] = d.Flux[i] > 0 ? d.FluxStandardError(i) / d.Flux[i] : double.NaN;
                what = "relative noise (standard error / flux)"; valueUnit = "1";
                break;
            default:
                throw new ArgumentException($"{title}: show data {s.ShowData} is not known.");
        }
        if (s.Smoothing > 0)
        {
            values = SmoothGrid(values, d.PixelsX, d.PixelsY, s.Smoothing, wrapX: false);
            assumptions.Add($"Smoothed {s.Smoothing} times: every pixel replaced by the average of itself and its existing neighbours (3 × 3).");
        }
        double ax0, ax1, ay0, ay1;
        string xl, yl;
        if (angle) { ax0 = d.AngleCenter(0, 0).X; ax1 = d.AngleCenter(d.PixelsX - 1, 0).X; ay0 = d.AngleCenter(0, 0).Y; ay1 = d.AngleCenter(0, d.PixelsY - 1).Y; xl = "x angle (°)"; yl = "y angle (°)"; }
        else { ax0 = L(d.PixelCenter(0, 0).X); ax1 = L(d.PixelCenter(d.PixelsX - 1, 0).X); ay0 = L(d.PixelCenter(0, 0).Y); ay1 = L(d.PixelCenter(0, d.PixelsY - 1).Y); xl = $"x ({u})"; yl = $"y ({u})"; }

        double peak = values.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max();
        sections.Add(new ReportSection(Capital(what)) { Pairs = new List<(string, string)> { ("Peak", $"{N(peak)} {valueUnit}") } });
        if (d.PixelCount <= 1024)
            sections.Add(PixelTable(d, values, angle, u, L, valueUnit));

        var (shown, lo, hi, scaleUnit) = Scaled(values, s, valueUnit);
        string plotTitle = $"{title}, {what}";
        if (s.ShowAs is NscDetectorShow.CrossSectionRow or NscDetectorShow.CrossSectionColumn)
        {
            bool row = s.ShowAs == NscDetectorShow.CrossSectionRow;
            int count = row ? d.PixelsY : d.PixelsX;
            int k = s.RowOrColumn == 0 ? count / 2 : s.RowOrColumn - 1;
            if (k < 0 || k >= count) throw new ArgumentException($"{title}: {(row ? "row" : "column")} {s.RowOrColumn} is not 0 (centre) or 1 to {count}.");
            int along = row ? d.PixelsX : d.PixelsY;
            var xs = new double[along];
            var ys = new double[along];
            for (int j = 0; j < along; j++)
            {
                xs[j] = along == 1 ? (row ? ax0 : ay0) : (row ? ax0 + (ax1 - ax0) * j / (along - 1) : ay0 + (ay1 - ay0) * j / (along - 1));
                ys[j] = shown[row ? d.Index(j, k) : d.Index(k, j)];
            }
            plots.Add(new LinePlot($"{plotTitle}, {(row ? "row" : "column")} {k + 1}", row ? xl : yl, scaleUnit, new[] { new AnalysisCurve(what, xs, ys) }));
        }
        else if (d.PixelsX >= 2 && d.PixelsY >= 2)
        {
            var grid = new double[d.PixelsY, d.PixelsX];
            for (int iy = 0; iy < d.PixelsY; iy++) for (int ix = 0; ix < d.PixelsX; ix++) grid[iy, ix] = shown[d.Index(ix, iy)];
            plots.Add(new AnalysisMapPlot(plotTitle, xl, yl, new GridField(grid, ax0, ax1, ay0, ay1), scaleUnit)
            {
                Style = s.ShowAs == NscDetectorShow.Contour ? AnalysisMapStyle.Contour : AnalysisMapStyle.Image,
                Minimum = lo, Maximum = hi,
            });
        }
        else assumptions.Add("A map needs at least 2 × 2 pixels; the values are listed instead.");
        assumptions.Add("Not built: grey-scale and inverse colour maps, contrast enhancement, output image files, PSF mode, polarization-filtered data, ray-database playback.");
        return OpticsAnalysisResult.FromSections(title, sections, assumptions, plots);
    }

    private static ReportSection NoiseSection(double[] flux, Func<int, double> se)
    {
        double sum = 0; int lit = 0; double worst = 0; int peakIndex = -1;
        for (int i = 0; i < flux.Length; i++)
        {
            if (!(flux[i] > 0)) continue;
            double r = se(i) / flux[i];
            if (double.IsNaN(r)) return new ReportSection("Noise") { Lines = new[] { "No noise estimate: the per-ray sums of squares are not known (data read from a file)." } };
            sum += r; lit++; worst = Math.Max(worst, r);
            if (peakIndex < 0 || flux[i] > flux[peakIndex]) peakIndex = i;
        }
        if (lit == 0) return new ReportSection("Noise") { Lines = new[] { "Nothing landed on the detector." } };
        return new ReportSection("Noise")
        {
            Pairs = new List<(string, string)>
            {
                ("Mean relative standard error over lit pixels", N(sum / lit)),
                ("Relative standard error of the brightest pixel", N(se(peakIndex) / flux[peakIndex])),
                ("Largest relative standard error", N(worst)),
            },
            Lines = new[] { "Standard error of each pixel's flux from the per-launched-ray contributions: √(N/(N − 1)·(Σw² − F²/N)). The help defines no noise estimate; for Sobol-sampled rays this overstates it." },
        };
    }

    private static ReportSection PixelTable(NscFlatDetectorData d, double[] values, bool angle, string u, Func<double, double> L, string valueUnit)
    {
        var rows = new List<IReadOnlyList<string>>();
        for (int iy = 0; iy < d.PixelsY; iy++)
            for (int ix = 0; ix < d.PixelsX; ix++)
            {
                int k = d.Index(ix, iy);
                var (x, y) = angle ? d.AngleCenter(ix, iy) : d.PixelCenter(ix, iy);
                double se = angle ? d.AngleFluxStandardError(k) : d.FluxStandardError(k);
                rows.Add(new[]
                {
                    (k + 1).ToString(CultureInfo.InvariantCulture), N(angle ? x : L(x)), N(angle ? y : L(y)),
                    N(angle ? d.AngleFlux[k] : d.Flux[k]), double.IsNaN(se) ? "—" : N(se), N(values[k]),
                });
            }
        return new ReportSection("Pixels")
        {
            TableHeaders = new[] { "Pixel", angle ? "x angle (°)" : $"x ({u})", angle ? "y angle (°)" : $"y ({u})", "Flux (W)", "Std. error (W)", $"Value ({valueUnit})" },
            TableRows = rows,
            Lines = new[] { "Pixel 1 is the (−x, −y) corner; x changes fastest (help). Values before smoothing and scaling." },
        };
    }

    /// <summary>Every pixel replaced by the average of itself and its existing neighbours, <paramref name="passes"/> times
    /// (help, "Detector Viewer", Smoothing); with <paramref name="wrapX"/> the x index wraps round (polar azimuth).</summary>
    public static double[] SmoothGrid(double[] values, int nx, int ny, int passes, bool wrapX)
    {
        var a = (double[])values.Clone();
        var b = new double[a.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    double sum = 0; int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int y = iy + dy;
                        if (y < 0 || y >= ny) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = ix + dx;
                            if (wrapX) x = ((x % nx) + nx) % nx;
                            else if (x < 0 || x >= nx) continue;
                            double v = a[y * nx + x];
                            if (!double.IsFinite(v)) continue;
                            sum += v; n++;
                        }
                    }
                    b[iy * nx + ix] = n > 0 ? sum / n : double.NaN;
                }
            (a, b) = (b, a);
        }
        return a;
    }

    /// <summary>The values as drawn and the colour-scale limits: linear (Min/Max Plot Scale when
    /// either is non-zero), or log₁₀ over 5, 10 or 15 decades below the top, the top being the
    /// next power of 10 above Max Plot Scale when that is positive, else the data's maximum.</summary>
    internal static (double[] Values, double Min, double Max, string Unit) Scaled(double[] values, NscDetectorViewerSettings s, string unit)
    {
        if (s.Scale == NscDetectorScale.Linear)
        {
            bool set = s.MaxPlotScale != 0 || s.MinPlotScale != 0;
            return (values, set ? s.MinPlotScale : double.NaN, set && s.MaxPlotScale != 0 ? s.MaxPlotScale : double.NaN, unit);
        }
        int decades = s.Scale switch { NscDetectorScale.Log5 => 5, NscDetectorScale.Log10 => 10, _ => 15 };
        double top = s.MaxPlotScale > 0
            ? Math.Pow(10, Math.Ceiling(Math.Log10(s.MaxPlotScale)))
            : values.Where(v => double.IsFinite(v) && v > 0).DefaultIfEmpty(1).Max();
        double logTop = Math.Log10(top), logFloor = logTop - decades;
        var r = new double[values.Length];
        for (int i = 0; i < r.Length; i++)
            r[i] = double.IsNaN(values[i]) ? double.NaN : values[i] > 0 ? Math.Max(logFloor, Math.Log10(values[i])) : logFloor;
        return (r, logFloor, logTop, $"log₁₀ {unit}");
    }

    private static IEnumerable<AnalysisPlot> TrueColor(NscFlatDetectorData d, NscDetectorViewerSettings s, LensUnit unit, out List<string> lines)
    {
        if (!d.IsColor || d.X is null || d.Y is null || d.Z is null)
            throw new ArgumentException($"Detector viewer: true colour needs Detector Color data; this is {d.TypeName}.");
        if (d.PixelsX < 2 || d.PixelsY < 2) throw new ArgumentException("Detector viewer: true colour needs at least 2 × 2 pixels.");
        int n = d.PixelCount;
        var X = s.Smoothing > 0 ? SmoothGrid(d.X, d.PixelsX, d.PixelsY, s.Smoothing, false) : d.X;
        var Y = s.Smoothing > 0 ? SmoothGrid(d.Y, d.PixelsX, d.PixelsY, s.Smoothing, false) : d.Y;
        var Z = s.Smoothing > 0 ? SmoothGrid(d.Z, d.PixelsX, d.PixelsY, s.Smoothing, false) : d.Z;
        var rgb = new (double R, double G, double B)[n];
        double max = 0; int outOfGamut = 0;
        for (int i = 0; i < n; i++)
        {
            var c = Photometric.LinearSrgb(X[i], Y[i], Z[i]);
            if (c.R < 0 || c.G < 0 || c.B < 0) outOfGamut++;
            c = (Math.Max(0, c.R), Math.Max(0, c.G), Math.Max(0, c.B));
            rgb[i] = c;
            max = Math.Max(max, Math.Max(c.R, Math.Max(c.G, c.B)));
        }
        lines = new List<string>
        {
            "True colour: XYZ per pixel to linear sRGB (CSS Color Module Level 4 §19, XYZ_to_lin_sRGB, D65), negative components clipped to 0, divided by the largest component of the image, then the sRGB transfer function; drawn as its R, G and B channels (0–1), as there is no colour-picture plot.",
            $"{outOfGamut} of {n} pixels are outside the sRGB gamut (a negative component, clipped).",
        };
        double L(double m) => OpticsConventions.FromMeters(m, unit);
        string u = UnitName(unit);
        double x0 = L(d.PixelCenter(0, 0).X), x1 = L(d.PixelCenter(d.PixelsX - 1, 0).X), y0 = L(d.PixelCenter(0, 0).Y), y1 = L(d.PixelCenter(0, d.PixelsY - 1).Y);
        var plots = new List<AnalysisPlot>();
        foreach (var (name, pick) in new (string, Func<(double R, double G, double B), double>)[] { ("R", c => c.R), ("G", c => c.G), ("B", c => c.B) })
        {
            var grid = new double[d.PixelsY, d.PixelsX];
            for (int iy = 0; iy < d.PixelsY; iy++)
                for (int ix = 0; ix < d.PixelsX; ix++)
                    grid[iy, ix] = max > 0 ? Photometric.SrgbEncode(pick(rgb[d.Index(ix, iy)]) / max) : 0;
            plots.Add(new AnalysisMapPlot($"Detector viewer, true colour, {name}", $"x ({u})", $"y ({u})", new GridField(grid, x0, x1, y0, y1), "sRGB value") { Minimum = 0, Maximum = 1 });
        }
        return plots;
    }

    private static AnalysisScatterPlot Chart(double[] X, double[] Y, double[] Z, bool ucs, string title)
    {
        (double, double) C(double x, double y, double z) => ucs ? Photometric.Ucs1976(x, y, z) : Photometric.Chromaticity(x, y, z);
        var locus = new List<AnalysisPoint>();
        for (int nm = 380; nm <= 780; nm += 5)
        {
            var (a, b, c) = CieTables.ColorMatching(nm);
            var (p, q) = C(a, b, c);
            locus.Add(new AnalysisPoint(p, q, nm % 50 == 0 ? $"{nm} nm" : ""));
        }
        var pixels = new List<AnalysisPoint>();
        for (int i = 0; i < X.Length && pixels.Count < 20000; i++)
        {
            if (!(X[i] + Y[i] + Z[i] > 0)) continue;
            var (p, q) = C(X[i], Y[i], Z[i]);
            pixels.Add(new AnalysisPoint(p, q, ""));
        }
        var (tp, tq) = C(X.Sum(), Y.Sum(), Z.Sum());
        return new AnalysisScatterPlot($"{title}, CIE {(ucs ? "1976 u′, v′" : "1931 x, y")} chromaticity", ucs ? "u′" : "x", ucs ? "v′" : "y", new[]
        {
            new AnalysisScatterSeries("spectrum locus", locus),
            new AnalysisScatterSeries("pixels", pixels),
            new AnalysisScatterSeries("whole detector", new[] { new AnalysisPoint(tp, tq, "total") }),
        });
    }

    // ================================================================== polar

    private static OpticsAnalysisResult RenderPolar(NscPolarDetectorData d, NscDetectorViewerSettings s, string title)
    {
        var sections = new List<ReportSection>();
        var assumptions = new List<string>
        {
            "Flux is in the launched rays' units, called W here. The detector bins rays by their direction: polar angle from the local +z axis, azimuth from +x counter-clockwise.",
            $"Polar bins: increment = maximum angle/(# radial pixels − 1) = {N(d.Increment)}°; the first bin is [0, increment/2], the last ends at the maximum angle (help, figure \"Detector Polar sampling\"). Azimuth arms are centred on multiples of {N(d.AzimuthStep)}°.",
        };
        var summary = new List<(string, string)>
        {
            ("Detector", d.ObjectNumber > 0 ? $"object {d.ObjectNumber}, Detector Polar" : "Detector Polar (from file)"),
            ("Pixels", $"{d.PolarPixels} polar × {d.AzimuthPixels} azimuthal, maximum angle {N(d.MaxAngle)}°"),
            ("Rays launched", d.LaunchedRays > 0 ? d.LaunchedRays.ToString(CultureInfo.InvariantCulture) : "unknown (data from file)"),
            ("Hits", d.Hits.ToString(CultureInfo.InvariantCulture)),
            ("Total flux", $"{N(d.Flux.Sum())} W"),
        };
        if (d.Luminous is not null) summary.Add(("Total luminous flux", $"{N(d.Luminous.Sum())} lm"));
        sections.Add(new ReportSection("Detector") { Pairs = summary });
        sections.Add(NoiseSection(d.Flux, d.FluxStandardError));
        var plots = new List<AnalysisPlot>();
        if (s.ShowAs is NscDetectorShow.ChromaticityCie1931 or NscDetectorShow.ChromaticityCie1976)
        {
            if (d.X is null || d.Y is null || d.Z is null) throw new ArgumentException($"{title}: this Detector Polar data has no tristimulus values.");
            plots.Add(Chart(d.X, d.Y, d.Z, s.ShowAs == NscDetectorShow.ChromaticityCie1976, title));
            return OpticsAnalysisResult.FromSections(title, sections, assumptions, plots);
        }
        if (s.ShowAs is NscDetectorShow.TrueColor or NscDetectorShow.FluxVsWavelength or NscDetectorShow.CrossSectionColumn)
            throw new ArgumentException($"{title}: Detector Polar data shows as an image, contours, a cross section at an angle, or a chromaticity chart.");
        if (s.ShowData is not (NscDetectorShowData.RadiantIntensity or NscDetectorShowData.RelativeNoise))
            throw new ArgumentException($"{title}: Detector Polar records angular data only; show radiant intensity (or the relative noise).");
        var f = s.Photometric ? d.Luminous ?? throw new ArgumentException($"{title}: this data has no luminous flux.") : d.Flux;
        var values = new double[d.PixelCount];
        for (int a = 0; a < d.AzimuthPixels; a++)
            for (int j = 0; j < d.PolarPixels; j++)
            {
                int k = d.Index(j, a);
                values[k] = s.ShowData == NscDetectorShowData.RelativeNoise
                    ? (d.Flux[k] > 0 ? d.FluxStandardError(k) / d.Flux[k] : double.NaN)
                    : f[k] / d.SolidAngle(j);
            }
        string what = s.ShowData == NscDetectorShowData.RelativeNoise ? "relative noise" : s.Photometric ? "luminous intensity" : "radiant intensity";
        string unit = s.ShowData == NscDetectorShowData.RelativeNoise ? "1" : s.Photometric ? "cd" : "W/sr";
        if (s.Smoothing > 0)
        {
            // Grid with the polar index fastest: x = polar index, y = azimuth arm; the arm wraps.
            var grid = new double[values.Length];
            for (int a = 0; a < d.AzimuthPixels; a++) for (int j = 0; j < d.PolarPixels; j++) grid[j * d.AzimuthPixels + a] = values[d.Index(j, a)];
            grid = SmoothGrid(grid, d.AzimuthPixels, d.PolarPixels, s.Smoothing, wrapX: true);
            double before = 0, after = 0;
            for (int a = 0; a < d.AzimuthPixels; a++) for (int j = 0; j < d.PolarPixels; j++) { before += values[d.Index(j, a)] * d.SolidAngle(j); after += grid[j * d.AzimuthPixels + a] * d.SolidAngle(j); }
            double scale = s.ShowData != NscDetectorShowData.RelativeNoise && after > 0 ? before / after : 1;
            for (int a = 0; a < d.AzimuthPixels; a++) for (int j = 0; j < d.PolarPixels; j++) values[d.Index(j, a)] = grid[j * d.AzimuthPixels + a] * scale;
            assumptions.Add($"Smoothed {s.Smoothing} times (3 × 3 average, the azimuth wrapping round), then scaled to keep the total power (help).");
        }
        sections.Add(new ReportSection(Capital(what)) { Pairs = new List<(string, string)> { ("Peak", $"{N(values.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max())} {unit}") } });
        var (shown, lo, hi, scaleUnit) = Scaled(values, s, unit);
        if (s.ShowAs == NscDetectorShow.CrossSectionRow)
        {
            int arm = d.AzimuthIndex(s.Angle);
            int opposite = d.AzimuthIndex(s.Angle + 180);
            var xs = new List<double>();
            var ys = new List<double>();
            for (int j = d.PolarPixels - 1; j >= 1; j--) { xs.Add(-j * d.Increment); ys.Add(shown[d.Index(j, opposite)]); }
            for (int j = 0; j < d.PolarPixels; j++) { xs.Add(j * d.Increment); ys.Add(shown[d.Index(j, arm)]); }
            plots.Add(new LinePlot($"{title}, {what} at {N(arm * d.AzimuthStep)}°", "polar angle (°), negative on the opposite arm", scaleUnit, new[] { new AnalysisCurve(what, xs, ys) }));
        }
        else
        {
            var grid = new double[d.PolarPixels, d.AzimuthPixels];
            for (int j = 0; j < d.PolarPixels; j++) for (int a = 0; a < d.AzimuthPixels; a++) grid[j, a] = shown[d.Index(j, a)];
            plots.Add(new AnalysisMapPlot($"{title}, {what}", "azimuth (°)", "polar angle (°)",
                new GridField(grid, 0, (d.AzimuthPixels - 1) * d.AzimuthStep, 0, d.MaxAngle), scaleUnit)
            {
                Style = s.ShowAs == NscDetectorShow.Contour ? AnalysisMapStyle.Contour : AnalysisMapStyle.Image,
                Minimum = lo, Maximum = hi,
            });
        }
        assumptions.Add("Not built: the full and half polar directivity plots, export as IES/LDT, ray-database playback.");
        return OpticsAnalysisResult.FromSections(title, sections, assumptions, plots);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    internal static string UnitName(LensUnit unit) => unit switch
    {
        LensUnit.Millimeter => "mm",
        LensUnit.Centimeter => "cm",
        LensUnit.Inch => "in",
        _ => "m",
    };
}

/// <summary>
/// OpticStudio's Detector Viewer as an analysis (help, "Detector Viewer"), shown by
/// <see cref="NscDetectorViewer"/>. With a file (.DDR, .DDC, .DDP, Save Detector Data) it shows
/// that data. Without one it traces the project's own sources onto its detectors
/// (<see cref="Sources.ProjectNscSources.Trace"/>: every source, the analysis ray counts, no
/// splitting or polarization, the default seed; OpticStudio's Ray Trace control with its
/// checkboxes clear) and shows the detector the settings name, its data summed over the
/// wavelengths.
/// </summary>
public sealed class NscDetectorViewerAnalysis : OpticsAnalysis<NscDetectorViewerSettings>
{
    public const string SavedId = "NscDetectorViewer";
    public override string Id => SavedId;
    public override string Name => "Detector viewer";
    public override string Category => "Non-sequential";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscDetectorViewerSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var unit = SystemDataSettings.FromSettings(context.System.SystemData, notes).Units.LensUnit;
        if (!string.IsNullOrWhiteSpace(settings.File))
        {
            var data = NscDetectorFile.Read(settings.File);
            var result = NscDetectorViewer.Render(data, settings, unit, Name);
            return OpticsAnalysisResult.FromSections(Name, result.Sections,
                result.Assumptions.Concat(notes).Prepend($"Data read from {Path.GetFileName(settings.File)}.").ToArray(), result.Plots);
        }

        if (!string.IsNullOrWhiteSpace(settings.PlaybackZrd))   // OP-83
            return RayDatabase.NscRayPlaybackViewer.Show(context, settings, Name, unit, notes);
        var scene = ProjectNonSequential.Scene(context, unit)
            ?? throw new ArgumentException($"{Name}: the system has no non-sequential objects; add sources and a detector, or give a detector data file (.DDR, .DDC or .DDP).");
        var detectors = Enumerable.Range(1, scene.Objects.Count).Where(i => scene.Objects[i - 1].Geometry is INscDetector).ToArray();
        if (detectors.Length == 0)
            throw new ArgumentException($"{Name}: the scene has no detector objects (Detector Rectangle, Detector Color or Detector Polar); add one, or give a detector data file.");
        if (!scene.Objects.Any(o => o.Geometry is Sources.NscSource))
            throw new ArgumentException($"{Name}: the scene has no source objects to trace onto its detectors; add one, or give a detector data file.");
        int number = settings.Detector == 0 ? detectors[0] : settings.Detector;
        if (!detectors.Contains(number))
            throw new ArgumentException($"{Name}: object {settings.Detector} is not a detector; the detectors are object{(detectors.Length == 1 ? "" : "s")} {string.Join(", ", detectors)}.");

        var run = Sources.ProjectNscSources.Trace(context, cancellation: cancellation, scatter: settings.ScatterRays);   // wire-16: Scatter NSC Rays
        var traced = run.Detectors?.FirstOrDefault(d => d.ObjectNumber == number)
            ?? throw new InvalidOperationException($"{Name}: the source run returned no data for detector object {number}.");
        var shown = NscDetectorViewer.Render(traced, settings, unit, Name);
        var ledger = run.Ledger;
        var traceNotes = new List<string>
        {
            $"Detector object {number} ({traced.TypeName}): the project's sources traced ({string.Join(", ", run.Sources.Select(s => $"object {s.ObjectNumber} {s.TypeName}, {s.Rays} rays"))}) at {run.Runs.Count} wavelength{(run.Runs.Count == 1 ? "" : "s")}, data summed over them; fluxes in {run.Unit}.",
            $"Energy ledger ({run.Unit}): emitted {StudyReport.Number(ledger.Emitted, "g6")}, detected {StudyReport.Number(ledger.Detected, "g6")}, absorbed {StudyReport.Number(ledger.Absorbed, "g6")}, escaped {StudyReport.Number(ledger.Escaped, "g6")}.",
        };
        return OpticsAnalysisResult.FromSections(Name, shown.Sections,
            shown.Assumptions.Concat(notes).Concat(run.Notes).Concat(traceNotes).ToArray(), shown.Plots);
    }
}
