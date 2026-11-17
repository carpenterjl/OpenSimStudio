using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis.ImageSimulation;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// ---------------------------------------------------------------------------------------------
// OP-42: the extended-scene analyses. Each reads the project's saved lens (ProjectLens) unless a
// lens source is given. Settings and behaviour come from the Ansys OpticStudio 2025 R2 help pages
// "Image Simulation", "Geometric Image Analysis", "Geometric Bitmap Image Analysis", "Partially
// Coherent Image Analysis", "Extended Diffraction Image Analysis", "IMA and BIM File Viewer" and
// "Bitmap File Viewer", saved under plans/sources/optics/op-42-help/ (docs/Optics-Conventions.md
// §67). "Light Source Analysis" (Premium, non-sequential source files) is not built.
// ---------------------------------------------------------------------------------------------

/// <summary>What the Image Simulation shows (help, "Show As": "Simulated Image, Source Bitmap ...
/// or PSF Grid").</summary>
public enum ImageSimulationShow
{
    SimulatedImage = 0,
    SourceBitmap = 1,
    PsfGrid = 2,
}

/// <summary>Settings of the <see cref="ImageSimulationAnalysis"/>.</summary>
public sealed record ImageSimulationSettings
{
    [DisplayName("Input file"), Description("The full path of the source: .IMA, .BIM, .BMP (24/32-bit) or .PNG (8-bit). JPG is not read.")]
    public string File { get; init; } = "";

    [DisplayName("Field height"), Description("The source's full height in field units (degrees for angle fields, lens units for heights), after oversampling, guard band and rotation.")]
    public double FieldHeight { get; init; } = 1;

    [DisplayName("Oversampling"), Description("Each pixel copied into n × n: 1, 2, 4, 8, 16 or 32.")]
    public int Oversampling { get; init; } = 1;

    [DisplayName("Guard band"), Description("A black border making the picture n times as wide and high: 1, 2, 4, 8, 16 or 32.")]
    public int GuardBand { get; init; } = 1;

    [DisplayName("Flip source"), Description("Flip the source left-right, top-bottom, or both, before the optics.")]
    public SceneFlip FlipSource { get; init; } = SceneFlip.None;

    [DisplayName("Rotate source"), Description("Turn the source by quarter turns (counter-clockwise), before the optics.")]
    public SceneRotation RotateSource { get; init; } = SceneRotation.None;

    [DisplayName("Wavelength"), Description("RGB (0.606, 0.535, 0.465 µm), 1+2+3 (red on wavelength 3, green on 2, blue on 1), or a single wavelength.")]
    public SceneWavelengths Wavelengths { get; init; } = SceneWavelengths.Rgb;

    [DisplayName("Wavelength number"), Description("The wavelength row used with Single (blue channel for 1, 4, 7…, green for 2, 5, 8…, red for 3, 6, 9…).")]
    public int WavelengthNumber { get; init; } = 1;

    [DisplayName("Field"), Description("The field row the source is centred on, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Pupil sampling"), Description("n: an n × n pupil grid for each PSF; 4 to 512.")]
    public int PupilSampling { get; init; } = 64;

    [DisplayName("Image sampling"), Description("The fewest PSF samples across a PSF's window; 4 to 1024.")]
    public int ImageSampling { get; init; } = 64;

    [DisplayName("PSF x points"), Description("PSF grid points across the picture; 1 to 33.")]
    public int PsfPointsX { get; init; } = 5;

    [DisplayName("PSF y points"), Description("PSF grid points down the picture; 1 to 33.")]
    public int PsfPointsY { get; init; } = 5;

    [DisplayName("Use polarization"), Description("Not built: polarization is refused.")]
    public bool UsePolarization { get; init; }

    [DisplayName("Apply fixed apertures"), Description("Give every powered surface without an aperture a circular one at its semi-diameter.")]
    public bool ApplyFixedApertures { get; init; } = true;

    [DisplayName("Use relative illumination"), Description("Weight the picture by OP-37's relative illumination at the PSF grid points.")]
    public bool UseRelativeIllumination { get; init; } = true;

    [DisplayName("Aberrations"), Description("None (delta PSFs: distortion and lateral colour only), Geometric (ray spots), or Diffraction.")]
    public ImageSimulationAberrations Aberrations { get; init; } = ImageSimulationAberrations.Geometric;

    [DisplayName("Diffraction PSF"), Description("Huygens (the help's) or FFT (faster; on the plane normal to the chief ray).")]
    public DiffractionPsfEngine DiffractionPsf { get; init; } = DiffractionPsfEngine.Huygens;

    [DisplayName("Show as"), Description("The simulated image, the source bitmap (after oversampling, guard band, flip and rotation) or the PSF grid.")]
    public ImageSimulationShow ShowAs { get; init; } = ImageSimulationShow.SimulatedImage;

    [DisplayName("Reference"), Description("The detector centre: chief ray, surface vertex, or primary wavelength chief ray.")]
    public ImageSimulationReference Reference { get; init; } = ImageSimulationReference.ChiefRay;

    [DisplayName("X pixels"), Description("Detector columns; 0 for the source's.")]
    public int XPixels { get; init; }

    [DisplayName("Y pixels"), Description("Detector rows; 0 for the source's.")]
    public int YPixels { get; init; }

    [DisplayName("Pixel size"), Description("Detector pixel size in lens units; 0 for the help's default from the centre pixel's chief rays.")]
    public double PixelSize { get; init; }

    [DisplayName("Flip image"), Description("Flip the simulated image left-right, top-bottom, or both.")]
    public SceneFlip FlipImage { get; init; } = SceneFlip.None;

    [DisplayName("Output file"), Description("Full path of a .BMP or .PNG to write the simulated image to; empty for none.")]
    public string OutputFile { get; init; } = "";

    [DisplayName("Remove vignetting factors"), Description("Replace the fields' vignetting factors by surface apertures (needed when the fields have them).")]
    public bool RemoveVignettingFactors { get; init; } = true;
}

/// <summary>
/// OpticStudio's Image Simulation (OP-42): a source bitmap imaged through the lens by a grid of
/// field-varying PSFs with distortion, lateral colour and relative illumination
/// (<see cref="ImageSimulator"/>). Not built: polarization, afocal systems, JPG files, the
/// "Suppress Frame" display switch, curved-image projection beyond the image surface's local x, y.
/// </summary>
public sealed class ImageSimulationAnalysis : WavefrontAnalysisBase<ImageSimulationSettings>
{
    public const string SavedId = "ImageSimulation";

    public ImageSimulationAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Image simulation";
    public override string Category => ExtendedSceneSupport.Category;

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        ImageSimulationSettings settings, CancellationToken cancellation)
    {
        Check(!settings.UsePolarization, $"{Name}: polarization is not built; turn \"Use polarization\" off.");
        Check(settings.FieldHeight > 0 && double.IsFinite(settings.FieldHeight), $"{Name}: the field height must be positive (it is {settings.FieldHeight:g6}).");
        Check(settings.PixelSize >= 0 && double.IsFinite(settings.PixelSize), $"{Name}: the pixel size must be 0 (default) or positive.");
        SceneTransforms.CheckPowerOfTwo(settings.Oversampling, Name, "oversampling");
        SceneTransforms.CheckPowerOfTwo(settings.GuardBand, Name, "guard band");
        var source = SceneFile.Read(settings.File, SceneFileUse.Any, Name);
        var scene = SceneTransforms.Oversample(source, settings.Oversampling);
        scene = SceneTransforms.GuardBand(scene, settings.GuardBand);
        scene = SceneTransforms.Flip(scene, settings.FlipSource);
        scene = SceneTransforms.Rotate(scene, settings.RotateSource);
        Check(scene.Width <= BmpFile.MaximumSide && scene.Height <= BmpFile.MaximumSide, $"{Name}: the oversampled, guard-banded picture is {scene.Width} × {scene.Height}; the help's limit is {BmpFile.MaximumSide} each way.");
        Check(scene.Width >= 2 && scene.Height >= 2, $"{Name}: the picture must have more than 1 pixel each way (help).");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        string unit = ExtendedSceneSupport.FieldUnit(system);
        string lu = ExtendedSceneSupport.LensUnit(system);

        var setup = ExtendedSceneSupport.Prepare(lens, system, settings.RemoveVignettingFactors, settings.ApplyFixedApertures, true, Name, notes, cancellation);
        var (sys, channels) = Channels(setup.System, scene, settings, notes);
        var sourceLine = $"Source {Path.GetFileName(settings.File)}: {source.Width} × {source.Height} pixels ({source.Kind}), shown as {scene.Width} × {scene.Height} after oversampling {settings.Oversampling}, guard band {settings.GuardBand}, flip {settings.FlipSource}, rotation {settings.RotateSource}; field height {settings.FieldHeight:g6}{unit}, centred on field {f + 1}.";

        if (settings.ShowAs == ImageSimulationShow.SourceBitmap)
        {
            var plots = scene.Channels.Select((m, i) => ExtendedSceneSupport.Map($"Source bitmap, {scene.ChannelNames[i]}", m, 0, 0,
                settings.FieldHeight / scene.Height, settings.FieldHeight / scene.Height, $"x field ({unit.Trim()})", $"y field ({unit.Trim()})", "pixel value")).ToArray<AnalysisPlot>();
            return OpticsAnalysisResult.FromSections(Name, new[] { new ReportSection("Source bitmap") { Lines = new[] { sourceLine } } },
                notes.Distinct().ToArray(), plots);
        }

        var result = ImageSimulator.Simulate(setup.Lens, sys, new ImageSimulationRequest
        {
            Scene = scene, Field = f, FieldHeight = ExtendedSceneSupport.FieldValue(settings.FieldHeight, sys), Channels = channels,
            PupilSampling = settings.PupilSampling, ImageSampling = settings.ImageSampling, PsfPointsX = settings.PsfPointsX, PsfPointsY = settings.PsfPointsY,
            Aberrations = settings.Aberrations, Engine = settings.DiffractionPsf, UseRelativeIllumination = settings.UseRelativeIllumination,
            Reference = settings.Reference, DetectorX = settings.XPixels, DetectorY = settings.YPixels,
            PixelSize = OpticsConventions.ToMeters(settings.PixelSize, sys.Units.LensUnit), FlipImage = settings.FlipImage,
        }, cancellation);

        double p = ExtendedSceneSupport.Shown(result.PixelSize, sys);
        var pairs = new List<(string, string)>
        {
            ("Detector", $"{result.Columns} × {result.Rows} pixels of {ExtendedSceneSupport.N(p)} {lu}"),
            ("Image size", $"{ExtendedSceneSupport.N(p * result.Columns)} W × {ExtendedSceneSupport.N(p * result.Rows)} H {lu}"),
            ("Default pixel size", $"{ExtendedSceneSupport.N(ExtendedSceneSupport.Shown(result.DefaultPixelSize, sys))} {lu} (√2/2 × the distance between the chief rays of the centre pixel's corners)"),
            ("Centre", $"{ExtendedSceneSupport.N(ExtendedSceneSupport.Shown(result.CentreX, sys), "g8")}, {ExtendedSceneSupport.N(ExtendedSceneSupport.Shown(result.CentreY, sys), "g8")} {lu} ({settings.Reference})"),
            ("Brightness factor", $"{ExtendedSceneSupport.N(result.Normalisation)} (the image's peak set to the source's)"),
        };
        AnalysisPlot[] plotsOut;
        if (settings.ShowAs == ImageSimulationShow.PsfGrid)
            plotsOut = result.Kernels.Select((ks, ci) => (AnalysisPlot)ExtendedSceneSupport.Map($"PSF grid, {channels[ci].Name}", Mosaic(ks, settings.PsfPointsX, settings.PsfPointsY),
                0, 0, 1, 1, "source pixels", "source pixels", "relative to each PSF's peak")).ToArray();
        else
            plotsOut = result.Images.Select((img, ci) => (AnalysisPlot)ExtendedSceneSupport.Map($"Simulated image, {channels[ci].Name}", img,
                ExtendedSceneSupport.Shown(result.CentreX, sys), ExtendedSceneSupport.Shown(result.CentreY, sys), p, p, $"x ({lu})", $"y ({lu})", "pixel value")).ToArray();
        if (!string.IsNullOrWhiteSpace(settings.OutputFile))
        {
            double full = double.IsFinite(scene.FullScale) && scene.FullScale > 0 ? scene.FullScale : scene.Maximum;
            var maps = result.Images.Count == 3 ? result.Images : new[] { result.Images[0] };
            SceneFile.WriteBitmap(settings.OutputFile, maps, full, Name);
            pairs.Add(("Written", settings.OutputFile));
        }
        var assumptions = new List<string> { sourceLine };
        assumptions.AddRange(result.Notes);
        assumptions.Add("The image is shown looking down the image surface's −z axis (x right, y up); positions are the image surface's local x, y (a curved image surface is projected onto its xy plane, help).");
        assumptions.Add("Not built: polarization, afocal image space, JPG files, the Suppress Frame switch.");
        if (setup.ReplacedVignetting) assumptions.Add("The fields' vignetting factors were replaced by surface apertures.");
        assumptions.AddRange(notes.Distinct());
        var sections = new List<ReportSection>();
        if (result.Warnings.Count > 0) sections.Add(new ReportSection("Sampling and validity warnings") { Lines = result.Warnings });
        sections.Add(new ReportSection("Image simulation") { Pairs = pairs });
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plotsOut);
    }

    /// <summary>The colours: their wavelength rows (appending the help's RGB wavelengths when asked) and source maps.</summary>
    internal static (OpticalSystemData System, IReadOnlyList<SimulationChannel> Channels) Channels(OpticalSystemData system, SceneImage scene,
        ImageSimulationSettings settings, List<string> notes)
    {
        const string who = "Image simulation";
        int count = system.Wavelengths.Wavelengths.Count;
        bool rgbScene = scene.IsRgb;
        int MapFor(int colour, int wavelengthRow)
        {
            if (rgbScene) return colour;
            if (scene.Channels.Count == 1) return 0;
            if (wavelengthRow < scene.Channels.Count) return wavelengthRow;
            throw new ArgumentException($"{who}: the IMA file has {scene.Channels.Count} pixel maps; wavelength {wavelengthRow + 1} has none.");
        }
        switch (settings.Wavelengths)
        {
            case SceneWavelengths.Rgb:
            {
                var (sys, rows) = ExtendedSceneSupport.WithWavelengths(system, ExtendedSceneSupport.ImageSimulationRgb);
                if (!rgbScene && scene.Channels.Count > 1) notes.Add("RGB with a multi-map IMA file: map 1 is imaged in every colour.");
                var list = Enumerable.Range(0, 3).Select(c => new SimulationChannel(rgbScene ? c : 0, rows[c], 1,
                    $"{ExtendedSceneSupport.ColourNames[c]} ({ExtendedSceneSupport.ImageSimulationRgb[c]:g4} µm)")).ToArray();
                notes.Add("Wavelength RGB: red 0.606, green 0.535, blue 0.465 µm (help), whatever the system's wavelengths.");
                return (sys, list);
            }
            case SceneWavelengths.Wavelengths123:
            {
                if (count < 3) throw new ArgumentException($"{who}: \"1+2+3\" needs three wavelengths; the system has {count}. Use RGB or a single wavelength.");
                bool weighted = !rgbScene && scene.Channels.Count == 1 && scene.Kind is SceneFileKind.ImaText or SceneFileKind.ImaBinary or SceneFileKind.Bim && count == 3;
                double maxWeight = Enumerable.Range(0, 3).Max(i => system.Wavelengths.Wavelengths[i].Weight);
                var list = new[] { 0, 1, 2 }.Select(c =>
                {
                    int row = 2 - c; // red on wavelength 3, green on 2, blue on 1
                    double gain = weighted && maxWeight > 0 ? system.Wavelengths.Wavelengths[row].Weight / maxWeight : 1;
                    return new SimulationChannel(MapFor(c, row), row, gain, $"{ExtendedSceneSupport.ColourNames[c]} (wavelength {row + 1}, {system.Wavelengths.Wavelengths[row].Micrometers:g6} µm)");
                }).ToArray();
                notes.Add(weighted
                    ? "Wavelength 1+2+3 with a monochrome file and three wavelengths: the wavelength weights set the colour balance (help)."
                    : "Wavelength 1+2+3: red channel on wavelength 3, green on 2, blue on 1; wavelength weights are not used (help).");
                return (system, list);
            }
            default:
            {
                int k = settings.WavelengthNumber;
                if (k < 1 || k > count) throw new ArgumentException($"{who}: wavelength {k} is not one of the {count} wavelengths.");
                int colour = ExtendedSceneSupport.ChannelOf(k, redAboveThree: false);
                var ch = new SimulationChannel(MapFor(colour, k - 1), k - 1, 1,
                    rgbScene ? $"{ExtendedSceneSupport.ColourNames[colour]} channel at wavelength {k} ({system.Wavelengths.Wavelengths[k - 1].Micrometers:g6} µm)"
                             : $"wavelength {k} ({system.Wavelengths.Wavelengths[k - 1].Micrometers:g6} µm)");
                return (system, new[] { ch });
            }
        }
    }

    /// <summary>The kernels tiled in grid order (node row 0 at the top), each over its own peak.</summary>
    internal static double[,] Mosaic(IReadOnlyList<PixelKernel> kernels, int gx, int gy)
    {
        int th = kernels.Max(k => k.Rows) + 2, tw = kernels.Max(k => k.Columns) + 2;
        var m = new double[gy * th, gx * tw];
        for (int b = 0; b < gy; b++)
            for (int a = 0; a < gx; a++)
            {
                var k = kernels[b * gx + a];
                double peak = 0;
                foreach (var v in k.Weights) peak = Math.Max(peak, v);
                if (!(peak > 0)) continue;
                int r0 = b * th + th / 2 - k.CentreRow, c0 = a * tw + tw / 2 - k.CentreColumn;
                for (int r = 0; r < k.Rows; r++)
                    for (int c = 0; c < k.Columns; c++) m[r0 + r, c0 + c] = k.Weights[r, c] / peak;
            }
        return m;
    }
}

/// <summary>What the Geometric Image Analysis shows (help, "Show": "surface plot, contour map,
/// grey scale, false color map, spot diagram, cross x, or cross y"; the surface plot is not built
/// and grey scale and false colour are one map whose colours the window picks).</summary>
public enum GeometricImageShow
{
    SpotDiagram = 0,
    Map = 1,
    Contour = 2,
    CrossX = 3,
    CrossY = 4,
}

/// <summary>The detector reference of the Geometric Image Analysis (help, "Reference": "chief ray,
/// surface vertex, primary wavelength chief ray, or centroid").</summary>
public enum GeometricImageReference
{
    ChiefRay = 0,
    Vertex = 1,
    PrimaryChiefRay = 2,
    Centroid = 3,
}

/// <summary>Object parity (help: "Even" leaves the object as seen down −z in object space, "Odd"
/// "reverses the object from top to bottom").</summary>
public enum SceneParity
{
    Even = 0,
    Odd = 1,
}

/// <summary>Settings of the <see cref="GeometricImageAnalysis"/>.</summary>
public sealed record GeometricImageSettings
{
    [DisplayName("File"), Description("The full path of the .IMA or .BIM source.")]
    public string File { get; init; } = "";

    [DisplayName("Field size"), Description("The square source's full width in field units (degrees, or lens units for heights).")]
    public double FieldSize { get; init; } = 0.1;

    [DisplayName("Image size"), Description("The detector's full width in lens units (the scale bar of the spot diagram).")]
    public double ImageSize { get; init; } = 0.1;

    [DisplayName("Parity"), Description("Even, or Odd (reversed top to bottom).")]
    public SceneParity Parity { get; init; } = SceneParity.Even;

    [DisplayName("Rotation"), Description("Degrees, counter-clockwise; the object is turned before tracing.")]
    public double Rotation { get; init; }

    [DisplayName("Rays x 1000"), Description("About 1000 × this many rays (at least one per lit pixel and wavelength); 1 to 10000.")]
    public int RaysThousands { get; init; } = 10;

    [DisplayName("Show"), Description("Spot diagram, map, contour, or cross-section x or y.")]
    public GeometricImageShow Show { get; init; } = GeometricImageShow.Map;

    [DisplayName("Source"), Description("Uniform, or Lambertian (weighted by the cosine to the object normal).")]
    public SceneSource Source { get; init; } = SceneSource.Uniform;

    [DisplayName("Use polarization"), Description("Not built: refused.")]
    public bool UsePolarization { get; init; }

    [DisplayName("Scatter rays"), Description("Not built (sequential surface scattering): refused.")]
    public bool ScatterRays { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (rays shared by the wavelength weights).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("The field row the source is centred on.")]
    public int Field { get; init; } = 1;

    [DisplayName("Surface"), Description("The surface the rays are read at; 0 for the image.")]
    public int Surface { get; init; }

    [DisplayName("# Pixels"), Description("Detector pixels across the image size; 2 to 4096.")]
    public int Pixels { get; init; } = 100;

    [DisplayName("NA"), Description("Ignore rays with n·sin θ above this at the surface; 0 for none.")]
    public double NumericalAperture { get; init; }

    [DisplayName("Total watts"), Description("The power radiated into the entrance pupil.")]
    public double TotalWatts { get; init; } = 1;

    [DisplayName("Plot scale"), Description("The top of the colour scale (W per pixel); 0 for the largest pixel.")]
    public double PlotScale { get; init; }

    [DisplayName("Reference"), Description("The detector centre: chief ray, vertex, primary wavelength chief ray, or centroid.")]
    public GeometricImageReference Reference { get; init; } = GeometricImageReference.ChiefRay;

    [DisplayName("Remove vignetting factors"), Description("Replace the fields' vignetting factors by surface apertures.")]
    public bool RemoveVignettingFactors { get; init; } = true;

    [DisplayName("Delete vignetted"), Description("Drop rays that pass the surface but are stopped further on.")]
    public bool DeleteVignetted { get; init; }

    [DisplayName("Use pixel interpolation"), Description("Share each ray's energy among the four nearest pixels.")]
    public bool UsePixelInterpolation { get; init; }

    [DisplayName("Save as BIM file"), Description("Full path of a .BIM to write the detector image to; empty for none.")]
    public string SaveAsBimFile { get; init; } = "";
}

/// <summary>
/// OpticStudio's Geometric Image Analysis (OP-42): rays from every lit pixel of an IMA or BIM
/// source (<see cref="GeometricSceneTracer"/>, Sobol positions as OP-41's extended source) binned
/// on a detector, with the efficiency. Not built: polarization, scattering, the surface plot,
/// symbols per wavelength (each wavelength is its own series), the text listing of every ray.
/// </summary>
public sealed class GeometricImageAnalysis : WavefrontAnalysisBase<GeometricImageSettings>
{
    public const string SavedId = "GeometricImageAnalysis";

    /// <summary>The most rays drawn in a spot diagram (the rest are counted, not drawn).</summary>
    public const int MaximumDrawn = 50_000;

    public GeometricImageAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Geometric image analysis";
    public override string Category => ExtendedSceneSupport.Category;

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        GeometricImageSettings settings, CancellationToken cancellation) =>
        Evaluate(lens, system, notes, settings, cancellation).Result;

    // wire-13: the ZPL function IMAE ("Returns the geometric image analysis efficiency") reads the
    // efficiency as computed here, not from the report's rounded text.
    /// <summary>The analysis and its efficiency (a fraction: the rays counted over every ray
    /// launched, by weight; every unvignetted ray for the spot diagram, the rays on the detector
    /// otherwise).</summary>
    internal (OpticsAnalysisResult Result, double Efficiency) Evaluate(SequentialLens lens, OpticalSystemData system, List<string> notes,
        GeometricImageSettings settings, CancellationToken cancellation)
    {
        Check(!settings.UsePolarization, $"{Name}: polarization is not built; turn \"Use polarization\" off.");
        Check(!settings.ScatterRays, $"{Name}: sequential surface scattering is not built (OP-80 built scattering in the non-sequential trace only); turn \"Scatter rays\" off.");
        Check(settings.FieldSize > 0 && double.IsFinite(settings.FieldSize), $"{Name}: the field size must be positive.");
        Check(settings.ImageSize > 0 && double.IsFinite(settings.ImageSize), $"{Name}: the image size must be positive.");
        Check(settings.RaysThousands >= 1 && settings.RaysThousands <= 10000, $"{Name}: Rays x 1000 must be 1 to 10000.");
        Check(settings.Pixels >= 2 && settings.Pixels <= 4096, $"{Name}: the pixels must be 2 to 4096.");
        Check(settings.TotalWatts > 0 && double.IsFinite(settings.TotalWatts), $"{Name}: the total watts must be positive.");
        Check(settings.PlotScale >= 0, $"{Name}: the plot scale must be 0 or positive.");
        var scene = SceneFile.Read(settings.File, SceneFileUse.ImaOrBim, Name);
        Check(scene.Width == scene.Height, $"{Name}: the file must be square.");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        var waves = PsfRuns.Waves(settings.Wavelength, system, Name);
        int surface = RayAnalysisSupport.Surface(Name, settings.Surface, lens);
        var setup = ExtendedSceneSupport.Prepare(lens, system, settings.RemoveVignettingFactors, false, false, Name, notes, cancellation);
        var sys = setup.System;
        bool poly = waves.Count > 1;
        var channels = waves.Select(w =>
        {
            int map = scene.Channels.Count == 1 ? 0 : w < scene.Channels.Count ? w
                : throw new ArgumentException($"{Name}: the file has {scene.Channels.Count} pixel maps; wavelength {w + 1} has none.");
            return new TracedChannel(map, w, poly ? sys.Wavelengths.Wavelengths[w].Weight : 1, RayAnalysisSupport.WaveLabel(sys, w));
        }).ToArray();
        var traced = GeometricSceneTracer.Trace(setup.Lens, sys, new GeometricSceneRequest
        {
            Scene = scene, Field = f, PixelField = ExtendedSceneSupport.FieldValue(settings.FieldSize, sys) / scene.Width, Channels = channels,
            TotalRays = 1000 * settings.RaysThousands, OddParity = settings.Parity == SceneParity.Odd, RotationDegrees = settings.Rotation,
            Source = settings.Source, Surface = surface, DeleteVignetted = settings.DeleteVignetted, NumericalAperture = settings.NumericalAperture,
        }, Name, cancellation);

        // The reference point.
        double rx, ry;
        int primaryChannel = Array.IndexOf(waves.ToArray(), sys.Wavelengths.PrimaryIndex);
        switch (settings.Reference)
        {
            case GeometricImageReference.Vertex: rx = 0; ry = 0; break;
            case GeometricImageReference.Centroid:
            {
                double sw = traced.Hits.Sum(h => h.Weight);
                Check(sw > 0, $"{Name}: no ray reaches surface {surface}, so there is no centroid.");
                rx = traced.Hits.Sum(h => h.Weight * h.X) / sw; ry = traced.Hits.Sum(h => h.Weight * h.Y) / sw;
                break;
            }
            default:
            {
                (double X, double Y) chief;
                if (settings.Reference == GeometricImageReference.PrimaryChiefRay && primaryChannel < 0)
                {
                    var one = GeometricSceneTracer.Trace(setup.Lens, sys, new GeometricSceneRequest
                    {
                        Scene = SceneImage.Grey(new double[,] { { 1 } }), Field = f, PixelField = 1e-12, Channels = new[] { new TracedChannel(0, sys.Wavelengths.PrimaryIndex, 1, "primary") },
                        RaysPerPixel = 1, Surface = surface,
                    }, Name, cancellation);
                    chief = one.Chief[0];
                }
                else chief = traced.Chief[settings.Reference == GeometricImageReference.PrimaryChiefRay || poly ? Math.Max(0, primaryChannel) : 0];
                Check(double.IsFinite(chief.X), $"{Name}: the chief ray of field {f + 1} does not reach surface {surface}; choose the vertex or the centroid as the reference.");
                (rx, ry) = chief;
                break;
            }
        }
        double size = OpticsConventions.ToMeters(settings.ImageSize, sys.Units.LensUnit);
        double pitch = size / settings.Pixels;
        var (maps, detected) = GeometricSceneTracer.Bin(traced.Hits, channels.Length, rx, ry, settings.Pixels, settings.Pixels, pitch, pitch, settings.UsePixelInterpolation);
        double launched = traced.Launched.Sum();
        var power = new double[settings.Pixels, settings.Pixels];
        foreach (var m in maps)
            for (int r = 0; r < settings.Pixels; r++)
                for (int c = 0; c < settings.Pixels; c++) power[r, c] += m[r, c] * settings.TotalWatts / launched;
        double onSurface = traced.Hits.Sum(h => h.Weight);
        double onDetector = detected.Sum();
        bool spot = settings.Show == GeometricImageShow.SpotDiagram;
        double efficiency = (spot ? onSurface : onDetector) / launched;

        string lu = ExtendedSceneSupport.LensUnit(sys);
        string unit = ExtendedSceneSupport.FieldUnit(sys);
        double shownPitch = ExtendedSceneSupport.Shown(pitch, sys);
        double cx = ExtendedSceneSupport.Shown(rx, sys), cy = ExtendedSceneSupport.Shown(ry, sys);
        AnalysisPlot plot;
        switch (settings.Show)
        {
            case GeometricImageShow.SpotDiagram:
            {
                var series = channels.Select((ch, ci) => new AnalysisScatterSeries(ch.Name, traced.Hits.Where(h => h.Channel == ci).Take(MaximumDrawn / channels.Length)
                    .Select(h => new AnalysisPoint(ExtendedSceneSupport.Shown(h.X, sys), ExtendedSceneSupport.Shown(h.Y, sys), "")).ToArray())).ToArray();
                plot = new AnalysisScatterPlot($"Geometric image, field {f + 1}", $"x ({lu})", $"y ({lu})", series);
                if (traced.Hits.Count > MaximumDrawn) notes.Add($"The spot diagram draws {MaximumDrawn} of the {traced.Hits.Count} rays.");
                break;
            }
            case GeometricImageShow.CrossX:
            case GeometricImageShow.CrossY:
            {
                bool alongX = settings.Show == GeometricImageShow.CrossX;
                var (xs, ys) = ExtendedSceneSupport.CrossSection(power, cx, cy, shownPitch, shownPitch, alongX);
                plot = new LinePlot($"Geometric image, cross-section {(alongX ? "x" : "y")}", $"{(alongX ? "x" : "y")} ({lu})", "W per pixel",
                    new[] { new AnalysisCurve("power", xs, ys) });
                break;
            }
            default:
                plot = ExtendedSceneSupport.Map($"Geometric image, field {f + 1}", power, cx, cy, shownPitch, shownPitch, $"x ({lu})", $"y ({lu})", "W per pixel",
                    settings.Show == GeometricImageShow.Contour ? AnalysisMapStyle.Contour : AnalysisMapStyle.Image,
                    settings.PlotScale > 0 ? settings.PlotScale : double.NaN);
                break;
        }
        var pairs = new List<(string, string)>
        {
            ("Efficiency", $"{ExtendedSceneSupport.N(100 * efficiency)} % ({(spot ? "every unvignetted ray" : "rays on the detector")} over every ray launched, by weight)"),
            ("Total power", $"{ExtendedSceneSupport.N(settings.TotalWatts * efficiency)} W of {ExtendedSceneSupport.N(settings.TotalWatts)} W"),
            ("Rays", $"{traced.RaysLaunched} launched, {traced.Hits.Count} reached surface {surface}"),
            ("Detector", $"{settings.Pixels} × {settings.Pixels} pixels of {ExtendedSceneSupport.N(shownPitch)} {lu}, centred on ({ExtendedSceneSupport.N(cx, "g8")}, {ExtendedSceneSupport.N(cy, "g8")}) {lu} ({settings.Reference})"),
        };
        if (!string.IsNullOrWhiteSpace(settings.SaveAsBimFile))
        {
            Check(!spot, $"{Name}: a BIM file is saved only when \"Show\" is not the spot diagram (help).");
            BimFile.Write(settings.SaveAsBimFile, power);
            pairs.Add(("Written", settings.SaveAsBimFile));
        }
        var assumptions = new List<string>
        {
            $"Source {Path.GetFileName(settings.File)}: {scene.Width} × {scene.Height} pixels ({scene.Kind}), {settings.FieldSize:g6}{unit} wide, centred on field {f + 1}, parity {settings.Parity}, rotated {settings.Rotation:g6}° counter-clockwise; row 1 the top (+y).",
            $"About {1000 * settings.RaysThousands} rays shared by pixel value × wavelength weight (at least one per lit pixel and wavelength); positions in the pixel and the pupil from a 4-D Sobol sequence instead of the help's random draws, so the result repeats exactly; {settings.Source} source.",
            $"Each pixel holds the power of the rays striking it{(settings.UsePixelInterpolation ? ", shared bilinearly among the four nearest pixel centres" : "")}: total watts × ray weight / Σ launched weight.",
            "Not built: polarization, scattering, the surface plot, the ray listing.",
        };
        if (traced.Failures.Count > 0) assumptions.Add("Rays not counted: " + string.Join("; ", traced.Failures) + ".");
        if (setup.ReplacedVignetting) assumptions.Add("The fields' vignetting factors were replaced by surface apertures.");
        assumptions.AddRange(notes.Distinct());
        return (OpticsAnalysisResult.FromSections(Name, new[] { new ReportSection("Geometric image analysis") { Pairs = pairs } }, assumptions, new[] { plot }), efficiency);
    }
}

/// <summary>How the Geometric Bitmap Image Analysis normalises (help, "Normalize": "by either the
/// peak pixel intensity or by the average pixel intensity").</summary>
public enum BitmapNormalization
{
    Peak = 0,
    Average = 1,
}

/// <summary>The Geometric Bitmap Image Analysis's reference ("either the chief ray or the surface vertex").</summary>
public enum BitmapReference
{
    ChiefRay = 0,
    Vertex = 1,
}

/// <summary>Settings of the <see cref="GeometricBitmapImageAnalysis"/>.</summary>
public sealed record GeometricBitmapImageSettings
{
    [DisplayName("Input"), Description("The full path of the .BMP (24/32-bit) or .PNG (8-bit) source. JPG is not read.")]
    public string File { get; init; } = "";

    [DisplayName("Field y size"), Description("The source's full height in field units (degrees, or lens units for heights).")]
    public double FieldYSize { get; init; } = 1;

    [DisplayName("Parity"), Description("Even, or Odd (reversed top to bottom).")]
    public SceneParity Parity { get; init; } = SceneParity.Even;

    [DisplayName("Rotation"), Description("Degrees, counter-clockwise; the object is turned before tracing.")]
    public double Rotation { get; init; }

    [DisplayName("Rays/pixel"), Description("Rays per lit source pixel and colour channel; 1 to 10000.")]
    public int RaysPerPixel { get; init; } = 10;

    [DisplayName("X pixels"), Description("Detector columns; 2 to 4096.")]
    public int XPixels { get; init; } = 100;

    [DisplayName("Y pixels"), Description("Detector rows; 2 to 4096.")]
    public int YPixels { get; init; } = 100;

    [DisplayName("X pixel size"), Description("Detector pixel width, lens units.")]
    public double XPixelSize { get; init; } = 0.01;

    [DisplayName("Y pixel size"), Description("Detector pixel height, lens units.")]
    public double YPixelSize { get; init; } = 0.01;

    [DisplayName("Source"), Description("Uniform, or Lambertian.")]
    public SceneSource Source { get; init; } = SceneSource.Uniform;

    [DisplayName("Normalize"), Description("By the peak pixel, or by the average (clipped at full scale).")]
    public BitmapNormalization Normalize { get; init; } = BitmapNormalization.Average;

    [DisplayName("Use polarization"), Description("Not built: refused.")]
    public bool UsePolarization { get; init; }

    [DisplayName("Grey scale"), Description("Average the red, green and blue detector images into one grey image.")]
    public bool GreyScale { get; init; }

    [DisplayName("Wavelength"), Description("RGB (0.656, 0.587, 0.486 µm), 1+2+3 (red on 3, green on 2, blue on 1; RGB with fewer than three wavelengths), or a single wavelength.")]
    public SceneWavelengths Wavelengths { get; init; } = SceneWavelengths.Rgb;

    [DisplayName("Wavelength number"), Description("The wavelength row used with Single: blue channel for 1, green for 2, red for 3 and above.")]
    public int WavelengthNumber { get; init; } = 1;

    [DisplayName("Field"), Description("The field row the source is centred on.")]
    public int Field { get; init; } = 1;

    [DisplayName("Surface"), Description("The surface the rays are read at; 0 for the image.")]
    public int Surface { get; init; }

    [DisplayName("Show source bitmap"), Description("Show the source and trace nothing.")]
    public bool ShowSourceBitmap { get; init; }

    [DisplayName("Output"), Description("Full path of a .BMP or .PNG to write the detected image to; empty for none.")]
    public string Output { get; init; } = "";

    [DisplayName("Reference"), Description("Chief ray or surface vertex.")]
    public BitmapReference Reference { get; init; } = BitmapReference.ChiefRay;

    [DisplayName("Delete vignetted"), Description("Drop rays that pass the surface but are stopped further on.")]
    public bool DeleteVignetted { get; init; }

    [DisplayName("Remove vignetting factors"), Description("Replace the fields' vignetting factors by surface apertures.")]
    public bool RemoveVignettingFactors { get; init; } = true;

    [DisplayName("Apply fixed apertures"), Description("Give every powered surface without an aperture a circular one at its semi-diameter.")]
    public bool ApplyFixedApertures { get; init; } = true;
}

/// <summary>
/// OpticStudio's Geometric Bitmap Image Analysis (OP-42): rays from every pixel of a BMP or PNG
/// source, per colour channel at its wavelength, binned on a detector and normalised. Not built:
/// polarization, JPG files, the Suppress Frame switch.
/// </summary>
public sealed class GeometricBitmapImageAnalysis : WavefrontAnalysisBase<GeometricBitmapImageSettings>
{
    public const string SavedId = "GeometricBitmapImageAnalysis";

    public GeometricBitmapImageAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Geometric bitmap image analysis";
    public override string Category => ExtendedSceneSupport.Category;

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        GeometricBitmapImageSettings settings, CancellationToken cancellation)
    {
        Check(!settings.UsePolarization, $"{Name}: polarization is not built; turn \"Use polarization\" off.");
        Check(settings.FieldYSize > 0 && double.IsFinite(settings.FieldYSize), $"{Name}: the field y size must be positive.");
        Check(settings.RaysPerPixel >= 1 && settings.RaysPerPixel <= 10000, $"{Name}: rays per pixel must be 1 to 10000.");
        Check(settings.XPixels >= 2 && settings.XPixels <= 4096 && settings.YPixels >= 2 && settings.YPixels <= 4096, $"{Name}: the detector must be 2 to 4096 pixels each way.");
        Check(settings.XPixelSize > 0 && settings.YPixelSize > 0, $"{Name}: the pixel sizes must be positive.");
        var scene = SceneFile.Read(settings.File, SceneFileUse.Bitmap, Name);
        string unit = ExtendedSceneSupport.FieldUnit(system);
        string lu = ExtendedSceneSupport.LensUnit(system);
        string sourceLine = $"Source {Path.GetFileName(settings.File)}: {scene.Width} × {scene.Height} pixels ({scene.Kind}{(scene.IsRgb ? ", red, green, blue" : ", grey")}), {settings.FieldYSize:g6}{unit} high; row 1 the top (+y).";
        if (settings.ShowSourceBitmap)
        {
            double pf = settings.FieldYSize / scene.Height;
            var plots = scene.Channels.Select((m, i) => (AnalysisPlot)ExtendedSceneSupport.Map($"Source bitmap, {scene.ChannelNames[i]}", m, 0, 0, pf, pf,
                $"x field ({unit.Trim()})", $"y field ({unit.Trim()})", "pixel value")).ToArray();
            return OpticsAnalysisResult.FromSections(Name, new[] { new ReportSection("Source bitmap") { Lines = new[] { sourceLine } } }, new[] { "No rays are traced (Show source bitmap)." }, plots);
        }
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        int surface = RayAnalysisSupport.Surface(Name, settings.Surface, lens);
        var setup = ExtendedSceneSupport.Prepare(lens, system, settings.RemoveVignettingFactors, settings.ApplyFixedApertures, true, Name, notes, cancellation);
        var (sys, channels) = Channels(setup.System, scene, settings, notes);
        var traced = GeometricSceneTracer.Trace(setup.Lens, sys, new GeometricSceneRequest
        {
            Scene = scene, Field = f, PixelField = ExtendedSceneSupport.FieldValue(settings.FieldYSize, sys) / scene.Height, Channels = channels,
            RaysPerPixel = settings.RaysPerPixel, OddParity = settings.Parity == SceneParity.Odd, RotationDegrees = settings.Rotation,
            Source = settings.Source, Surface = surface, DeleteVignetted = settings.DeleteVignetted,
        }, Name, cancellation);
        double rx = 0, ry = 0;
        if (settings.Reference == BitmapReference.ChiefRay)
        {
            var chief = traced.Chief[channels.Length / 2];
            Check(double.IsFinite(chief.X), $"{Name}: the chief ray of field {f + 1} does not reach surface {surface}; choose the vertex as the reference.");
            (rx, ry) = chief;
        }
        double px = OpticsConventions.ToMeters(settings.XPixelSize, sys.Units.LensUnit), py = OpticsConventions.ToMeters(settings.YPixelSize, sys.Units.LensUnit);
        var (maps, detected) = GeometricSceneTracer.Bin(traced.Hits, channels.Length, rx, ry, settings.XPixels, settings.YPixels, px, py, false);
        double efficiency = detected.Sum() / traced.Launched.Sum();

        // Normalise: the peak to full scale, or the mean to the source's mean (clipped at full scale).
        double full = double.IsFinite(scene.FullScale) && scene.FullScale > 0 ? scene.FullScale : scene.Maximum;
        var images = maps;
        if (settings.GreyScale && images.Length > 1)
        {
            var grey = new double[settings.YPixels, settings.XPixels];
            foreach (var m in images)
                for (int r = 0; r < settings.YPixels; r++) for (int c = 0; c < settings.XPixels; c++) grey[r, c] += m[r, c] / images.Length;
            images = new[] { grey };
        }
        double factor;
        if (settings.Normalize == BitmapNormalization.Peak)
        {
            double peak = images.SelectMany(m => m.Cast<double>()).Max();
            Check(peak > 0, $"{Name}: no light reaches the detector.");
            factor = full / peak;
        }
        else
        {
            double mean = images.SelectMany(m => m.Cast<double>()).Average();
            double sourceMean = channels.Select(c => scene.Channels[c.SceneChannel]).SelectMany(m => m.Cast<double>()).Average();
            Check(mean > 0, $"{Name}: no light reaches the detector.");
            factor = sourceMean / mean;
        }
        int clipped = 0;
        foreach (var m in images)
            for (int r = 0; r < settings.YPixels; r++)
                for (int c = 0; c < settings.XPixels; c++)
                {
                    double v = m[r, c] * factor;
                    if (v > full) { v = full; clipped++; }
                    m[r, c] = v;
                }
        double spx = ExtendedSceneSupport.Shown(px, sys), spy = ExtendedSceneSupport.Shown(py, sys);
        double cx = ExtendedSceneSupport.Shown(rx, sys), cy = ExtendedSceneSupport.Shown(ry, sys);
        var plotsOut = images.Select((m, i) => (AnalysisPlot)ExtendedSceneSupport.Map(
            $"Geometric bitmap image, {(images.Length == 1 && channels.Length > 1 ? "grey" : channels[i].Name)}", m, cx, cy, spx, spy, $"x ({lu})", $"y ({lu})", "pixel value",
            maximum: full)).ToArray();
        var pairs = new List<(string, string)>
        {
            ("Efficiency", $"{ExtendedSceneSupport.N(100 * efficiency)} % (rays on the detector over every ray launched, by weight)"),
            ("Rays", $"{traced.RaysLaunched} launched, {traced.Hits.Count} reached surface {surface}"),
            ("Detector", $"{settings.XPixels} × {settings.YPixels} pixels of {ExtendedSceneSupport.N(spx)} × {ExtendedSceneSupport.N(spy)} {lu}, centred on ({ExtendedSceneSupport.N(cx, "g8")}, {ExtendedSceneSupport.N(cy, "g8")}) {lu}"),
            ("Normalisation", settings.Normalize == BitmapNormalization.Peak ? "peak pixel at full scale" : $"mean equal to the source's; {clipped} pixel values clipped at full scale"),
        };
        if (!string.IsNullOrWhiteSpace(settings.Output))
        {
            SceneFile.WriteBitmap(settings.Output, images, full, Name);
            pairs.Add(("Written", settings.Output));
        }
        var assumptions = new List<string>
        {
            sourceLine,
            $"{settings.RaysPerPixel} rays per lit pixel and colour; positions in the pixel and the pupil from a 4-D Sobol sequence instead of the help's random draws; {settings.Source} source; parity {settings.Parity}; rotated {settings.Rotation:g6}° counter-clockwise.",
            "Not built: polarization, JPG files, the Suppress Frame switch.",
        };
        if (traced.Failures.Count > 0) assumptions.Add("Rays not counted: " + string.Join("; ", traced.Failures) + ".");
        if (setup.ReplacedVignetting) assumptions.Add("The fields' vignetting factors were replaced by surface apertures.");
        assumptions.AddRange(notes.Distinct());
        return OpticsAnalysisResult.FromSections(Name, new[] { new ReportSection("Geometric bitmap image analysis") { Pairs = pairs } }, assumptions, plotsOut);
    }

    /// <summary>The colour channels and wavelengths (help, "Wavelength").</summary>
    internal static (OpticalSystemData System, TracedChannel[] Channels) Channels(OpticalSystemData system, SceneImage scene,
        GeometricBitmapImageSettings settings, List<string> notes)
    {
        int count = system.Wavelengths.Wavelengths.Count;
        int Map(int colour) => scene.IsRgb ? colour : 0;
        var mode = settings.Wavelengths;
        if (mode == SceneWavelengths.Wavelengths123 && count < 3)
        {
            notes.Add($"\"1+2+3\" with {count} wavelength(s) reverts to RGB (help).");
            mode = SceneWavelengths.Rgb;
        }
        switch (mode)
        {
            case SceneWavelengths.Rgb:
            {
                var (sys, rows) = ExtendedSceneSupport.WithWavelengths(system, ExtendedSceneSupport.GeometricBitmapRgb);
                notes.Add("Wavelength RGB: red 0.656, green 0.587, blue 0.486 µm (help), whatever the system's wavelengths.");
                return (sys, Enumerable.Range(0, 3).Select(c => new TracedChannel(Map(c), rows[c], 1,
                    $"{ExtendedSceneSupport.ColourNames[c]} ({ExtendedSceneSupport.GeometricBitmapRgb[c]:g4} µm)")).ToArray());
            }
            case SceneWavelengths.Wavelengths123:
                notes.Add("Wavelength 1+2+3: red on wavelength 3, green on 2, blue on 1 (help).");
                return (system, Enumerable.Range(0, 3).Select(c => new TracedChannel(Map(c), 2 - c, 1,
                    $"{ExtendedSceneSupport.ColourNames[c]} (wavelength {3 - c})")).ToArray());
            default:
            {
                int k = settings.WavelengthNumber;
                if (k < 1 || k > count) throw new ArgumentException($"Geometric bitmap image analysis: wavelength {k} is not one of the {count} wavelengths.");
                int colour = ExtendedSceneSupport.ChannelOf(k, redAboveThree: true);
                return (system, new[] { new TracedChannel(Map(colour), k - 1, 1, $"{ExtendedSceneSupport.ColourNames[colour]} channel at wavelength {k}") });
            }
        }
    }
}
