// OP-88
using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis.ImageSimulation;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.Photometry;

namespace OpenSim.Optics.Merit;

/// <summary>The criteria of the non-sequential Optimization Wizard (help, "Optimization Wizard
/// (optimize tab, non-sequential)", Criteria Settings), in the help's order.</summary>
public enum NscWizardCriterion
{
    RmsSpotRadius,
    RmsSpotX,
    RmsSpotY,
    RmsAngularRadius,
    RmsAngularX,
    RmsAngularY,
    SpatialUniformity,
    AngularUniformity,
    TotalFlux,
    CentroidX,
    CentroidY,
    AngularXCentroid,
    AngularYCentroid,
}

/// <summary>How a criterion is held to its target: "equal to, greater than, or less than".</summary>
public enum NscWizardComparison
{
    Equal,
    GreaterThan,
    LessThan,
}

/// <summary>The settings both non-sequential wizards share (help: Clear Data, Start At and
/// Raytrace Settings, identical for the two wizards).</summary>
public abstract record NscWizardCommonOptions
{
    /// <summary>"Clear Detectors": 0 (All) or a detector object to clear as well. A row clearing
    /// every detector is always written ("The Optimization Wizard will add an operand to clear all
    /// detectors regardless of the clear detectors setting").</summary>
    public int ClearDetector { get; init; }

    /// <summary>Write the clearing and NSTR rows. The help's dialog turns these settings off after
    /// the first Apply, "to prevent redundant definitions of these operands".</summary>
    public bool IncludeClearAndTrace { get; init; } = true;

    /// <summary>"Start At": the row the generated rows begin at (1-based); 0 or past the end appends.</summary>
    public int StartAt { get; init; }

    /// <summary>"Overwrite": delete every row from Start At on before writing.</summary>
    public bool Overwrite { get; init; }

    public double OverallWeight { get; init; } = 1;

    /// <summary>"Configuration": 0 for All, else one configuration.</summary>
    public int Configuration { get; init; }

    /// <summary>"Source": 0 for every source.</summary>
    public int Source { get; init; }

    public bool SplitRays { get; init; }
    public bool ScatterRays { get; init; }
    public bool UsePolarization { get; init; }
    public bool IgnoreErrors { get; init; }

    /// <summary>"Use LightningTrace" (OP-85): the trace row is NSLT (Ray Samp 0, Edge Samp 0, RT? 0) instead of NSTR.</summary>
    public bool UseLightningTrace { get; init; }
}

/// <summary>The non-sequential Optimization Wizard's settings.</summary>
public sealed record NscOptimizationWizardOptions : NscWizardCommonOptions
{
    /// <summary>The detector object the criterion reads.</summary>
    public int Detector { get; init; }

    public NscWizardCriterion Criterion { get; init; } = NscWizardCriterion.TotalFlux;

    public NscWizardComparison Comparison { get; init; } = NscWizardComparison.Equal;

    public double Target { get; init; }

    /// <summary>"Minimum flux target": a lower bound on the detector's total flux; 0 leaves it out
    /// ("ignored if it is left at the default value of zero").</summary>
    public double MinimumFlux { get; init; }
}

/// <summary>The non-sequential Bitmap Wizard's settings.</summary>
public sealed record NscBitmapWizardOptions : NscWizardCommonOptions
{
    /// <summary>A Detector Rectangle or Detector Color object.</summary>
    public int Detector { get; init; }

    /// <summary>"Total Flux": the bitmap gives the relative distribution, this the total.</summary>
    public double TotalFlux { get; init; }

    /// <summary>"Color Targets" (Detector Color only).</summary>
    public bool ColorTargets { get; init; }

    /// <summary>"Resample Detector": the detector takes the image's pixel counts instead of the
    /// image being scaled to the detector.</summary>
    public bool ResampleDetector { get; init; }
}

/// <summary>Rows a wizard wrote, the merit function with them in place, and notes.</summary>
public sealed record NscWizardResult
{
    public required IReadOnlyList<MeritOperandRow> Rows { get; init; }

    /// <summary>The merit function with <see cref="Rows"/> inserted (or written over from Start At).</summary>
    public required MeritFunction Function { get; init; }

    /// <summary>The row after the last one written: the next Apply's Start At.</summary>
    public required int NextStartAt { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Bitmap Wizard with Resample Detector: the detector's new pixel counts (X, Y),
    /// to write into its parameters 3 and 4; null otherwise.</summary>
    public (int X, int Y)? DetectorPixels { get; init; }

    /// <summary>Bitmap Wizard: the targets as the detector sees them, [iy, ix] with iy = 0 the
    /// detector's −y edge (the preview).</summary>
    public double[,]? Preview { get; init; }
}

/// <summary>
/// The non-sequential Optimization Wizard and Bitmap Wizard (plan item OP-88;
/// docs/Optics-Conventions.md §76; help, "Optimization Wizard (optimize tab, non-sequential)",
/// "Bitmap Wizard (optimize tab, non-sequential)" and "High Resolution Images and Detectors",
/// saved under plans/sources/optics/op-88-help/). Each writes the rows the help describes: a row
/// clearing every detector (NSDD Det# 0), an NSTR with the raytrace settings, then the
/// criterion's rows, for each configuration chosen (a CONF row before each when the system has
/// several). Which detector operand and code each criterion becomes is this program's reading
/// (Conventions §10 item 72), listed in <see cref="CriterionRows"/> and <see cref="Bitmap"/>.
/// </summary>
public static class NscMeritWizards
{
    // ================================================================ the Optimization Wizard

    /// <summary>The criteria the help offers for a detector type: "Only supported criteria types
    /// will be listed ... For the widest range of available criteria use the detector rectangle."
    /// A Detector Color offers Total Flux (NSDE has no centroid or spread); a Detector Polar
    /// Total Flux and RMS Angular Radius (NSDP's −4).</summary>
    public static IReadOnlyList<NscWizardCriterion> CriteriaFor(string detectorType) => detectorType switch
    {
        NscDetectorTypes.Rectangle => Enum.GetValues<NscWizardCriterion>(),
        NscDetectorTypes.Color => new[] { NscWizardCriterion.TotalFlux },
        NscDetectorTypes.Polar => new[] { NscWizardCriterion.TotalFlux, NscWizardCriterion.RmsAngularRadius },
        _ => Array.Empty<NscWizardCriterion>(),
    };

    /// <summary>The help's name of a criterion ("RMS Spot Radius").</summary>
    public static string Name(NscWizardCriterion c) => c switch
    {
        NscWizardCriterion.RmsSpotRadius => "RMS Spot Radius",
        NscWizardCriterion.RmsSpotX => "RMS Spot X",
        NscWizardCriterion.RmsSpotY => "RMS Spot Y",
        NscWizardCriterion.RmsAngularRadius => "RMS Angular Radius",
        NscWizardCriterion.RmsAngularX => "RMS Angular X",
        NscWizardCriterion.RmsAngularY => "RMS Angular Y",
        NscWizardCriterion.SpatialUniformity => "Spatial Uniformity",
        NscWizardCriterion.AngularUniformity => "Angular Uniformity",
        NscWizardCriterion.TotalFlux => "Total Flux",
        NscWizardCriterion.CentroidX => "Centroid X",
        NscWizardCriterion.CentroidY => "Centroid Y",
        NscWizardCriterion.AngularXCentroid => "Angular X Centroid",
        _ => "Angular Y Centroid",
    };

    /// <summary>
    /// The rows of one criterion on detector <paramref name="detector"/> of type
    /// <paramref name="detectorType"/>, with the target held by <paramref name="comparison"/>,
    /// numbered from <paramref name="firstRow"/>. A Detector Rectangle's criteria read NSDD:
    /// RMS spot radius, x, y Pix# −9, −10, −11 with Data 0 (flux-weighted, lens units); the
    /// angular ones the same with Data 2 (degrees); centroids −6, −7 with Data 0 or 2; total flux
    /// Pix# 0, Data 0; uniformity the standard deviation over the mean of the non-zero pixels,
    /// σ/μ (Pix# −4 over −5) of flux/area (Data 1, spatial) or flux/solid angle (Data 2, angular),
    /// 0 for a perfectly uniform detector. Detector Color: total power NSDE Pix# 0, Data 1.
    /// Detector Polar: total power NSDP Pix# 0, Data 1; RMS angular radius NSDP −4, Data 1.
    /// Equal puts the target on the value row; greater or less than gives the value row weight 0
    /// and an OPGT or OPLT row on it with the target and weight.
    /// </summary>
    public static IReadOnlyList<MeritOperandRow> CriterionRows(int detector, string detectorType, NscWizardCriterion criterion,
        NscWizardComparison comparison, double target, double weight, int firstRow)
    {
        if (!CriteriaFor(detectorType).Contains(criterion))
            throw new ArgumentException($"Optimization Wizard: the criterion {Name(criterion)} is not offered for a {detectorType}; it offers {string.Join(", ", CriteriaFor(detectorType).Select(Name))}.");
        var rows = new List<MeritOperandRow>();
        string comment = Name(criterion);
        MeritOperandRow Nsdd(int pixel, int data, double t, double w, string c) => new()
        {
            Code = "NSDD", Int1 = 1, Int2 = detector, Data = new double[] { pixel, data }, Target = t, Weight = w, Comment = c,
        };
        void Held(MeritOperandRow value)
        {
            if (comparison == NscWizardComparison.Equal) { rows.Add(value with { Target = target, Weight = weight }); return; }
            rows.Add(value with { Target = 0, Weight = 0 });
            rows.Add(new MeritOperandRow
            {
                Code = comparison == NscWizardComparison.GreaterThan ? "OPGT" : "OPLT", Int1 = firstRow + rows.Count - 1,
                Target = target, Weight = weight, Comment = $"{comment} {(comparison == NscWizardComparison.GreaterThan ? "at least" : "at most")} the target",
            });
        }
        if (detectorType == NscDetectorTypes.Color)
        {
            Held(new MeritOperandRow { Code = "NSDE", Int1 = 1, Int2 = detector, Data = new double[] { 0, 0, 1 }, Comment = comment });
            return rows;
        }
        if (detectorType == NscDetectorTypes.Polar)
        {
            Held(new MeritOperandRow
            {
                Code = "NSDP", Int1 = 1, Int2 = detector, Data = new double[] { criterion == NscWizardCriterion.TotalFlux ? 0 : -4, 1 }, Comment = comment,
            });
            return rows;
        }
        switch (criterion)
        {
            case NscWizardCriterion.SpatialUniformity:
            case NscWizardCriterion.AngularUniformity:
            {
                int data = criterion == NscWizardCriterion.SpatialUniformity ? 1 : 2;
                rows.Add(Nsdd(-4, data, 0, 0, "Standard deviation of the non-zero pixels"));
                rows.Add(Nsdd(-5, data, 0, 0, "Mean of the non-zero pixels"));
                Held(new MeritOperandRow { Code = "DIVI", Int1 = firstRow, Int2 = firstRow + 1, Comment = $"{comment}: standard deviation / mean" });
                return rows;
            }
        }
        var (pix, code) = criterion switch
        {
            NscWizardCriterion.RmsSpotRadius => (-9, 0),
            NscWizardCriterion.RmsSpotX => (-10, 0),
            NscWizardCriterion.RmsSpotY => (-11, 0),
            NscWizardCriterion.RmsAngularRadius => (-9, 2),
            NscWizardCriterion.RmsAngularX => (-10, 2),
            NscWizardCriterion.RmsAngularY => (-11, 2),
            NscWizardCriterion.TotalFlux => (0, 0),
            NscWizardCriterion.CentroidX => (-6, 0),
            NscWizardCriterion.CentroidY => (-7, 0),
            NscWizardCriterion.AngularXCentroid => (-6, 2),
            _ => (-7, 2),
        };
        Held(Nsdd(pix, code, 0, 0, comment));
        return rows;
    }

    /// <summary>
    /// The non-sequential Optimization Wizard on <paramref name="existing"/>. Throws
    /// <see cref="ArgumentException"/> naming the setting when one is wrong (a detector that is
    /// not a detector, a criterion its type does not offer, LightningTrace).
    /// </summary>
    /// <param name="rows">The project's non-sequential objects (object 1 first).</param>
    public static NscWizardResult Optimization(MeritFunction existing, IReadOnlyList<OpticsNscObjectSettings> rows, int configurations,
        NscOptimizationWizardOptions options, MeritOperandRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(options);
        string type = DetectorType(rows, options.Detector, "Optimization Wizard");
        Common(options, rows, configurations, "Optimization Wizard");
        var notes = new List<string>();
        var block = new List<MeritOperandRow>();
        int at = Start(existing, options);
        foreach (int configuration in Configurations(options, configurations))
        {
            if (configurations > 1) block.Add(new MeritOperandRow { Code = "CONF", Int1 = configuration });
            if (options.IncludeClearAndTrace) block.AddRange(ClearAndTrace(options));
            block.AddRange(CriterionRows(options.Detector, type, options.Criterion, options.Comparison, options.Target, options.OverallWeight, at + block.Count));
            if (options.MinimumFlux != 0)
            {
                var flux = type switch
                {
                    NscDetectorTypes.Color => new MeritOperandRow { Code = "NSDE", Int1 = 1, Int2 = options.Detector, Data = new double[] { 0, 0, 1 } },
                    NscDetectorTypes.Polar => new MeritOperandRow { Code = "NSDP", Int1 = 1, Int2 = options.Detector, Data = new double[] { 0, 1 } },
                    _ => new MeritOperandRow { Code = "NSDD", Int1 = 1, Int2 = options.Detector, Data = new double[] { 0, 0 } },
                };
                block.Add(flux with { Comment = "Total flux, for the minimum flux target" });
                block.Add(new MeritOperandRow { Code = "OPGT", Int1 = at + block.Count - 1, Target = options.MinimumFlux, Weight = options.OverallWeight, Comment = "Minimum flux target" });
            }
        }
        notes.Add($"Optimization Wizard: {Name(options.Criterion)} on detector object {options.Detector} ({type}), {block.Count} rows.");
        return Finish(existing, block, at, options, registry, notes);
    }

    // ================================================================ the Bitmap Wizard

    /// <summary>
    /// The non-sequential Bitmap Wizard: <paramref name="image"/> becomes per-pixel targets on a
    /// Detector Rectangle or Detector Color. The image is scaled to the detector's pixels by area
    /// (each detector pixel takes the mean of the image area it covers; or, with Resample
    /// Detector, the detector takes the image's pixel counts) with the picture's top row at the
    /// detector's +y edge and its left column at −x. A pixel's flux is the relative luminance of
    /// its sRGB values (decoded by CSS Color 4's lin_sRGB and weighted by the Y row of its
    /// lin_sRGB_to_XYZ matrix, Photometric.XyzOfLinearSrgb), normalised to the brightest pixel.
    /// Rows (the help: "one operand per pixel if only flux is targeted and three operands per
    /// pixel if color and flux are targeted"): one total-flux row (NSDD Pix# 0, Data 0, or NSDE
    /// Pix# 0, Data 1) with the Total Flux target, then for each pixel,
    /// NSDD Data 3 (flux over the peak of these rows) for a Detector Rectangle, NSDE Data 15
    /// (tristimulus Y over the peak Y) for a Detector Color, and with Color Targets NSDE Data 14,
    /// 15 and 16 targeting the pixel's X, Y, Z over the image's peak Y. Pixels are written in the
    /// detector's numbering (pixel = ix + iy·Nx + 1).
    /// </summary>
    public static NscWizardResult Bitmap(MeritFunction existing, IReadOnlyList<OpticsNscObjectSettings> rows, int configurations,
        NscBitmapWizardOptions options, SceneImage image, MeritOperandRegistry? registry = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(image);
        string type = DetectorType(rows, options.Detector, "Bitmap Wizard");
        if (type is not (NscDetectorTypes.Rectangle or NscDetectorTypes.Color))
            throw new ArgumentException($"Bitmap Wizard: detector object {options.Detector} is a {type}; the wizard \"only supports color and rectangle detectors\" (help).");
        if (options.ColorTargets && type != NscDetectorTypes.Color)
            throw new ArgumentException("Bitmap Wizard: Color Targets need a Detector Color (\"If the detector selected is a detector color, the ability to define color targets is available. Otherwise, only flux will be targeted\").");
        if (!(options.TotalFlux > 0) || !double.IsFinite(options.TotalFlux))
            throw new ArgumentException($"Bitmap Wizard: the Total Flux must be positive (it is {options.TotalFlux.ToString("g6", CultureInfo.InvariantCulture)}).");
        Common(options, rows, configurations, "Bitmap Wizard");
        if (image.Channels.Count is not (1 or 3))
            throw new ArgumentException($"Bitmap Wizard: the image has {image.Channels.Count} maps; a grey or red, green, blue picture is needed.");
        if (!(image.FullScale > 0))
            throw new ArgumentException("Bitmap Wizard: the image has no full-scale value (an empty picture).");

        var row = rows[options.Detector - 1];
        int nx, ny;
        if (options.ResampleDetector) (nx, ny) = (image.Width, image.Height);
        else
        {
            nx = Pixels(row, 2, "# X Pixels");
            ny = Pixels(row, 3, "# Y Pixels");
        }
        var (lx, ly, lz) = Linear(image);
        var X = Scale(lx, nx, ny); var Y = Scale(ly, nx, ny); var Z = Scale(lz, nx, ny);
        double peak = 0;
        foreach (double v in Y) peak = Math.Max(peak, v);
        if (!(peak > 0)) throw new ArgumentException("Bitmap Wizard: the image is black everywhere, so it gives no relative flux to target.");

        var block = new List<MeritOperandRow>();
        int at = Start(existing, options);
        var preview = new double[ny, nx];
        for (int iy = 0; iy < ny; iy++) for (int ix = 0; ix < nx; ix++) preview[iy, ix] = Y[iy, ix] / peak;
        foreach (int configuration in Configurations(options, configurations))
        {
            if (configurations > 1) block.Add(new MeritOperandRow { Code = "CONF", Int1 = configuration });
            if (options.IncludeClearAndTrace) block.AddRange(ClearAndTrace(options));
            block.Add(type == NscDetectorTypes.Color
                ? new MeritOperandRow { Code = "NSDE", Int1 = 1, Int2 = options.Detector, Data = new double[] { 0, 0, 1 }, Target = options.TotalFlux, Weight = options.OverallWeight, Comment = "Total flux" }
                : new MeritOperandRow { Code = "NSDD", Int1 = 1, Int2 = options.Detector, Data = new double[] { 0, 0 }, Target = options.TotalFlux, Weight = options.OverallWeight, Comment = "Total flux" });
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int pixel = ix + iy * nx + 1;
                    if (type == NscDetectorTypes.Rectangle)
                        block.Add(new MeritOperandRow { Code = "NSDD", Int1 = 1, Int2 = options.Detector, Data = new double[] { pixel, 3 }, Target = Y[iy, ix] / peak, Weight = options.OverallWeight });
                    else if (!options.ColorTargets)
                        block.Add(new MeritOperandRow { Code = "NSDE", Int1 = 1, Int2 = options.Detector, Data = new double[] { pixel, 0, 15 }, Target = Y[iy, ix] / peak, Weight = options.OverallWeight });
                    else
                        foreach (var (data, value) in new[] { (14, X[iy, ix]), (15, Y[iy, ix]), (16, Z[iy, ix]) })
                            block.Add(new MeritOperandRow { Code = "NSDE", Int1 = 1, Int2 = options.Detector, Data = new double[] { pixel, 0, data }, Target = value / peak, Weight = options.OverallWeight });
                }
        }
        var notes = new List<string>
        {
            $"Bitmap Wizard: a {image.Width} × {image.Height} image on detector object {options.Detector} ({type}) of {nx} × {ny} pixels, {block.Count} rows.",
        };
        if (options.ResampleDetector) notes.Add($"Resample Detector: detector object {options.Detector}'s # X Pixels and # Y Pixels become {nx} and {ny}.");
        return Finish(existing, block, at, options, registry, notes) with
        {
            DetectorPixels = options.ResampleDetector ? (nx, ny) : null,
            Preview = preview,
        };
    }

    /// <summary>The saved detector row with its pixel counts (parameters 3 and 4) set, for Resample Detector.</summary>
    public static OpticsNscObjectSettings WithPixels(OpticsNscObjectSettings row, int nx, int ny)
    {
        ArgumentNullException.ThrowIfNull(row);
        var p = row.Parameters?.ToList() ?? new List<double>();
        var defaults = NscDetectorTypes.Descriptors.First(d => d.TypeName == NscDetectorTypes.Rectangle).Parameters;
        while (p.Count < 4) p.Add(defaults[p.Count].Default);
        p[2] = nx;
        p[3] = ny;
        return row with { Parameters = p };
    }

    /// <summary>sRGB decoding, CSS Color Module Level 4, §19 sample code, lin_sRGB: v/12.92 for
    /// |v| ≤ 0.04045, else sign(v)·((|v| + 0.055)/1.055)^2.4 (saved under
    /// plans/sources/optics/op-79-help/srgb/css-color-4.txt).</summary>
    public static double SrgbDecode(double v)
    {
        double a = Math.Abs(v);
        return a <= 0.04045 ? v / 12.92 : Math.CopySign(Math.Pow((a + 0.055) / 1.055, 2.4), v);
    }

    /// <summary>Each image pixel's linear X, Y, Z (a grey image: X = Y = Z ratio of the white,
    /// from equal channels).</summary>
    private static (double[,] X, double[,] Y, double[,] Z) Linear(SceneImage image)
    {
        int h = image.Height, w = image.Width;
        var X = new double[h, w]; var Y = new double[h, w]; var Z = new double[h, w];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                double R, G, B;
                if (image.Channels.Count == 1) R = G = B = SrgbDecode(image.Channels[0][r, c] / image.FullScale);
                else
                {
                    R = SrgbDecode(image.Channels[0][r, c] / image.FullScale);
                    G = SrgbDecode(image.Channels[1][r, c] / image.FullScale);
                    B = SrgbDecode(image.Channels[2][r, c] / image.FullScale);
                }
                (X[r, c], Y[r, c], Z[r, c]) = Photometric.XyzOfLinearSrgb(R, G, B);
            }
        return (X, Y, Z);
    }

    /// <summary>The image map [row, column] (row 0 the top) averaged by area onto nx × ny detector
    /// pixels [iy, ix] (iy 0 the −y edge, so the image's top row is the detector's last).</summary>
    internal static double[,] Scale(double[,] map, int nx, int ny)
    {
        int h = map.GetLength(0), w = map.GetLength(1);
        var result = new double[ny, nx];
        for (int iy = 0; iy < ny; iy++)
        {
            int top = ny - 1 - iy;   // detector row iy covers image rows [top·h/ny, (top+1)·h/ny)
            double r0 = (double)top * h / ny, r1 = (double)(top + 1) * h / ny;
            for (int ix = 0; ix < nx; ix++)
            {
                double c0 = (double)ix * w / nx, c1 = (double)(ix + 1) * w / nx;
                double sum = 0, area = 0;
                for (int r = (int)Math.Floor(r0); r < Math.Min(h, (int)Math.Ceiling(r1)); r++)
                {
                    double dr = Math.Min(r + 1, r1) - Math.Max(r, r0);
                    if (dr <= 0) continue;
                    for (int c = (int)Math.Floor(c0); c < Math.Min(w, (int)Math.Ceiling(c1)); c++)
                    {
                        double dc = Math.Min(c + 1, c1) - Math.Max(c, c0);
                        if (dc <= 0) continue;
                        sum += map[r, c] * dr * dc;
                        area += dr * dc;
                    }
                }
                result[iy, ix] = sum / area;
            }
        }
        return result;
    }

    // ================================================================ shared

    private static int Pixels(OpticsNscObjectSettings row, int index, string name)
    {
        double v = row.Parameters is { } p && index < p.Count ? p[index]
            : NscDetectorTypes.Descriptors.First(d => d.TypeName == NscDetectorTypes.Rectangle).Parameters[index].Default;
        if (v < 1 || v != Math.Floor(v)) throw new ArgumentException($"Bitmap Wizard: the detector's {name} is {v}; it must be a whole number of at least 1.");
        return (int)v;
    }

    private static string DetectorType(IReadOnlyList<OpticsNscObjectSettings> rows, int detector, string who)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (detector < 1 || detector > rows.Count)
            throw new ArgumentException(rows.Count == 0 ? $"{who}: the system has no non-sequential objects." : $"{who}: detector object {detector} is not an object; there are objects 1 to {rows.Count}.");
        string type = (rows[detector - 1]?.Type ?? "").Trim();
        if (!NscDetectorTypes.IsDetectorType(type) || NscDetectorTypes.Descriptors.FirstOrDefault(d => d.TypeName == type) is not { IsBuilt: true })
            throw new ArgumentException($"{who}: object {detector} is a {(type.Length == 0 ? "row with no type" : type)}, not a Detector Rectangle, Color or Polar.");
        return type;
    }

    private static void Common(NscWizardCommonOptions o, IReadOnlyList<OpticsNscObjectSettings> rows, int configurations, string who)
    {
        // OP-85: LightningTrace writes an NSLT row; its mesh rays neither split nor scatter.
        if (o.UseLightningTrace && (o.SplitRays || o.ScatterRays))
            throw new ArgumentException($"{who}: LightningTrace mesh rays neither split nor scatter (help, \"Lightning Trace\"); turn Split Rays and Scatter Rays off, or use conventional raytracing.");
        if (!(o.OverallWeight >= 0) || !double.IsFinite(o.OverallWeight)) throw new ArgumentException($"{who}: the overall weight must be zero or positive.");
        if (o.Configuration < 0 || o.Configuration > configurations) throw new ArgumentException($"{who}: configuration {o.Configuration} is not one of the {configurations} (0 for All).");
        if (o.Source < 0 || o.Source > rows.Count) throw new ArgumentException($"{who}: source {o.Source} is not an object (0 for every source).");
        if (o.Source > 0 && NonSequential.Sources.NscSourceTypes.Find(rows[o.Source - 1]?.Type) is null)
            throw new ArgumentException($"{who}: object {o.Source} ({rows[o.Source - 1]?.Type ?? "no type"}) is not a source.");
        if (o.ClearDetector < 0 || o.ClearDetector > rows.Count) throw new ArgumentException($"{who}: Clear Detectors {o.ClearDetector} is not an object (0 for All).");
    }

    private static IEnumerable<int> Configurations(NscWizardCommonOptions o, int count) =>
        o.Configuration == 0 ? Enumerable.Range(1, count) : new[] { o.Configuration };

    private static IEnumerable<MeritOperandRow> ClearAndTrace(NscWizardCommonOptions o)
    {
        yield return new MeritOperandRow { Code = "NSDD", Int1 = 1, Int2 = 0, Comment = "Clear every detector" };
        if (o.ClearDetector > 0) yield return new MeritOperandRow { Code = "NSDD", Int1 = 1, Int2 = -o.ClearDetector, Comment = $"Clear detector {o.ClearDetector}" };
        if (o.UseLightningTrace)   // OP-85
        {
            yield return new MeritOperandRow
            {
                Code = "NSLT", Int1 = 1, Int2 = o.Source, Comment = o.Source == 0 ? "LightningTrace every source" : $"LightningTrace source {o.Source}",
                Data = new double[] { 0, 0, 0 },
            };
            yield break;
        }
        yield return new MeritOperandRow
        {
            Code = "NSTR", Int1 = 1, Int2 = o.Source, Comment = o.Source == 0 ? "Trace every source" : $"Trace source {o.Source}",
            Data = new double[] { o.SplitRays ? 1 : 0, o.ScatterRays ? 1 : 0, o.UsePolarization ? 1 : 0, o.IgnoreErrors ? 1 : 0 },   // wire-16: Scat?
        };
    }

    /// <summary>The first generated row's number.</summary>
    private static int Start(MeritFunction existing, NscWizardCommonOptions o)
    {
        int count = existing.Rows.Count;
        return o.StartAt < 1 || o.StartAt > count ? count + 1 : o.StartAt;
    }

    private static NscWizardResult Finish(MeritFunction existing, List<MeritOperandRow> block, int at, NscWizardCommonOptions o,
        MeritOperandRegistry? registry, List<string> notes)
    {
        registry ??= MeritOperandRegistry.CreateDefault();
        var basis = o.Overwrite && at <= existing.Rows.Count ? new MeritFunction(existing.Rows.Take(at - 1)) : existing;
        var function = DefaultMeritWizard.Insert(basis, block, at, registry);
        return new NscWizardResult { Rows = block, Function = function, NextStartAt = at + block.Count, Notes = notes };
    }
}
