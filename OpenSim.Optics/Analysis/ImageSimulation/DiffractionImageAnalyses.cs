using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis.ImageSimulation;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// OP-42: the diffraction image analyses and the file viewers (docs/Optics-Conventions.md §67).

/// <summary>Settings of the <see cref="PartiallyCoherentImageAnalysis"/>.</summary>
public sealed record PartiallyCoherentImageSettings
{
    [DisplayName("File"), Description("The full path of the .IMA or .BIM object (relative irradiance). ZBF files are not read.")]
    public string File { get; init; } = "";

    [DisplayName("File size"), Description("The file's full width in image space, lens units.")]
    public double FileSize { get; init; } = 0.05;

    [DisplayName("Oversampling"), Description("Each pixel copied into n × n: 1, 2, 4, 8, 16 or 32.")]
    public int Oversampling { get; init; } = 4;

    [DisplayName("Zero padding"), Description("Zeros around the file: 1, 2, 4, 8, 16 or 32 (n odd: z·n − z + 1 pixels; n even: z·n).")]
    public int ZeroPadding { get; init; } = 2;

    [DisplayName("OTF sampling"), Description("n: the n × n pupil grid of the transfer function; 16 to 512.")]
    public int OtfSampling { get; init; } = 64;

    [DisplayName("Show as"), Description("Map, contour, or cross-section x or y (with the help's MTF estimate).")]
    public DiffractionImageShow ShowAs { get; init; } = DiffractionImageShow.Map;

    [DisplayName("Data type"), Description("Incoherent, coherent or raw image, the transfer functions, the raw transform, partially coherent images, or the partially coherent PSF or γ.")]
    public PartiallyCoherentDataType DataType { get; init; } = PartiallyCoherentDataType.IncoherentImage;

    [DisplayName("Gamma"), Description("γ's form: Gaussian e^(−r·r/α²) or SINC(x/α)·SINC(y/α).")]
    public CoherenceForm Gamma { get; init; } = CoherenceForm.Gaussian;

    [DisplayName("Alpha"), Description("γ's scale α, lens units.")]
    public double Alpha { get; init; } = 0.001;

    [DisplayName("Fraction"), Description("The share of the coherence terms kept (0.90 to 0.96 recommended by the help); 1 keeps all.")]
    public double Fraction { get; init; } = 0.95;

    [DisplayName("Diffraction limited"), Description("Ignore the aberrations (apertures still count).")]
    public bool DiffractionLimited { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("The field row whose transfer function is used.")]
    public int Field { get; init; } = 1;

    [DisplayName("Use polarization"), Description("Not built: refused.")]
    public bool UsePolarization { get; init; }

    [DisplayName("Resample image"), Description("Average the image over a detector of Number × Delta pixels.")]
    public bool ResampleImage { get; init; }

    [DisplayName("Skip normalization"), Description("Show the image in units of the object's irradiance instead of over its peak.")]
    public bool SkipNormalization { get; init; }

    [DisplayName("Number x"), Description("Detector columns for resampling.")]
    public int NumberX { get; init; } = 64;

    [DisplayName("Number y"), Description("Detector rows for resampling.")]
    public int NumberY { get; init; } = 64;

    [DisplayName("Delta x"), Description("Detector pixel width, lens units.")]
    public double DeltaX { get; init; } = 0.001;

    [DisplayName("Delta y"), Description("Detector pixel height, lens units.")]
    public double DeltaY { get; init; } = 0.001;

    [DisplayName("Decenter x"), Description("Detector decentre in detector pixels.")]
    public double DecenterX { get; init; }

    [DisplayName("Decenter y"), Description("Detector decentre in detector pixels.")]
    public double DecenterY { get; init; }

    [DisplayName("Output file"), Description("Not built (ZBF output belongs to physical optics): leave empty.")]
    public string OutputFile { get; init; } = "";

    [DisplayName("Map"), Description("Which pixel map of a multi-map binary IMA file; 1 for the first.")]
    public int Map { get; init; } = 1;
}

/// <summary>
/// OpticStudio's Partially Coherent Image Analysis (OP-42; <see cref="PartiallyCoherentImaging"/>).
/// Not built: ZBF input and output, polarization, the image surface aperture's energy cut, the
/// surface plot.
/// </summary>
public sealed class PartiallyCoherentImageAnalysis : WavefrontAnalysisBase<PartiallyCoherentImageSettings>
{
    public const string SavedId = "PartiallyCoherentImageAnalysis";

    public PartiallyCoherentImageAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Partially coherent image analysis";
    public override string Category => ExtendedSceneSupport.Category;

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        PartiallyCoherentImageSettings settings, CancellationToken cancellation)
    {
        Check(!settings.UsePolarization, $"{Name}: polarization is not built; turn \"Use polarization\" off.");
        Check(string.IsNullOrWhiteSpace(settings.OutputFile), $"{Name}: ZBF output is not built (ZBF files belong to physical optics); clear the output file.");
        Check(!settings.File.EndsWith(".zbf", StringComparison.OrdinalIgnoreCase), $"{Name}: ZBF input is not built; use an .IMA or .BIM file.");
        Check(settings.FileSize > 0 && double.IsFinite(settings.FileSize), $"{Name}: the file size must be positive.");
        Check(settings.OtfSampling >= 16 && settings.OtfSampling <= 512, $"{Name}: the OTF sampling must be 16 to 512.");
        Check(settings.Alpha > 0 && double.IsFinite(settings.Alpha), $"{Name}: α must be positive.");
        SceneTransforms.CheckPowerOfTwo(settings.Oversampling, Name, "oversampling");
        SceneTransforms.CheckPowerOfTwo(settings.ZeroPadding, Name, "zero padding");
        var scene = SceneFile.Read(settings.File, SceneFileUse.ImaOrBim, Name);
        Check(settings.Map >= 1 && settings.Map <= scene.Channels.Count, $"{Name}: map {settings.Map} is not one of the file's {scene.Channels.Count}.");
        int zero = scene.Kind == SceneFileKind.Bim ? 1 : settings.ZeroPadding;
        if (scene.Kind == SceneFileKind.Bim && settings.ZeroPadding > 1) notes.Add("Zero padding is not applied to BIM files (help).");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        int w = WavelengthRow(settings.Wavelength, system);
        var setup = ExtendedSceneSupport.Prepare(lens, system, false, false, false, Name, notes, cancellation);
        var sys = setup.System;
        var result = PartiallyCoherentImaging.Compute(setup.Lens, sys, new PartiallyCoherentRequest
        {
            Object = scene.Channels[settings.Map - 1], FileSize = OpticsConventions.ToMeters(settings.FileSize, sys.Units.LensUnit),
            Oversampling = settings.Oversampling, ZeroPadding = zero, OtfSampling = settings.OtfSampling, Field = f, Wavelength = w,
            DiffractionLimited = settings.DiffractionLimited, DataType = settings.DataType, Gamma = settings.Gamma,
            Alpha = OpticsConventions.ToMeters(settings.Alpha, sys.Units.LensUnit), Fraction = settings.Fraction,
        }, cancellation);
        return DiffractionImageOutput.Result(Name, result, settings.ShowAs, settings.SkipNormalization, settings.ResampleImage,
            settings.NumberX, settings.NumberY, settings.DeltaX, settings.DeltaY, settings.DecenterX, settings.DecenterY, sys, notes,
            $"Object {Path.GetFileName(settings.File)}: {scene.Width} × {scene.Height} pixels, {settings.FileSize:g6} {ExtendedSceneSupport.LensUnit(sys)} wide in image space; oversampling {settings.Oversampling}, zero padding {zero}; field {f + 1}, {RayAnalysisSupport.WaveLabel(sys, w)}.",
            "Not built: ZBF input and output, polarization, the image surface aperture's energy cut, the surface plot.");
    }
}

/// <summary>Settings of the <see cref="ExtendedDiffractionImageAnalysis"/>.</summary>
public sealed record ExtendedDiffractionImageSettings
{
    [DisplayName("File"), Description("The full path of the .IMA or .BIM object.")]
    public string File { get; init; } = "";

    [DisplayName("File size"), Description("The file's full width in image space, lens units.")]
    public double FileSize { get; init; } = 0.05;

    [DisplayName("Oversampling"), Description("Each pixel copied into n × n: 1, 2, 4, 8, 16 or 32.")]
    public int Oversampling { get; init; } = 1;

    [DisplayName("Display size"), Description("The displayed image's width, lens units.")]
    public double DisplaySize { get; init; } = 0.1;

    [DisplayName("OTF sampling"), Description("n: the n × n pupil grid of each transfer function; 16 to 512.")]
    public int OtfSampling { get; init; } = 64;

    [DisplayName("OTF grid"), Description("Transfer functions computed on an n × n grid over the file; 1 to 33.")]
    public int OtfGrid { get; init; } = 3;

    [DisplayName("Resolution"), Description("Display samples are 2 × OTF sampling × this; 1 to 16.")]
    public int Resolution { get; init; } = 1;

    [DisplayName("Show as"), Description("Map, contour, or cross-section x or y.")]
    public DiffractionImageShow ShowAs { get; init; } = DiffractionImageShow.Map;

    [DisplayName("Coherent"), Description("The coherent image; off for the incoherent image.")]
    public bool Coherent { get; init; }

    [DisplayName("Diffraction limited"), Description("Ignore the aberrations (apertures still count).")]
    public bool DiffractionLimited { get; init; }

    [DisplayName("Use delta functions"), Description("Each pixel a delta function instead of a luminous square.")]
    public bool UseDeltaFunctions { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("The field row the image is centred on.")]
    public int Field { get; init; } = 1;

    [DisplayName("Use polarization"), Description("Not built: refused.")]
    public bool UsePolarization { get; init; }

    [DisplayName("Consider distortion"), Description("Shift each grid point's transfer function by its chief ray's distortion.")]
    public bool ConsiderDistortion { get; init; }

    [DisplayName("Use relative illumination"), Description("Weight each grid point's transfer function by OP-37's relative illumination.")]
    public bool UseRelativeIllumination { get; init; }

    [DisplayName("Output file"), Description("Not built (ZBF output belongs to physical optics): leave empty.")]
    public string OutputFile { get; init; } = "";

    [DisplayName("Remove vignetting factors"), Description("Replace the fields' vignetting factors by surface apertures.")]
    public bool RemoveVignettingFactors { get; init; } = true;

    [DisplayName("Map"), Description("Which pixel map of a multi-map binary IMA file; 1 for the first.")]
    public int Map { get; init; } = 1;
}

/// <summary>
/// OpticStudio's Extended Diffraction Image Analysis (OP-42; <see cref="ExtendedDiffractionImaging"/>).
/// Not built: ZBF output, polarization, the surface plot, the help's per-pixel OPD-spread check.
/// </summary>
public sealed class ExtendedDiffractionImageAnalysis : WavefrontAnalysisBase<ExtendedDiffractionImageSettings>
{
    public const string SavedId = "ExtendedDiffractionImageAnalysis";

    public ExtendedDiffractionImageAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Extended diffraction image analysis";
    public override string Category => ExtendedSceneSupport.Category;

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        ExtendedDiffractionImageSettings settings, CancellationToken cancellation)
    {
        Check(!settings.UsePolarization, $"{Name}: polarization is not built; turn \"Use polarization\" off.");
        Check(string.IsNullOrWhiteSpace(settings.OutputFile), $"{Name}: ZBF output is not built (ZBF files belong to physical optics); clear the output file.");
        Check(settings.FileSize > 0 && settings.DisplaySize > 0, $"{Name}: the file and display sizes must be positive.");
        Check(settings.OtfSampling >= 16 && settings.OtfSampling <= 512, $"{Name}: the OTF sampling must be 16 to 512.");
        SceneTransforms.CheckPowerOfTwo(settings.Oversampling, Name, "oversampling");
        var scene = SceneFile.Read(settings.File, SceneFileUse.ImaOrBim, Name);
        Check(settings.Map >= 1 && settings.Map <= scene.Channels.Count, $"{Name}: map {settings.Map} is not one of the file's {scene.Channels.Count}.");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        int w = WavelengthRow(settings.Wavelength, system);
        var setup = ExtendedSceneSupport.Prepare(lens, system, settings.RemoveVignettingFactors, false, true, Name, notes, cancellation);
        var sys = setup.System;
        var result = ExtendedDiffractionImaging.Compute(setup.Lens, sys, new ExtendedDiffractionRequest
        {
            Object = scene.Channels[settings.Map - 1], FileSize = OpticsConventions.ToMeters(settings.FileSize, sys.Units.LensUnit),
            Oversampling = settings.Oversampling, DisplaySize = OpticsConventions.ToMeters(settings.DisplaySize, sys.Units.LensUnit),
            OtfSampling = settings.OtfSampling, OtfGrid = settings.OtfGrid, Resolution = settings.Resolution, Coherent = settings.Coherent,
            DiffractionLimited = settings.DiffractionLimited, DeltaFunctions = settings.UseDeltaFunctions, Field = f, Wavelength = w,
            ConsiderDistortion = settings.ConsiderDistortion, UseRelativeIllumination = settings.UseRelativeIllumination,
        }, cancellation);
        return DiffractionImageOutput.Result(Name, result, settings.ShowAs, false, false, 0, 0, 0, 0, 0, 0, sys, notes,
            $"Object {Path.GetFileName(settings.File)}: {scene.Width} × {scene.Height} pixels, {settings.FileSize:g6} {ExtendedSceneSupport.LensUnit(sys)} wide in image space; display {settings.DisplaySize:g6} {ExtendedSceneSupport.LensUnit(sys)} centred on field {f + 1}'s chief ray; {RayAnalysisSupport.WaveLabel(sys, w)}; {(settings.Coherent ? "coherent" : "incoherent")}.",
            "Not built: ZBF output, polarization, the surface plot, the help's check of the OPD spread per pixel.",
            centreHalfSample: true);
    }
}

/// <summary>The plots and text of a diffraction image.</summary>
internal static class DiffractionImageOutput
{
    public static OpticsAnalysisResult Result(string name, DiffractionImageResult result, DiffractionImageShow show, bool skipNormalization,
        bool resample, int nx, int ny, double dx, double dy, double ox, double oy, OpticalSystemData system, List<string> notes, string objectLine,
        string notBuilt, bool centreHalfSample = false)
    {
        string lu = ExtendedSceneSupport.LensUnit(system);
        var values = result.Values;
        double pitch = result.Pitch;
        int n = values.GetLength(0);
        // Even-sized grids centred on sample n/2 (the extended diffraction display) sit half a sample off the middle.
        double cx = centreHalfSample && n % 2 == 0 ? -pitch / 2 : 0, cy = centreHalfSample && n % 2 == 0 ? pitch / 2 : 0;
        if (resample && !result.Frequency)
        {
            if (nx < 2 || ny < 2 || !(dx > 0) || !(dy > 0))
                throw new ArgumentException($"{name}: resampling needs at least 2 × 2 detector pixels of positive size.");
            values = ExtendedSceneSupport.Resample(values, pitch, nx, ny, OpticsConventions.ToMeters(dx, system.Units.LensUnit), OpticsConventions.ToMeters(dy, system.Units.LensUnit), ox, oy);
            notes.Add($"Resampled on a {nx} × {ny} detector of {dx:g6} × {dy:g6} {lu} pixels decentred by ({ox:g6}, {oy:g6}) pixels, each the mean of the image over its area.");
        }
        double peak = 0;
        foreach (var v in values) peak = Math.Max(peak, v);
        if (!skipNormalization && !result.Frequency)
        {
            if (!(peak > 0)) throw new InvalidOperationException($"{name}: the image is dark everywhere.");
            values = (double[,])values.Clone();
            for (int r = 0; r < values.GetLength(0); r++) for (int c = 0; c < values.GetLength(1); c++) values[r, c] /= peak;
        }
        double sx, sy;
        string xl, yl, unit;
        if (result.Frequency)
        {
            sx = sy = pitch * OpticsConventions.MetersPer(system.Units.LensUnit);
            xl = $"fx (cycles/{lu})"; yl = $"fy (cycles/{lu})"; unit = "modulus";
            cx = cy = 0;
            int m = values.GetLength(0);
            if (m % 2 == 0) { cx = -sx / 2; cy = sy / 2; }
        }
        else
        {
            sx = resample ? dx : ExtendedSceneSupport.Shown(pitch, system);
            sy = resample ? dy : ExtendedSceneSupport.Shown(pitch, system);
            cx = resample ? 0 : ExtendedSceneSupport.Shown(cx, system);
            cy = resample ? 0 : ExtendedSceneSupport.Shown(cy, system);
            xl = $"x ({lu})"; yl = $"y ({lu})"; unit = skipNormalization ? "relative to the object's irradiance" : "relative intensity";
        }
        var pairs = new List<(string, string)>
        {
            ("Samples", $"{values.GetLength(1)} × {values.GetLength(0)} at {ExtendedSceneSupport.N(sx)} {(result.Frequency ? $"cycles/{lu}" : lu)}"),
            ("Transform size", $"{result.TransformSize} × {result.TransformSize}"),
            ("Coherent cutoff", $"{ExtendedSceneSupport.N(result.CutoffFrequency * OpticsConventions.MetersPer(system.Units.LensUnit))} cycles/{lu}"),
            ("Peak before normalisation", ExtendedSceneSupport.N(peak)),
        };
        AnalysisPlot plot;
        if (show is DiffractionImageShow.CrossSectionX or DiffractionImageShow.CrossSectionY)
        {
            bool alongX = show == DiffractionImageShow.CrossSectionX;
            var (xs, ys) = ExtendedSceneSupport.CrossSection(values, cx, cy, sx, sy, alongX);
            plot = new LinePlot($"{name}, cross-section {(alongX ? "x" : "y")}", alongX ? xl : yl, unit, new[] { new AnalysisCurve("image", xs, ys) });
            double mtf = ExtendedSceneSupport.MtfEstimate(ys);
            pairs.Add(("MTF estimate", double.IsFinite(mtf) ? ExtendedSceneSupport.N(mtf) + " ((Imax − Imin)/(Imax + Imin) between the second and second-to-last peaks; square-wave modulation for a bar target)" : "not found (fewer than four peaks)"));
        }
        else
        {
            plot = ExtendedSceneSupport.Map(name, values, cx, cy, sx, sy, xl, yl, unit, show == DiffractionImageShow.Contour ? AnalysisMapStyle.Contour : AnalysisMapStyle.Image);
        }
        var sections = new List<ReportSection>();
        if (result.Warnings.Count > 0) sections.Add(new ReportSection("Sampling and validity warnings") { Lines = result.Warnings });
        sections.Add(new ReportSection(name) { Pairs = pairs });
        var assumptions = new List<string> { objectLine };
        assumptions.AddRange(result.Notes);
        assumptions.Add(notBuilt);
        assumptions.AddRange(notes.Distinct());
        return OpticsAnalysisResult.FromSections(name, sections, assumptions, new[] { plot });
    }
}

/// <summary>Settings of the <see cref="ImaBimFileViewerAnalysis"/>.</summary>
public sealed record ImaBimFileViewerSettings
{
    [DisplayName("File"), Description("The full path of the .IMA or .BIM file.")]
    public string File { get; init; } = "";

    [DisplayName("Show as"), Description("Map or contour.")]
    public DiffractionImageShow ShowAs { get; init; } = DiffractionImageShow.Map;

    [DisplayName("Color"), Description("The pixel map of a binary IMA file, 1 for the first; 0 for every map.")]
    public int Color { get; init; }
}

/// <summary>OpticStudio's IMA/BIM File Viewer (OP-42): the file's maps, "normalized to a relative
/// intensity of 1.0" (help, "IMA and BIM File Viewer"), row 1 at the top. Not built: the surface plot.</summary>
public sealed class ImaBimFileViewerAnalysis : OpticsAnalysis<ImaBimFileViewerSettings>
{
    public const string SavedId = "ImaBimFileViewer";
    public override string Id => SavedId;
    public override string Name => "IMA/BIM file viewer";
    public override string Category => ExtendedSceneSupport.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, ImaBimFileViewerSettings settings, CancellationToken cancellation)
    {
        var scene = SceneFile.Read(settings.File, SceneFileUse.ImaOrBim, Name);
        if (settings.Color < 0 || settings.Color > scene.Channels.Count)
            throw new ArgumentException($"{Name}: color {settings.Color} is not 0 (all) or one of the file's {scene.Channels.Count} maps.");
        double max = scene.Maximum;
        var indices = settings.Color == 0 ? Enumerable.Range(0, scene.Channels.Count) : new[] { settings.Color - 1 };
        var style = settings.ShowAs == DiffractionImageShow.Contour ? AnalysisMapStyle.Contour : AnalysisMapStyle.Image;
        if (settings.ShowAs is DiffractionImageShow.CrossSectionX or DiffractionImageShow.CrossSectionY)
            throw new ArgumentException($"{Name}: show a map or contours (the help offers no cross-sections here).");
        var plots = indices.Select(i =>
        {
            var m = (double[,])scene.Channels[i].Clone();
            if (max > 0) for (int r = 0; r < scene.Height; r++) for (int c = 0; c < scene.Width; c++) m[r, c] /= max;
            return (AnalysisPlot)ExtendedSceneSupport.Map($"{Path.GetFileName(settings.File)}, {scene.ChannelNames[i]}", m, 0, 0, 1, 1, "pixel", "pixel", "relative intensity", style);
        }).ToArray();
        var pairs = new List<(string, string)>
        {
            ("Pixels", $"{scene.Width} × {scene.Height}"),
            ("Kind", scene.Kind.ToString()),
            ("Maps", scene.Channels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Largest value", ExtendedSceneSupport.N(max)),
        };
        return OpticsAnalysisResult.FromSections(Name, new[] { new ReportSection(Name) { Pairs = pairs } },
            new[] { "Every map is divided by the file's largest value (help: \"normalized to a relative intensity of 1.0\"); row 1 of the file is the top.", "Not built: the surface plot." }, plots);
    }
}

/// <summary>Settings of the <see cref="BitmapFileViewerAnalysis"/>.</summary>
public sealed record BitmapFileViewerSettings
{
    [DisplayName("File"), Description("The full path of the .BMP (24/32-bit) or .PNG (8-bit) file. JPG is not read.")]
    public string File { get; init; } = "";
}

/// <summary>OpticStudio's Bitmap File Viewer (OP-42): the file "without any processing" (help,
/// "Bitmap File Viewer"), one map per colour channel. Not built: JPG files, a true-colour picture
/// (the channels are shown as separate maps).</summary>
public sealed class BitmapFileViewerAnalysis : OpticsAnalysis<BitmapFileViewerSettings>
{
    public const string SavedId = "BitmapFileViewer";
    public override string Id => SavedId;
    public override string Name => "Bitmap file viewer";
    public override string Category => ExtendedSceneSupport.Category;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, BitmapFileViewerSettings settings, CancellationToken cancellation)
    {
        var scene = SceneFile.Read(settings.File, SceneFileUse.Bitmap, Name);
        var plots = scene.Channels.Select((m, i) => (AnalysisPlot)ExtendedSceneSupport.Map($"{Path.GetFileName(settings.File)}, {scene.ChannelNames[i]}", m, 0, 0, 1, 1,
            "pixel", "pixel", "pixel value (0–255)", maximum: 255)).ToArray();
        return OpticsAnalysisResult.FromSections(Name, new[] { new ReportSection(Name)
        {
            Pairs = new List<(string, string)> { ("Pixels", $"{scene.Width} × {scene.Height}"), ("Kind", scene.Kind.ToString()), ("Channels", string.Join(", ", scene.ChannelNames)) },
        } }, new[] { "The pixel values as stored (0–255), one map per channel; an alpha channel is not shown.", "Not built: JPG files, a true-colour picture." }, plots);
    }
}

/// <summary>Extended-scene analyses OpticStudio has that are not built, with the reason.</summary>
public static class ExtendedSceneNotBuilt
{
    public static IReadOnlyDictionary<string, string> Analyses { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Light Source Analysis"] = "Premium and Enterprise editions only (help); it traces DAT, SDF or TM25RAY non-sequential source files, which this program does not read.",
    };
}
