using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Optics.Analysis.ImageSimulation;

/// <summary>Which wavelengths the bitmap analyses use (help, "Image Simulation" and "Geometric
/// Bitmap Image Analysis", "Wavelength": "RGB", "1+2+3", or one wavelength).</summary>
public enum SceneWavelengths
{
    /// <summary>Three wavelengths of the help's own, whatever the system's.</summary>
    Rgb = 0,
    /// <summary>The system's wavelengths 1, 2 and 3: blue, green and red.</summary>
    Wavelengths123 = 1,
    /// <summary>One system wavelength (the setting "Wavelength number").</summary>
    Single = 2,
}

/// <summary>How a diffraction image is drawn.</summary>
public enum DiffractionImageShow
{
    /// <summary>A grey-scale or false-colour map (the window picks the colour scale).</summary>
    Map = 0,
    Contour = 1,
    CrossSectionX = 2,
    CrossSectionY = 3,
}

/// <summary>What the extended-scene analyses share: the lens preparation, colour channels,
/// map plots and cross-sections.</summary>
internal static class ExtendedSceneSupport
{
    public const string Category = "Extended scene";

    /// <summary>The help's RGB wavelengths of the Image Simulation ("0.606, 0.535, and 0.465
    /// micrometers for red green, and blue"), µm.</summary>
    public static readonly double[] ImageSimulationRgb = { 0.606, 0.535, 0.465 };

    /// <summary>The help's RGB wavelengths of the Geometric Bitmap Image Analysis ("0.656,
    /// 0.587. and 0.486 micrometers for red green, and blue"), µm.</summary>
    public static readonly double[] GeometricBitmapRgb = { 0.656, 0.587, 0.486 };

    /// <summary>The lens and system ready to trace: afocal refused, vignetting factors replaced
    /// when asked, floating apertures frozen (OP-40's preparation); real image height read as
    /// paraxial image height when the help says so; and, with <paramref name="fixedApertures"/>,
    /// a circular aperture at the semi-diameter on every powered surface that has none (help,
    /// "Apply Fixed Apertures"; 1e-9 relative is added so a marginal ray of a defined field is not
    /// stopped by rounding).</summary>
    public static RmsSetupView Prepare(SequentialLens lens, OpticalSystemData system, bool removeVignetting, bool fixedApertures,
        bool realToParaxial, string who, List<string> notes, CancellationToken cancellation)
    {
        if (realToParaxial && system.Fields.Type == FieldType.RealImageHeight)
        {
            system = system with { Fields = system.Fields with { Type = FieldType.ParaxialImageHeight } };
            notes.Add("Real image height fields are read as paraxial image heights (help: \"the field type is automatically changed to Paraxial Image Height for this analysis\").");
        }
        var setup = RmsCore.Prepare(lens, system, removeVignetting, who, notes, cancellation);
        lens = setup.Lens;
        system = setup.System;
        if (fixedApertures)
        {
            var clear = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
            int changed = 0;
            var surfaces = lens.Surfaces.Select((s, j) =>
            {
                if (j == 0 || j == lens.ImageIndex || s.Aperture is not null) return s;
                bool powered = s.Curvature != 0 || s.IsMirror || (s.Shape is ParaxialShape p && p.Power != 0) || (s.Shape is not null && s.Shape is not ParaxialShape);
                if (!powered) return s;
                double sd = s.SemiDiameter ?? clear[j];
                if (!(sd > 0) || !double.IsFinite(sd)) return s;
                changed++;
                return s with { Aperture = new CircularAperture(0, sd * (1 + 1e-9)) };
            }).ToArray();
            if (changed > 0)
            {
                lens = lens with { Surfaces = surfaces };
                notes.Add($"Apply fixed apertures: {changed} powered surface(s) without an aperture get a circular one at the semi-diameter (help).");
            }
        }
        return new RmsSetupView(lens, system, setup.ReplacedVignetting);
    }

    public sealed record RmsSetupView(SequentialLens Lens, OpticalSystemData System, bool ReplacedVignetting);

    /// <summary>A field length setting (degrees, or lens units for heights) in the trace's units.</summary>
    public static double FieldValue(double setting, OpticalSystemData system) =>
        FieldTypes.IsAngle(system.Fields.Type) ? setting : OpticsConventions.ToMeters(setting, system.Units.LensUnit);

    public static string FieldUnit(OpticalSystemData system) =>
        FieldTypes.IsAngle(system.Fields.Type) ? "°" : " " + Paraxial.ParaxialReport.UnitName(system.Units.LensUnit);

    public static string LensUnit(OpticalSystemData system) => Paraxial.ParaxialReport.UnitName(system.Units.LensUnit);

    public static double Shown(double meters, OpticalSystemData system) => OpticsConventions.FromMeters(meters, system.Units.LensUnit);

    /// <summary>The system with extra wavelengths appended (weight 1), and their rows.</summary>
    public static (OpticalSystemData System, int[] Rows) WithWavelengths(OpticalSystemData system, IReadOnlyList<double> micrometers)
    {
        var list = system.Wavelengths.Wavelengths.ToList();
        var rows = new int[micrometers.Count];
        for (int i = 0; i < micrometers.Count; i++)
        {
            rows[i] = list.Count;
            list.Add(new Wavelength { Micrometers = micrometers[i], Weight = 1 });
        }
        return (system with { Wavelengths = system.Wavelengths with { Wavelengths = list.ToArray() } }, rows);
    }

    /// <summary>The bitmap channel the help gives a single wavelength k (1-based): blue for
    /// 1, 4, 7 …, green for 2, 5, 8 …, red for 3, 6, 9 … (help, "Image Simulation"); or, with
    /// <paramref name="redAboveThree"/>, red for every k above 3 (help, "Geometric Bitmap Image
    /// Analysis"). 0 red, 1 green, 2 blue.</summary>
    public static int ChannelOf(int k, bool redAboveThree)
    {
        if (redAboveThree && k > 3) return 0;
        return (k % 3) switch { 1 => 2, 2 => 1, _ => 0 };
    }

    public static readonly string[] ColourNames = { "red", "green", "blue" };

    /// <summary>An image [row, column] (row 0 the top) as a map plot centred on (cx, cy) with
    /// pixel pitch (px, py), all in the axis unit.</summary>
    public static AnalysisMapPlot Map(string title, double[,] image, double cx, double cy, double px, double py, string xLabel, string yLabel,
        string unit, AnalysisMapStyle style = AnalysisMapStyle.Image, double maximum = double.NaN)
    {
        int ny = image.GetLength(0), nx = image.GetLength(1);
        // GridField needs 2 × 2 samples; a single row or column is repeated.
        int gy = Math.Max(2, ny), gx = Math.Max(2, nx);
        var values = new double[gy, gx];
        for (int r = 0; r < gy; r++)
            for (int c = 0; c < gx; c++) values[r, c] = image[ny - 1 - Math.Min(r, ny - 1), Math.Min(c, nx - 1)];
        double x0 = cx - (nx - 1) / 2.0 * px, y0 = cy - (ny - 1) / 2.0 * py;
        double x1 = nx > 1 ? x0 + (nx - 1) * px : x0 + px, y1 = ny > 1 ? y0 + (ny - 1) * py : y0 + py;
        return new AnalysisMapPlot(title, xLabel, yLabel, new GridField(values, x0, x1, y0, y1), unit) { Style = style, Maximum = maximum };
    }

    /// <summary>The middle row (X) or column (Y) of an image as a curve, coordinates in the axis unit.</summary>
    public static (double[] X, double[] Y) CrossSection(double[,] image, double cx, double cy, double px, double py, bool alongX)
    {
        int ny = image.GetLength(0), nx = image.GetLength(1);
        if (alongX)
        {
            int r = ny / 2;
            return (Enumerable.Range(0, nx).Select(c => cx + (c - (nx - 1) / 2.0) * px).ToArray(), Enumerable.Range(0, nx).Select(c => image[r, c]).ToArray());
        }
        int col = nx / 2;
        // Bottom to top so y increases.
        return (Enumerable.Range(0, ny).Select(i => cy + (i - (ny - 1) / 2.0) * py).ToArray(), Enumerable.Range(0, ny).Select(i => image[ny - 1 - i, col]).ToArray());
    }

    /// <summary>
    /// The help's MTF estimate from a cross-section (help, "Partially Coherent Image Analysis",
    /// "Computing MTF using Cross Section X/Y"): the largest and smallest values at all points
    /// between the second and the second-to-last local peaks, (Imax − Imin)/(Imax + Imin); NaN
    /// when fewer than four peaks are found. A peak is a sample larger than both neighbours (a
    /// flat top counts once, at its first sample).
    /// </summary>
    public static double MtfEstimate(IReadOnlyList<double> y)
    {
        var peaks = new List<int>();
        for (int i = 1; i < y.Count - 1; i++)
        {
            if (!(y[i] > y[i - 1])) continue;
            int j = i;
            while (j + 1 < y.Count && y[j + 1] == y[i]) j++;
            if (j + 1 < y.Count && y[j + 1] < y[i]) peaks.Add(i);
            i = j;
        }
        if (peaks.Count < 4) return double.NaN;
        double max = double.NegativeInfinity, min = double.PositiveInfinity;
        for (int i = peaks[1]; i <= peaks[^2]; i++) { max = Math.Max(max, y[i]); min = Math.Min(min, y[i]); }
        return max + min > 0 ? (max - min) / (max + min) : double.NaN;
    }

    /// <summary>
    /// An image resampled by a detector of nx × ny pixels of size (dx, dy) whose centre is
    /// decentred by (ox·dx, oy·dy) from the image centre (help, "Resample Image"): each detector
    /// pixel is the mean of the image over its area, s × s points bilinear between image samples
    /// (s = ⌈Δ/d⌉, at most 16), zero outside the image.
    /// </summary>
    public static double[,] Resample(double[,] image, double d, int nx, int ny, double dx, double dy, double ox, double oy)
    {
        int h = image.GetLength(0), w = image.GetLength(1);
        int sx = (int)Math.Clamp(Math.Ceiling(dx / d - 1e-9), 1, 16), sy = (int)Math.Clamp(Math.Ceiling(dy / d - 1e-9), 1, 16);
        var o = new double[ny, nx];
        for (int i = 0; i < ny; i++)
            for (int j = 0; j < nx; j++)
            {
                double sum = 0;
                for (int u = 0; u < sy; u++)
                    for (int v = 0; v < sx; v++)
                    {
                        double x = (j - (nx - 1) / 2.0 + ox + (v + 0.5) / sx - 0.5) * dx;
                        double y = ((ny - 1) / 2.0 - i + oy - (u + 0.5) / sy + 0.5) * dy;
                        // Image sample (r, c) at x = (c − (w − 1)/2)·d, y = ((h − 1)/2 − r)·d.
                        sum += ImageSimulator.Sample(image, x / d + (w - 1) / 2.0, (h - 1) / 2.0 - y / d);
                    }
                o[i, j] = sum / (sx * sy);
            }
        return o;
    }

    public static string N(double v, string format = "g6") => StudyReport.Number(v, format);
}
