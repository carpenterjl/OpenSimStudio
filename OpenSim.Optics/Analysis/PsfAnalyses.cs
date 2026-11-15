using System.ComponentModel;
using OpenSim.Core.Numerics;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis;

// ---------------------------------------------------------------------------------------------
// OP-38: point spread functions and the Strehl ratio. Each analysis reads the project's saved
// lens (ProjectLens) unless a lens source is given, builds the pupil function with PsfEngine and
// states its sampling, reference point and any sampling warning in the result. The settings and
// their meaning come from the Ansys OpticStudio 2025 R2 help pages "FFT PSF", "FFT Cross
// Section", "FFT Line/Edge Spread", "Huygens PSF", "Huygens Cross Section" and "Strehl Ratio",
// saved under plans/sources/optics/op-38-help/ (docs/Optics-Conventions.md §33).
// ---------------------------------------------------------------------------------------------

/// <summary>What a PSF map or cross-section shows (help, "FFT PSF": "linear (intensity),
/// logarithmic (intensity), phase, real part (signed amplitude), or imaginary part (signed
/// amplitude)").</summary>
public enum PsfDisplayType
{
    Linear = 0,
    Logarithmic = 1,
    RealPart = 2,
    ImaginaryPart = 3,
    /// <summary>The phase of the complex amplitude, degrees.</summary>
    Phase = 4,
}

/// <summary>Which way a cross-section or a line/edge spread runs. X: a row (the help: "X cross
/// sections are called rows"); for the line/edge spread, a line parallel to the x axis, so the
/// curve runs along y.</summary>
public enum PsfDirection
{
    X = 0,
    Y = 1,
}

/// <summary>Line or edge spread function.</summary>
public enum SpreadFunction
{
    Line = 0,
    Edge = 1,
}

/// <summary>Settings of the <see cref="FftPsfAnalysis"/>.</summary>
public sealed record FftPsfSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes, zero-padded to 2n × 2n; 16 to 1024.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Display"), Description("Image samples per side drawn about the centre, 8 up to twice the sampling; 0 for all.")]
    public int Display { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic, on the shortest wavelength's grid).")]
    public int Wavelength { get; init; }

    [DisplayName("Type"), Description("Linear or logarithmic intensity, real or imaginary amplitude, or phase (degrees).")]
    public PsfDisplayType Type { get; init; } = PsfDisplayType.Linear;

    [DisplayName("Log decades"), Description("Decades below the peak a logarithmic display reaches, 1 to 10.")]
    public int LogDecades { get; init; } = 5;

    [DisplayName("Show as"), Description("False-colour image or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Image delta (µm)"), Description("Spacing of the image samples; 0 for the FFT's own spacing. It may not exceed the FFT's own (the window would be wider than the PSF's period).")]
    public double ImageDeltaMicrometers { get; init; }

    [DisplayName("Normalize"), Description("Scale the peak to 1; off, the peak is the Strehl ratio.")]
    public bool Normalize { get; init; }

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light; off traces through every aperture.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="FftPsfCrossSectionAnalysis"/>.</summary>
public sealed record FftPsfCrossSectionSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes, zero-padded to 2n × 2n; 16 to 1024.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Row/column"), Description("The row (X) or column (Y) of the 2n × 2n image, 1 for the first; 0 for the centre (the reference point, not always the peak).")]
    public int RowColumn { get; init; }

    [DisplayName("Plot scale (µm)"), Description("Half-width of the plotted section; 0 for the whole window.")]
    public double PlotScaleMicrometers { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Direction"), Description("X: a row, against x; Y: a column, against y.")]
    public PsfDirection Direction { get; init; } = PsfDirection.X;

    [DisplayName("Type"), Description("Linear or logarithmic intensity, real or imaginary amplitude, or phase (degrees).")]
    public PsfDisplayType Type { get; init; } = PsfDisplayType.Linear;

    [DisplayName("Log decades"), Description("Decades below the peak a logarithmic section reaches, 1 to 10.")]
    public int LogDecades { get; init; } = 5;

    [DisplayName("Normalize"), Description("Scale the peak to 1; off, the peak is the Strehl ratio.")]
    public bool Normalize { get; init; }

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="FftLineEdgeSpreadAnalysis"/>.</summary>
public sealed record FftLineEdgeSpreadSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes, zero-padded to 2n × 2n; 16 to 1024.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Spread"), Description("Line or edge spread function.")]
    public SpreadFunction Spread { get; init; } = SpreadFunction.Line;

    [DisplayName("Plot scale (µm)"), Description("Half-width of the plot; 0 for the whole window.")]
    public double PlotScaleMicrometers { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Direction"), Description("X: a line or edge parallel to the x axis (the curve runs along y); Y: parallel to y.")]
    public PsfDirection Direction { get; init; } = PsfDirection.X;

    [DisplayName("Logarithmic"), Description("Plot log10 of the value (floored at 10⁻⁶).")]
    public bool Logarithmic { get; init; }

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="HuygensPsfAnalysis"/>.</summary>
public sealed record HuygensPsfSettings
{
    [DisplayName("Pupil sampling"), Description("n: an n × n grid of pupil nodes traced; 8 to 512.")]
    public int PupilSampling { get; init; } = 32;

    [DisplayName("Image sampling"), Description("m: an m × m grid of image points; 8 to 512.")]
    public int ImageSampling { get; init; } = 64;

    [DisplayName("Image delta (µm)"), Description("Spacing of the image points; 0 for the help's default λF/√n.")]
    public double ImageDeltaMicrometers { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Type"), Description("Linear or logarithmic intensity, real or imaginary amplitude, or phase (degrees).")]
    public PsfDisplayType Type { get; init; } = PsfDisplayType.Linear;

    [DisplayName("Log decades"), Description("Decades below the peak a logarithmic display reaches, 1 to 10.")]
    public int LogDecades { get; init; } = 5;

    [DisplayName("Show as"), Description("False-colour image or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Normalize"), Description("Scale the peak to 1; off, the peak is the Strehl ratio.")]
    public bool Normalize { get; init; }

    [DisplayName("Use centroid"), Description("Centre the image on the rays' geometric centroid; off, on the chief ray.")]
    public bool UseCentroid { get; init; }

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="HuygensPsfCrossSectionAnalysis"/>.</summary>
public sealed record HuygensPsfCrossSectionSettings
{
    [DisplayName("Pupil sampling"), Description("n: an n × n grid of pupil nodes traced; 8 to 512.")]
    public int PupilSampling { get; init; } = 32;

    [DisplayName("Image sampling"), Description("m: image points along the section; 8 to 4096.")]
    public int ImageSampling { get; init; } = 128;

    [DisplayName("Image delta (µm)"), Description("Spacing of the image points; 0 for the help's default λF/√n.")]
    public double ImageDeltaMicrometers { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Direction"), Description("X: the section along x through the centre; Y: along y.")]
    public PsfDirection Direction { get; init; } = PsfDirection.X;

    [DisplayName("Type"), Description("Linear or logarithmic intensity, real or imaginary amplitude, or phase (degrees).")]
    public PsfDisplayType Type { get; init; } = PsfDisplayType.Linear;

    [DisplayName("Log decades"), Description("Decades below the peak a logarithmic section reaches, 1 to 10.")]
    public int LogDecades { get; init; } = 5;

    [DisplayName("Normalize"), Description("Scale the peak to 1; off, the peak is the Strehl ratio.")]
    public bool Normalize { get; init; }

    [DisplayName("Use centroid"), Description("Centre the section on the rays' geometric centroid; off, on the chief ray.")]
    public bool UseCentroid { get; init; }

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="StrehlRatioAnalysis"/>.</summary>
public sealed record StrehlRatioSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes, zero-padded to 2n × 2n; 16 to 1024.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for every wavelength and the polychromatic ratio.")]
    public int Wavelength { get; init; }

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>A computed PSF of one field with what it rests on.</summary>
internal sealed record PsfRun(PsfImage Image, double Strehl, IReadOnlyList<int> Wavelengths, IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings, IReadOnlyList<(string, string)> Pairs);

/// <summary>The FFT and Huygens computations the OP-38 analyses share.</summary>
internal static class PsfRuns
{
    public static string Um(double meters) => StudyReport.Number(meters * 1e6, "g6");

    public static OpdOptions Options(bool polychromatic, bool checkApertures, bool allow) => new()
    {
        Reference = OpdReference.ExitPupil, ReferencePoint = OpdReferencePoint.ChiefRay, ReferenceToPrimary = polychromatic,
        IgnoreApertures = !checkApertures, AllowOpdLimitations = allow,
    };

    /// <summary>The polychromatic Strehl ratio of the Strehl ratio analysis (also used by OP-40's
    /// RMS analyses): the peak of <see cref="PsfEngine.FftPolychromatic"/> on the shortest
    /// wavelength's FFT grid, refined off the grid by <see cref="PsfEngine.RefinePeak"/> on the
    /// same combination (<see cref="PsfEngine.Combine"/>) sampled anywhere.</summary>
    public static double PolychromaticStrehl(IReadOnlyList<PupilFunction> pupils, IReadOnlyList<double> weights, int? maxDegreeOfParallelism,
        CancellationToken cancellation)
    {
        var image = PsfEngine.FftPolychromatic(pupils, weights, PsfEngine.DefaultPadding, 0, maxDegreeOfParallelism, cancellation);
        var lambdas = pupils.Select(p => p.Wavelength).ToArray();
        return PsfEngine.RefinePeak(image, (x, y) =>
            PsfEngine.Combine(lambdas, weights, i => PsfEngine.Sample(pupils[i], x, y, maxDegreeOfParallelism, cancellation), x, y).Intensity).Value;
    }

    /// <summary>The FFT PSF of one field at the given wavelength rows (more than one:
    /// polychromatic), on the FFT's own grid or at a chosen spacing.</summary>
    public static PsfRun Fft(SequentialLens lens, OpticalSystemData system, int field, IReadOnlyList<int> waves, int sampling,
        double imageDelta, bool checkApertures, bool allow, string who, CancellationToken cancellation)
    {
        bool poly = waves.Count > 1;
        var options = Options(poly, checkApertures, allow);
        var pupils = new List<PupilFunction>();
        foreach (int w in waves)
        {
            cancellation.ThrowIfCancellationRequested();
            pupils.Add(PsfEngine.BuildPupil(lens, system, field, w, sampling, options, cancellation));
        }
        var weights = waves.Select(w => system.Wavelengths.Wavelengths[w].Weight).ToArray();
        int shortest = 0;
        for (int i = 1; i < pupils.Count; i++) if (pupils[i].Wavelength < pupils[shortest].Wavelength) shortest = i;
        var native = PsfEngine.NativeSpacing(pupils[shortest]);
        if (imageDelta > 0 && (imageDelta > native.Dx * (1 + 1e-12) || imageDelta > native.Dy * (1 + 1e-12)))
            throw new ArgumentException(
                $"{who}: the image delta {Um(imageDelta)} µm is larger than the FFT's own spacing ({Um(native.Dx)} × {Um(native.Dy)} µm at {pupils[shortest].Wavelength * 1e6:g6} µm), so the {2 * sampling}-sample window would be wider than the PSF's period and show its copies; use 0 or a smaller delta (the help: \"if the image delta is too big … an error message will be issued\").");

        PsfImage image;
        Func<double[], double[], double[,]> evaluate;
        if (!poly)
        {
            var p = pupils[0];
            if (imageDelta > 0)
            {
                var (xs, ys) = PsfEngine.Grid(2 * sampling, imageDelta, imageDelta);
                image = PsfEngine.Sample(p, xs, ys, null, cancellation);
            }
            else image = PsfEngine.Fft(p, PsfEngine.DefaultPadding, null, cancellation);
            evaluate = (x, y) => PsfEngine.Sample(p, x, y, null, cancellation).Intensity;
        }
        else
        {
            image = PsfEngine.FftPolychromatic(pupils, weights, PsfEngine.DefaultPadding, imageDelta, null, cancellation);
            var lambdas = pupils.Select(p => p.Wavelength).ToArray();
            evaluate = (x, y) => PsfEngine.Combine(lambdas, weights, i => PsfEngine.Sample(pupils[i], x, y, null, cancellation), x, y).Intensity;
        }
        cancellation.ThrowIfCancellationRequested();
        var peak = PsfEngine.RefinePeak(image, evaluate);

        var notes = new List<string>(pupils[0].Notes);
        var warnings = new List<string>();
        for (int i = 0; i < pupils.Count; i++)
        {
            string label = poly ? $"{pupils[i].Wavelength * 1e6:g6} µm: " : "";
            double width = imageDelta > 0 ? 2 * sampling * imageDelta : double.NaN;
            var check = PsfEngine.CheckSampling(pupils[i], imageDelta > 0 ? width : null, imageDelta > 0 ? width : null);
            warnings.AddRange(check.Warnings.Select(x => label + x));
            warnings.AddRange(pupils[i].Warnings.Select(x => label + x));
        }
        notes.Add($"Pupil {sampling} × {sampling}, zero-padded to {2 * sampling} × {2 * sampling} (help, \"FFT PSF\": the PSF is \"distributed over a 64 x 64 size grid\" for 32 × 32 sampling); image on the plane through the reference point normal to the chief ray, x and y along the image surface's x and y with the chief ray's component taken out.");
        notes.Add(imageDelta > 0
            ? $"Image delta {Um(imageDelta)} µm (chosen): the sum is evaluated at those points by the matrix Fourier transform."
            : $"Image delta λ/(n·|a|·2n·ΔP) = {Um(image.DeltaX)} × {Um(image.DeltaY)} µm, ΔP = 2/(n − 1); the window is one period of the PSF.");
        if (poly)
            notes.Add($"Polychromatic: each wavelength referenced to the primary wavelength's chief ray (lateral colour shows as a shift), sampled on the {pupils[shortest].Wavelength * 1e6:g6} µm grid, weighted by its weight × (λ_min/λ)² over its own unaberrated peak (the help's Huygens \"wFactor\" without the configuration weight).");
        notes.Add("Strehl ratio: the PSF's peak over the unaberrated PSF's peak (the same pupil amplitude with no OPD), the peak found by refining the largest sample (help, \"Strehl Ratio\": not useful below about 0.1 or when the peak is ambiguous).");
        notes.Add("Not built: polarization, the image rotation setting, surface plots, intermediate surfaces, OpticStudio's pupil-grid stretching (the default image delta here is the unstretched one).");

        var pairs = new List<(string, string)>
        {
            ("Strehl ratio", StudyReport.Number(peak.Value, "g6")),
            ("Peak position (µm)", $"{Um(peak.X)}, {Um(peak.Y)}"),
            ("Image delta (µm)", $"{Um(image.DeltaX)} × {Um(image.DeltaY)}"),
            ("Image window (µm)", $"{Um(image.DeltaX * image.X.Count)} × {Um(image.DeltaY * image.Y.Count)}"),
            ("Working F/# from the pupil scale", $"{pupils[0].FNumberX:g5} (x), {pupils[0].FNumberY:g5} (y)"),
        };
        if (double.IsFinite(image.EnergyFraction)) pairs.Add(("Energy in the window / clear pupil", StudyReport.Number(image.EnergyFraction, "g6")));
        pairs.Add(("Pupil transmission", StudyReport.Number(pupils[0].Transmission, "g6")));
        return new PsfRun(image, peak.Value, waves, notes, warnings, pairs);
    }

    /// <summary>The Huygens PSF of one field on an image grid of m × m points (or one row or
    /// column of them).</summary>
    public static PsfRun Huygens(SequentialLens lens, OpticalSystemData system, int field, IReadOnlyList<int> waves, int pupilSampling,
        int imageSampling, double imageDelta, bool useCentroid, bool checkApertures, bool allow, PsfDirection? section, CancellationToken cancellation)
    {
        bool poly = waves.Count > 1;
        var options = Options(false, checkApertures, allow);
        int primary = system.Wavelengths.PrimaryIndex;
        // The centre: the selected (or, polychromatic, the primary) wavelength's chief ray or centroid.
        int centreWave = poly ? primary : waves[0];
        var centrePupil = PsfEngine.BuildHuygens(lens, system, field, centreWave, pupilSampling, options, null, useCentroid, cancellation);
        var pupils = new List<HuygensPupil>();
        foreach (int w in waves)
        {
            cancellation.ThrowIfCancellationRequested();
            pupils.Add(w == centreWave ? centrePupil
                : PsfEngine.BuildHuygens(lens, system, field, w, pupilSampling, options, centrePupil.Centre, false, cancellation));
        }
        double longest = pupils.Max(p => p.Wavelength);
        double delta = imageDelta > 0 ? imageDelta : PsfEngine.DefaultHuygensDelta(poly ? longest : pupils[0].Wavelength, centrePupil.FNumber, pupilSampling);
        var full = PsfEngine.Grid(imageSampling, delta, delta);
        double[] us = full.X, vs = full.Y;
        if (section == PsfDirection.X) vs = new[] { 0.0 };
        else if (section == PsfDirection.Y) us = new[] { 0.0 };
        var weights = waves.Select(w => system.Wavelengths.Wavelengths[w].Weight).ToArray();
        var lambdas = pupils.Select(p => p.Wavelength).ToArray();
        Func<double[], double[], double[,]> evaluate = poly
            ? (x, y) => PsfEngine.Combine(lambdas, weights, i => PsfEngine.Huygens(pupils[i], x, y, null, cancellation), x, y).Intensity
            : (x, y) => PsfEngine.Huygens(pupils[0], x, y, null, cancellation).Intensity;
        var image = poly
            ? PsfEngine.Combine(lambdas, weights, i => PsfEngine.Huygens(pupils[i], us, vs, null, cancellation), us, vs)
            : PsfEngine.Huygens(pupils[0], us, vs, null, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var peak = PsfEngine.RefinePeak(image, evaluate);

        var warnings = new List<string>();
        for (int i = 0; i < pupils.Count; i++)
        {
            string label = poly ? $"{pupils[i].Wavelength * 1e6:g6} µm: " : "";
            warnings.AddRange(PsfEngine.HuygensWarnings(pupils[i], full.X, full.Y).Select(x => label + x));
            warnings.AddRange(pupils[i].Warnings.Select(x => label + x));
        }
        var notes = new List<string>(centrePupil.Notes)
        {
            $"Image {imageSampling} × {imageSampling} points at {Um(delta)} µm{(imageDelta > 0 ? " (chosen)" : $", the help's default λF/√n_p with λ = {(poly ? "the longest wavelength" : "the selected wavelength")} and F = {centrePupil.FNumber:g5} from the fitted pupil scale")}; point p at (p − m/2)·Δ from the centre.",
            "Each wavelength's intensity is over its own unaberrated peak (Σ c)², every wavelet in phase at the centre.",
            "Not built: the spherical phase reference, polarization, the sum over configurations, True Color, the image rotation setting, surface plots.",
        };
        if (poly)
            notes.Add($"Polychromatic: the incoherent sum of the wavelengths, each weighted by its weight × (λ_min/λ)² (the help's \"wFactor\" without the configuration weight), centred on the primary wavelength's {(useCentroid ? "centroid" : "chief ray")}.");

        // Centroid offset of the PSF itself from the centre (first moments of the intensity).
        double sum = 0, mx = 0, my = 0;
        for (int r = 0; r < image.Y.Count; r++)
            for (int c = 0; c < image.X.Count; c++)
            {
                double v = image.Intensity[r, c];
                sum += v; mx += v * image.X[c]; my += v * image.Y[r];
            }
        var pc = centrePupil.Centre;
        var pairs = new List<(string, string)>
        {
            ("Strehl ratio", StudyReport.Number(peak.Value, "g6")),
            ("Centre (image surface frame, mm)", $"{StudyReport.Number(pc.X * 1e3, "g8")}, {StudyReport.Number(pc.Y * 1e3, "g8")}, {StudyReport.Number(pc.Z * 1e3, "g8")}"),
            ("Peak position from the centre (µm)", $"{Um(peak.X)}, {Um(peak.Y)}"),
            ("Image delta (µm)", Um(delta)),
        };
        if (section is null && sum > 0) pairs.Add(("PSF centroid offset from the centre (µm)", $"{Um(mx / sum)}, {Um(my / sum)}"));
        return new PsfRun(image, peak.Value, waves, notes, warnings, pairs);
    }

    /// <summary>The values to draw for a display type: the intensity (normalised to the peak on
    /// request), its log10 floored <paramref name="decades"/> below the peak (or below 1 when not
    /// normalised), the amplitude's real or imaginary part (scaled likewise), or its phase in
    /// degrees.</summary>
    public static double[,] Values(PsfImage image, PsfDisplayType type, bool normalize, int decades, string who)
    {
        int rows = image.Y.Count, columns = image.X.Count;
        var values = new double[rows, columns];
        double max = image.MaximumSample;
        double scale = normalize && max > 0 ? max : 1;
        if (type is PsfDisplayType.RealPart or PsfDisplayType.ImaginaryPart or PsfDisplayType.Phase && image.Amplitude is null)
            throw new InvalidOperationException($"{who}: the {type} of a polychromatic PSF means nothing; choose one wavelength.");
        double floor = Math.Pow(10, -decades);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                values[r, c] = type switch
                {
                    PsfDisplayType.Linear => image.Intensity[r, c] / scale,
                    PsfDisplayType.Logarithmic => Math.Log10(Math.Max(image.Intensity[r, c] / scale, floor)),
                    PsfDisplayType.RealPart => image.Amplitude![r, c].Real / Math.Sqrt(scale),
                    PsfDisplayType.ImaginaryPart => image.Amplitude![r, c].Imaginary / Math.Sqrt(scale),
                    _ => image.Amplitude![r, c].Phase * 180 / Math.PI,
                };
            }
        return values;
    }

    public static string Unit(PsfDisplayType type, bool normalize) => type switch
    {
        PsfDisplayType.Linear => normalize ? "relative to the peak" : "relative to the unaberrated peak",
        PsfDisplayType.Logarithmic => normalize ? "log10, relative to the peak" : "log10, relative to the unaberrated peak",
        PsfDisplayType.RealPart => "real amplitude",
        PsfDisplayType.ImaginaryPart => "imaginary amplitude",
        _ => "phase (degrees)",
    };

    /// <summary>The 0-based wavelength rows: 0 for every wavelength, else the one named.</summary>
    public static IReadOnlyList<int> Waves(int setting, OpticalSystemData system, string who)
    {
        int count = system.Wavelengths.Wavelengths.Count;
        if (setting == 0) return Enumerable.Range(0, count).ToArray();
        if (setting < 1 || setting > count)
            throw new ArgumentException($"{who}: wavelength {setting} is not one of the {count} wavelengths (0 for all).");
        return new[] { setting - 1 };
    }

    /// <summary>A display type that needs the complex amplitude, with every wavelength chosen,
    /// uses the primary wavelength (help, "FFT PSF": "the polychromatic average is not physically
    /// meaningful, and OpticStudio will set the wavelength selection to the primary wavelength").</summary>
    public static IReadOnlyList<int> ForType(IReadOnlyList<int> waves, PsfDisplayType type, OpticalSystemData system, List<string> notes)
    {
        if (waves.Count > 1 && type is PsfDisplayType.RealPart or PsfDisplayType.ImaginaryPart or PsfDisplayType.Phase)
        {
            notes.Add("The phase, real and imaginary displays use the primary wavelength when every wavelength is chosen (help, \"FFT PSF\").");
            return new[] { system.Wavelengths.PrimaryIndex };
        }
        return waves;
    }

    public static string WaveLabel(OpticalSystemData system, IReadOnlyList<int> waves) =>
        waves.Count > 1 ? "polychromatic" : $"{system.Wavelengths.Wavelengths[waves[0]].Micrometers:g6} µm";

    public static ReportSection? WarningSection(IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? null : new ReportSection("Sampling and validity warnings") { Lines = warnings };

    public static IReadOnlyList<ReportSection> Sections(ReportSection main, IReadOnlyList<string> warnings) =>
        WarningSection(warnings) is { } w ? new[] { w, main } : new[] { main };

    /// <summary>A section's samples within ±scale (meters) of the centre; all when scale is 0.</summary>
    public static (double[] X, double[] Y) Clip(IReadOnlyList<double> x, IReadOnlyList<double> y, double scale)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        for (int i = 0; i < x.Count; i++)
            if (scale <= 0 || Math.Abs(x[i]) <= scale * (1 + 1e-12)) { xs.Add(x[i] * 1e6); ys.Add(y[i]); }
        return (xs.ToArray(), ys.ToArray());
    }
}

/// <summary>
/// OpticStudio's FFT PSF (OP-38): the diffraction image of a point by the FFT of the pupil
/// function (<see cref="PsfEngine.Fft"/>), monochromatic or polychromatic, as a false-colour map
/// with the Strehl ratio and the sampling warnings.
/// </summary>
public sealed class FftPsfAnalysis : WavefrontAnalysisBase<FftPsfSettings>
{
    public const string SavedId = "FftPsf";

    public FftPsfAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "FFT PSF";
    public override string Category => "PSF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        FftPsfSettings settings, CancellationToken cancellation)
    {
        Check(settings.Sampling >= 16 && settings.Sampling <= 1024, $"{Name}: the sampling must be 16 to 1024 (it is {settings.Sampling}).");
        Check(settings.Display == 0 || settings.Display >= 8 && settings.Display <= 2 * settings.Sampling, $"{Name}: the display must be 0 or 8 to twice the sampling (it is {settings.Display}).");
        Check(settings.LogDecades >= 1 && settings.LogDecades <= 10, $"{Name}: the log decades must be 1 to 10 (it is {settings.LogDecades}).");
        Check(settings.ImageDeltaMicrometers >= 0 && double.IsFinite(settings.ImageDeltaMicrometers), $"{Name}: the image delta must be 0 or positive.");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        var extra = new List<string>();
        var waves = PsfRuns.ForType(PsfRuns.Waves(settings.Wavelength, system, Name), settings.Type, system, extra);
        var run = PsfRuns.Fft(lens, system, f, waves, settings.Sampling, settings.ImageDeltaMicrometers * 1e-6,
            settings.CheckApertures, settings.AllowOpdLimitations, Name, cancellation);
        var values = PsfRuns.Values(run.Image, settings.Type, settings.Normalize, settings.LogDecades, Name);
        int m = run.Image.X.Count;
        int d = settings.Display == 0 ? m : settings.Display;
        int start = m / 2 - d / 2;
        var shown = new double[d, d];
        for (int r = 0; r < d; r++)
            for (int c = 0; c < d; c++) shown[r, c] = values[start + r, start + c];
        var x = run.Image.X;
        var y = run.Image.Y;
        var plot = new AnalysisMapPlot($"FFT PSF, field {f + 1}, {PsfRuns.WaveLabel(system, waves)}", "x (µm)", "y (µm)",
            new GridField(shown, x[start] * 1e6, x[start + d - 1] * 1e6, y[start] * 1e6, y[start + d - 1] * 1e6), PsfRuns.Unit(settings.Type, settings.Normalize))
        { Style = settings.ShowAs };
        var assumptions = new List<string>(run.Notes);
        assumptions.AddRange(extra);
        assumptions.AddRange(run.Warnings.Select(w => "Warning: " + w));
        if (settings.Display != 0) assumptions.Add($"The central {d} × {d} of the {m} × {m} samples are drawn.");
        assumptions.Add($"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.");
        assumptions.AddRange(notes);
        var main = new ReportSection("FFT PSF") { Pairs = run.Pairs };
        return OpticsAnalysisResult.FromSections(Name, PsfRuns.Sections(main, run.Warnings), assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>OpticStudio's FFT Cross Section (OP-38): a row (X) or column (Y) of the FFT PSF.</summary>
public sealed class FftPsfCrossSectionAnalysis : WavefrontAnalysisBase<FftPsfCrossSectionSettings>
{
    public const string SavedId = "FftPsfCrossSection";

    public FftPsfCrossSectionAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "FFT PSF cross section";
    public override string Category => "PSF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        FftPsfCrossSectionSettings settings, CancellationToken cancellation)
    {
        Check(settings.Sampling >= 16 && settings.Sampling <= 1024, $"{Name}: the sampling must be 16 to 1024 (it is {settings.Sampling}).");
        Check(settings.RowColumn >= 0 && settings.RowColumn <= 2 * settings.Sampling, $"{Name}: the row/column must be 0 (centre) or 1 to {2 * settings.Sampling} (it is {settings.RowColumn}).");
        Check(settings.PlotScaleMicrometers >= 0 && double.IsFinite(settings.PlotScaleMicrometers), $"{Name}: the plot scale must be 0 or positive.");
        Check(settings.LogDecades >= 1 && settings.LogDecades <= 10, $"{Name}: the log decades must be 1 to 10 (it is {settings.LogDecades}).");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        var extra = new List<string>();
        var waves = PsfRuns.ForType(PsfRuns.Waves(settings.Wavelength, system, Name), settings.Type, system, extra);
        var run = PsfRuns.Fft(lens, system, f, waves, settings.Sampling, 0, settings.CheckApertures, settings.AllowOpdLimitations, Name, cancellation);
        var values = PsfRuns.Values(run.Image, settings.Type, settings.Normalize, settings.LogDecades, Name);
        int m = run.Image.X.Count;
        int line = settings.RowColumn == 0 ? m / 2 : settings.RowColumn - 1;
        bool rowWise = settings.Direction == PsfDirection.X;
        var coordinate = rowWise ? run.Image.X : run.Image.Y;
        var section = Enumerable.Range(0, m).Select(p => rowWise ? values[line, p] : values[p, line]).ToArray();
        var (xs, ys) = PsfRuns.Clip(coordinate, section, settings.PlotScaleMicrometers * 1e-6);
        double at = (rowWise ? run.Image.Y : run.Image.X)[line];
        var plot = new LinePlot($"FFT PSF cross section, field {f + 1}, {PsfRuns.WaveLabel(system, waves)}",
            rowWise ? "x (µm)" : "y (µm)", PsfRuns.Unit(settings.Type, settings.Normalize),
            new[] { new AnalysisCurve($"{(rowWise ? "row" : "column")} {line + 1} ({(rowWise ? "y" : "x")} = {PsfRuns.Um(at)} µm)", xs, ys) });
        var assumptions = new List<string>(run.Notes);
        assumptions.AddRange(extra);
        assumptions.AddRange(run.Warnings.Select(w => "Warning: " + w));
        assumptions.Add($"{(rowWise ? "Row" : "Column")} {line + 1} of {m}{(settings.RowColumn == 0 ? ", the centre (the reference point, not always the peak)" : "")}.");
        assumptions.AddRange(notes);
        var pairs = new List<(string, string)>(run.Pairs) { ("Section", $"{(rowWise ? "row" : "column")} {line + 1} at {(rowWise ? "y" : "x")} = {PsfRuns.Um(at)} µm") };
        var main = new ReportSection("FFT PSF cross section")
        {
            Pairs = pairs,
            TableHeaders = new[] { rowWise ? "x (µm)" : "y (µm)", PsfRuns.Unit(settings.Type, settings.Normalize) },
            TableRows = xs.Select((v, i) => (IReadOnlyList<string>)new[] { W(v), W(ys[i]) }).ToArray(),
        };
        return OpticsAnalysisResult.FromSections(Name, PsfRuns.Sections(main, run.Warnings), assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>OpticStudio's FFT Line/Edge Spread (OP-38): the FFT PSF summed across a line
/// (<see cref="PsfEngine.LineEdgeSpread"/>). Not built: "Use Coherent PSF" (the coherent
/// line image).</summary>
public sealed class FftLineEdgeSpreadAnalysis : WavefrontAnalysisBase<FftLineEdgeSpreadSettings>
{
    public const string SavedId = "FftLineEdgeSpread";

    public FftLineEdgeSpreadAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "FFT line/edge spread";
    public override string Category => "PSF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        FftLineEdgeSpreadSettings settings, CancellationToken cancellation)
    {
        Check(settings.Sampling >= 16 && settings.Sampling <= 1024, $"{Name}: the sampling must be 16 to 1024 (it is {settings.Sampling}).");
        Check(settings.PlotScaleMicrometers >= 0 && double.IsFinite(settings.PlotScaleMicrometers), $"{Name}: the plot scale must be 0 or positive.");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        var waves = PsfRuns.Waves(settings.Wavelength, system, Name);
        var run = PsfRuns.Fft(lens, system, f, waves, settings.Sampling, 0, settings.CheckApertures, settings.AllowOpdLimitations, Name, cancellation);
        bool parallelToX = settings.Direction == PsfDirection.X;
        var (coordinate, lineSpread, edgeSpread) = PsfEngine.LineEdgeSpread(run.Image, parallelToX);
        var values = settings.Spread == SpreadFunction.Line ? lineSpread : edgeSpread;
        if (settings.Logarithmic) values = values.Select(v => Math.Log10(Math.Max(v, 1e-6))).ToArray();
        var (xs, ys) = PsfRuns.Clip(coordinate, values, settings.PlotScaleMicrometers * 1e-6);
        string what = settings.Spread == SpreadFunction.Line ? "Line spread" : "Edge spread";
        string unit = (settings.Spread == SpreadFunction.Line ? "relative to its peak" : "fraction of the energy") + (settings.Logarithmic ? ", log10" : "");
        var plot = new LinePlot($"FFT {what.ToLowerInvariant()}, field {f + 1}, {PsfRuns.WaveLabel(system, waves)}",
            parallelToX ? "y (µm)" : "x (µm)", unit,
            new[] { new AnalysisCurve($"{(parallelToX ? "X" : "Y")} orientation", xs, ys) });
        var assumptions = new List<string>(run.Notes);
        assumptions.AddRange(run.Warnings.Select(w => "Warning: " + w));
        assumptions.Add($"{(parallelToX ? "X orientation: a line or edge parallel to the x axis; each row summed over x, plotted against y" : "Y orientation: a line or edge parallel to the y axis; each column summed over y, plotted against x")} (help, \"FFT Line/Edge Spread\"). The line spread is divided by its peak; the edge spread is the running sum (half the current sample) over the window's total, so 0 to 1 across it.");
        assumptions.Add("Not built: \"Use Coherent PSF\" (coherent illumination); the incoherent spread is computed.");
        assumptions.AddRange(notes);
        var main = new ReportSection(what)
        {
            Pairs = run.Pairs,
            TableHeaders = new[] { parallelToX ? "y (µm)" : "x (µm)", unit },
            TableRows = xs.Select((v, i) => (IReadOnlyList<string>)new[] { W(v), W(ys[i]) }).ToArray(),
        };
        return OpticsAnalysisResult.FromSections(Name, PsfRuns.Sections(main, run.Warnings), assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>OpticStudio's Huygens PSF (OP-38): the sum of ray wavelets on the plane tangent to the
/// image surface (<see cref="PsfEngine.Huygens"/>), with the Strehl ratio.</summary>
public sealed class HuygensPsfAnalysis : WavefrontAnalysisBase<HuygensPsfSettings>
{
    public const string SavedId = "HuygensPsf";

    public HuygensPsfAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Huygens PSF";
    public override string Category => "PSF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        HuygensPsfSettings settings, CancellationToken cancellation)
    {
        Check(settings.PupilSampling >= 8 && settings.PupilSampling <= 512, $"{Name}: the pupil sampling must be 8 to 512 (it is {settings.PupilSampling}).");
        Check(settings.ImageSampling >= 8 && settings.ImageSampling <= 512, $"{Name}: the image sampling must be 8 to 512 (it is {settings.ImageSampling}).");
        Check(settings.ImageDeltaMicrometers >= 0 && double.IsFinite(settings.ImageDeltaMicrometers), $"{Name}: the image delta must be 0 or positive.");
        Check(settings.LogDecades >= 1 && settings.LogDecades <= 10, $"{Name}: the log decades must be 1 to 10 (it is {settings.LogDecades}).");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        var extra = new List<string>();
        var waves = PsfRuns.ForType(PsfRuns.Waves(settings.Wavelength, system, Name), settings.Type, system, extra);
        var run = PsfRuns.Huygens(lens, system, f, waves, settings.PupilSampling, settings.ImageSampling, settings.ImageDeltaMicrometers * 1e-6,
            settings.UseCentroid, settings.CheckApertures, settings.AllowOpdLimitations, null, cancellation);
        var values = PsfRuns.Values(run.Image, settings.Type, settings.Normalize, settings.LogDecades, Name);
        var x = run.Image.X;
        var y = run.Image.Y;
        var plot = new AnalysisMapPlot($"Huygens PSF, field {f + 1}, {PsfRuns.WaveLabel(system, waves)}", "x (µm)", "y (µm)",
            new GridField(values, x[0] * 1e6, x[^1] * 1e6, y[0] * 1e6, y[^1] * 1e6), PsfRuns.Unit(settings.Type, settings.Normalize))
        { Style = settings.ShowAs };
        var assumptions = new List<string>(run.Notes);
        assumptions.AddRange(extra);
        assumptions.AddRange(run.Warnings.Select(w => "Warning: " + w));
        assumptions.Add($"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.");
        assumptions.AddRange(notes);
        var main = new ReportSection("Huygens PSF") { Pairs = run.Pairs };
        return OpticsAnalysisResult.FromSections(Name, PsfRuns.Sections(main, run.Warnings), assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>OpticStudio's Huygens Cross Section (OP-38): the Huygens PSF along x or y through the
/// centre (computed on that line only).</summary>
public sealed class HuygensPsfCrossSectionAnalysis : WavefrontAnalysisBase<HuygensPsfCrossSectionSettings>
{
    public const string SavedId = "HuygensPsfCrossSection";

    public HuygensPsfCrossSectionAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Huygens PSF cross section";
    public override string Category => "PSF";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        HuygensPsfCrossSectionSettings settings, CancellationToken cancellation)
    {
        Check(settings.PupilSampling >= 8 && settings.PupilSampling <= 512, $"{Name}: the pupil sampling must be 8 to 512 (it is {settings.PupilSampling}).");
        Check(settings.ImageSampling >= 8 && settings.ImageSampling <= 4096, $"{Name}: the image sampling must be 8 to 4096 (it is {settings.ImageSampling}).");
        Check(settings.ImageDeltaMicrometers >= 0 && double.IsFinite(settings.ImageDeltaMicrometers), $"{Name}: the image delta must be 0 or positive.");
        Check(settings.LogDecades >= 1 && settings.LogDecades <= 10, $"{Name}: the log decades must be 1 to 10 (it is {settings.LogDecades}).");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        var extra = new List<string>();
        var waves = PsfRuns.ForType(PsfRuns.Waves(settings.Wavelength, system, Name), settings.Type, system, extra);
        var run = PsfRuns.Huygens(lens, system, f, waves, settings.PupilSampling, settings.ImageSampling, settings.ImageDeltaMicrometers * 1e-6,
            settings.UseCentroid, settings.CheckApertures, settings.AllowOpdLimitations, settings.Direction, cancellation);
        var values = PsfRuns.Values(run.Image, settings.Type, settings.Normalize, settings.LogDecades, Name);
        bool rowWise = settings.Direction == PsfDirection.X;
        var coordinate = rowWise ? run.Image.X : run.Image.Y;
        var section = Enumerable.Range(0, coordinate.Count).Select(p => rowWise ? values[0, p] : values[p, 0]).ToArray();
        var xs = coordinate.Select(v => v * 1e6).ToArray();
        var plot = new LinePlot($"Huygens PSF cross section, field {f + 1}, {PsfRuns.WaveLabel(system, waves)}",
            rowWise ? "x (µm)" : "y (µm)", PsfRuns.Unit(settings.Type, settings.Normalize),
            new[] { new AnalysisCurve(rowWise ? "x section" : "y section", xs, section) });
        var assumptions = new List<string>(run.Notes);
        assumptions.AddRange(extra);
        assumptions.AddRange(run.Warnings.Select(w => "Warning: " + w));
        assumptions.Add("The Strehl ratio is the peak found near the largest sample of this section (the peak of the full PSF when it lies on the section).");
        assumptions.AddRange(notes);
        var main = new ReportSection("Huygens PSF cross section")
        {
            Pairs = run.Pairs,
            TableHeaders = new[] { rowWise ? "x (µm)" : "y (µm)", PsfRuns.Unit(settings.Type, settings.Normalize) },
            TableRows = xs.Select((v, i) => (IReadOnlyList<string>)new[] { W(v), W(section[i]) }).ToArray(),
        };
        return OpticsAnalysisResult.FromSections(Name, PsfRuns.Sections(main, run.Warnings), assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>
/// The Strehl ratio (OP-38; help, "Strehl Ratio") of each field and wavelength by the FFT PSF
/// (<see cref="PsfEngine.Strehl"/>), and of the polychromatic PSF, with the RMS wavefront and the
/// second-order small-aberration estimate 1 − (2πσ)² (σ the amplitude-weighted RMS about the
/// weighted mean, waves; derived from |⟨e^{iφ}⟩|² = 1 − var φ + O(φ⁴), see the OP-38 gate).
/// </summary>
public sealed class StrehlRatioAnalysis : WavefrontAnalysisBase<StrehlRatioSettings>
{
    public const string SavedId = "StrehlRatio";

    public StrehlRatioAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Strehl ratio";
    public override string Category => "PSF";

    /// <summary>σ: the RMS of W about its mean, each node weighted by its amplitude, waves.</summary>
    public static double WeightedRms(PupilFunction pupil)
    {
        ArgumentNullException.ThrowIfNull(pupil);
        double s = 0, s1 = 0, s2 = 0;
        int n = pupil.Sampling;
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double a = pupil.Amplitude[i, k];
                if (a == 0) continue;
                double w = pupil.Waves[i, k];
                s += a; s1 += a * w; s2 += a * w * w;
            }
        double mean = s1 / s;
        return Math.Sqrt(Math.Max(0, s2 / s - mean * mean));
    }

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        StrehlRatioSettings settings, CancellationToken cancellation)
    {
        Check(settings.Sampling >= 16 && settings.Sampling <= 1024, $"{Name}: the sampling must be 16 to 1024 (it is {settings.Sampling}).");
        var fields = Rows(settings.Field, system.Fields.Points.Count, true, "field");
        var waves = PsfRuns.Waves(settings.Wavelength, system, Name);
        bool poly = waves.Count > 1;
        var options = PsfRuns.Options(poly, settings.CheckApertures, settings.AllowOpdLimitations);
        var rows = new List<IReadOnlyList<string>>();
        var curves = waves.Select(w => (Name: $"{system.Wavelengths.Wavelengths[w].Micrometers:g6} µm", X: new List<double>(), Y: new List<double>())).ToList();
        var polyCurve = (X: new List<double>(), Y: new List<double>());
        var warnings = new List<string>();
        foreach (int f in fields)
        {
            var pupils = new List<PupilFunction>();
            for (int j = 0; j < waves.Count; j++)
            {
                cancellation.ThrowIfCancellationRequested();
                var p = PsfEngine.BuildPupil(lens, system, f, waves[j], settings.Sampling, options, cancellation);
                pupils.Add(p);
                double strehl = PsfEngine.Strehl(p, PsfEngine.DefaultPadding, null, cancellation);
                double sigma = WeightedRms(p);
                double estimate = 1 - Math.Pow(2 * Math.PI * sigma, 2);
                rows.Add(new[] { $"{f + 1}", curves[j].Name, W(strehl), W(sigma), W(estimate) });
                curves[j].X.Add(f + 1); curves[j].Y.Add(strehl);
                string label = $"Field {f + 1}, {curves[j].Name}: ";
                warnings.AddRange(PsfEngine.CheckSampling(p).Warnings.Select(x => label + x));
                warnings.AddRange(p.Warnings.Select(x => label + x));
                if (strehl < 0.1) warnings.Add(label + $"Strehl ratio {strehl:g3} is below about 0.1, where the help says the ratio \"is not useful\" (help, \"Strehl Ratio\").");
            }
            if (poly)
            {
                var weights = waves.Select(w => system.Wavelengths.Wavelengths[w].Weight).ToArray();
                double strehl = PsfRuns.PolychromaticStrehl(pupils, weights, null, cancellation);
                rows.Add(new[] { $"{f + 1}", "polychromatic", W(strehl), "", "" });
                polyCurve.X.Add(f + 1); polyCurve.Y.Add(strehl);
            }
        }
        var plotCurves = curves.Select(c => new AnalysisCurve(c.Name, c.X, c.Y)).ToList();
        if (poly) plotCurves.Add(new AnalysisCurve("polychromatic", polyCurve.X, polyCurve.Y, Dashed: true));
        var plot = new LinePlot("Strehl ratio against field", "Field number", "Strehl ratio", plotCurves);
        var assumptions = new List<string>
        {
            "Strehl ratio: the FFT PSF's peak (refined off the grid) over the unaberrated PSF's peak, the same pupil amplitude with no OPD (help, \"Strehl Ratio\").",
            $"Pupil {settings.Sampling} × {settings.Sampling} nodes, zero-padded to {2 * settings.Sampling} × {2 * settings.Sampling}; OPD against the exit-pupil reference sphere centred on {(poly ? "the primary wavelength's" : "the")} chief ray.",
            "σ: the RMS wavefront about its mean, each pupil node weighted by its amplitude. 1 − (2πσ)² is the second-order small-aberration estimate, derived from |⟨e^{iφ}⟩|² = 1 − var φ + O(φ⁴); it holds only while 2πσ is small.",
            poly ? "Polychromatic: each wavelength weighted by its weight × (λ_min/λ)² over its own unaberrated peak." : "One wavelength.",
        };
        assumptions.AddRange(warnings.Select(w => "Warning: " + w));
        assumptions.Add($"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.");
        assumptions.AddRange(notes);
        var main = new ReportSection("Strehl ratio")
        {
            TableHeaders = new[] { "Field", "Wavelength", "Strehl ratio", "σ (waves)", "1 − (2πσ)²" }, TableRows = rows,
        };
        return OpticsAnalysisResult.FromSections(Name, PsfRuns.Sections(main, warnings), assumptions, new AnalysisPlot[] { plot });
    }
}
