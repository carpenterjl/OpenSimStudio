using System.ComponentModel;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;

namespace OpenSim.Optics.NonSequential.Scattering;

/// <summary>The model inputs the two scatter plots share (help, "Scatter Function Viewer",
/// "Scatter Polar Plot").</summary>
public interface INscScatterPlotSettings
{
    NscScatterModel Model { get; }
    int Wavelength { get; }
    double Fraction { get; }
    double SigmaOrAngle { get; }
    string FileName { get; }
}

/// <summary>Settings of the <see cref="NscScatterFunctionViewerAnalysis"/>.</summary>
public sealed record NscScatterFunctionViewerSettings : INscScatterPlotSettings
{
    [DisplayName("Scatter model")][Description("Lambertian, Gaussian, ABg, ABg File or BSDF (User Defined is not built).")]
    public NscScatterModel Model { get; init; } = NscScatterModel.Lambertian;

    [DisplayName("Wavelength")][Description("Row of the wavelength table, 1 for the first; 0 for the primary. Scales ABg data that have a reference wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Fraction")][Description("Fraction to scatter, 0 to 1; ignored for ABg and ABg File; the help says to use 1 for BSDF.")]
    public double Fraction { get; init; } = 1;

    [DisplayName("Sigma/Angle")][Description("Gaussian σ (0 to 5); for BSDF the rotation of the surface relative to the measured sample (degrees).")]
    public double SigmaOrAngle { get; init; } = 0.1;

    [DisplayName("File name")][Description("ABg: the profile's name in the project's ABg catalog; ABg File: full path of the .ABGF file; BSDF: full path of the .BSDF file.")]
    public string FileName { get; init; } = "";

    [DisplayName("Bins")][Description("Histogram bins of |x| (BSDF model only, which is plotted from scattered rays).")]
    public int Bins { get; init; } = 50;

    [DisplayName("Rays")][Description("Rays scattered to build the BSDF model's curve.")]
    public int Rays { get; init; } = 100_000;

    [DisplayName("Alpha X")][Description("Angle of the specular ray in the X–Z plane, degrees.")]
    public double AlphaX { get; init; }

    [DisplayName("Alpha Y")][Description("Angle of the specular ray in the Y–Z plane, degrees.")]
    public double AlphaY { get; init; }

    [DisplayName("X minimum")][Description("Smallest |x| plotted, as a power of 10.")]
    public double XMinimum { get; init; } = -4;

    [DisplayName("Y minimum")][Description("Smallest BSDF plotted, as a power of 10 (smaller values are left out).")]
    public double YMinimum { get; init; } = -6;

    [DisplayName("X decades")][Description("Decades of |x| plotted.")]
    public int XDecades { get; init; } = 4;

    [DisplayName("Y decades")][Description("Decades of BSDF plotted (larger values are left out).")]
    public int YDecades { get; init; } = 8;
}

/// <summary>Settings of the <see cref="NscScatterPolarPlotAnalysis"/>.</summary>
public sealed record NscScatterPolarPlotSettings : INscScatterPlotSettings
{
    [DisplayName("Scatter model")][Description("Lambertian, Gaussian, ABg, ABg File or BSDF (User Defined is not built).")]
    public NscScatterModel Model { get; init; } = NscScatterModel.Lambertian;

    [DisplayName("Wavelength")][Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Fraction")][Description("Fraction to scatter, 0 to 1; ignored for ABg and ABg File.")]
    public double Fraction { get; init; } = 1;

    [DisplayName("Sigma/Angle")][Description("Gaussian σ; for BSDF the surface rotation (degrees).")]
    public double SigmaOrAngle { get; init; } = 0.1;

    [DisplayName("File name")][Description("ABg profile name, or the full path of the .ABGF or .BSDF file.")]
    public string FileName { get; init; } = "";

    [DisplayName("Incident angle")][Description("Angle of incidence in the X–Z plane, degrees (0 to below 90).")]
    public double IncidentAngle { get; init; }

    [DisplayName("Show as")][Description("False-colour image or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Plot scale")][Description("If non-zero, the top of the colour scale; zero takes it from the data.")]
    public double PlotScale { get; init; }

    [DisplayName("Radial bins")][Description("Bins across the 180° of the plot's diameter.")]
    public int RadialBins { get; init; } = 90;

    [DisplayName("Azimuthal bins")][Description("Bins around 360°.")]
    public int AzimuthalBins { get; init; } = 180;

    [DisplayName("Normalize")][Description("Divide by the largest BSDF value.")]
    public bool Normalize { get; init; }
}

/// <summary>What the two plots share: the model built from their settings.</summary>
internal static class NscScatterPlotSupport
{
    public static (NscScatterDistribution Distribution, double Micrometers, string Description) Build(
        OpticsAnalysisContext context, INscScatterPlotSettings settings, string who, List<string> notes)
    {
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        int count = system.Wavelengths.Wavelengths.Count;
        if (settings.Wavelength < 0 || settings.Wavelength > count)
            throw new ArgumentException($"{who}: the wavelength row must be 0 (primary) or 1 to {count}; it is {settings.Wavelength}.");
        double micrometers = system.Wavelengths.Wavelengths[settings.Wavelength == 0 ? system.Wavelengths.PrimaryIndex : settings.Wavelength - 1].Micrometers;
        if (settings.Model == NscScatterModel.None) throw new ArgumentException($"{who}: choose a scatter model (None has no BSDF).");
        if (settings.Model == NscScatterModel.UserDefined)
            throw new NotSupportedException($"{who}: User Defined scattering (a scatter DLL) is not built; no DLLs are loaded.");
        var face = new OpticsNscFaceSettings
        {
            ScatterModel = ProjectNscScatter.ModelNames[(int)settings.Model],
            ScatterFraction = settings.Fraction,
            ScatterSigma = settings.SigmaOrAngle,
            ScatterAngle = settings.SigmaOrAngle,
            ScatterReflect = settings.FileName,
        };
        var problems = new List<string>();
        var scatter = ProjectNscScatter.Face(face, context, who, problems);
        if (scatter is not null) scatter.Validate(who, problems);
        if (problems.Count > 0 || scatter is null) throw new ArgumentException(string.Join(Environment.NewLine, problems.DefaultIfEmpty($"{who}: no scatter model.")));
        var model = NscScatterer.Model(scatter, micrometers);
        var distribution = model.Reflect ?? throw new ArgumentException($"{who}: the model has no data.");
        return (distribution, micrometers, scatter.Describe());
    }
}

/// <summary>
/// OpticStudio's Scatter Function Viewer (help, "Scatter Function Viewer"): the BSDF of a scatter
/// model against |x|, the change in direction cosines between the specular and the scattered ray,
/// on log–log axes, for the specular ray given by Alpha X and Y (n = 1/√(1 + tan²α_x + tan²α_y),
/// l = n tan α_x, m = n tan α_y). Lambertian, Gaussian, ABg and ABg File are evaluated from their
/// formulas along x pointing from β₀ toward the centre of the unit disk (the BSDF of these models
/// depends on |x| only; points beyond the disk's edge are left out); a BSDF file is plotted, as
/// the help does, from scattered rays binned by |x|: BSDF ≈ (rays in the bin / rays)·TIS / (the
/// projected area of the bin's annulus inside the unit disk), the area ∫ r·arc(r) dr by
/// 20-point Gauss–Legendre per bin. The TIS is listed. Y Minimum and Y Decades leave out values
/// outside that range (the plot's axes are automatic).
/// </summary>
public sealed class NscScatterFunctionViewerAnalysis : OpticsAnalysis<NscScatterFunctionViewerSettings>
{
    public const string SavedId = "NscScatterFunctionViewer";
    public override string Id => SavedId;
    public override string Name => "Scatter function viewer";
    public override string Category => "Non-sequential";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscScatterFunctionViewerSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        if (!(settings.XDecades >= 1 && settings.XDecades <= 20) || !(settings.YDecades >= 1 && settings.YDecades <= 40))
            throw new ArgumentException($"{Name}: X decades must lie in 1–20 and Y decades in 1–40.");
        var (distribution, micrometers, description) = NscScatterPlotSupport.Build(context, settings, Name, notes);
        double tx = Math.Tan(settings.AlphaX * Math.PI / 180), ty = Math.Tan(settings.AlphaY * Math.PI / 180);
        if (!double.IsFinite(tx) || !double.IsFinite(ty) || Math.Abs(settings.AlphaX) >= 90 || Math.Abs(settings.AlphaY) >= 90)
            throw new ArgumentException($"{Name}: Alpha X and Y must lie strictly between −90° and 90°.");
        double n = 1 / Math.Sqrt(1 + tx * tx + ty * ty), l = n * tx, m = n * ty;
        var incidence = new NscScatterIncidence(l, m, Math.Acos(n) * 180 / Math.PI);
        double b = incidence.B0;
        double tis = distribution.Tis(incidence);
        double xLo = Math.Pow(10, settings.XMinimum), xHi = xLo * Math.Pow(10, settings.XDecades);
        double yLo = Math.Pow(10, settings.YMinimum), yHi = yLo * Math.Pow(10, settings.YDecades);
        var xs = new List<double>();
        var ys = new List<double>();
        if (distribution is NscTableScatter)
        {
            if (settings.Bins < 1 || settings.Bins > 10_000 || settings.Rays < 1 || settings.Rays > 100_000_000)
                throw new ArgumentException($"{Name}: bins must lie in 1–10000 and rays in 1–10⁸.");
            var edges = Enumerable.Range(0, settings.Bins + 1).Select(i => xLo * Math.Pow(xHi / xLo, (double)i / settings.Bins)).ToArray();
            var counts = new long[settings.Bins];
            var rng = new SplitMix64(1);
            for (int k = 0; k < settings.Rays; k++)
            {
                if ((k & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                var (bx, by) = distribution.Sample(incidence, rng);
                double r = Math.Sqrt((bx - l) * (bx - l) + (by - m) * (by - m));
                if (r < edges[0] || r >= edges[^1]) continue;
                int i = (int)Math.Floor(Math.Log(r / xLo) / Math.Log(xHi / xLo) * settings.Bins);
                counts[Math.Clamp(i, 0, settings.Bins - 1)]++;
            }
            for (int i = 0; i < settings.Bins; i++)
            {
                var (nodes, weights) = GaussLegendre.Rule(20, edges[i], edges[i + 1]);
                double area = 0;
                for (int j = 0; j < nodes.Length; j++) area += weights[j] * nodes[j] * NscScatterMath.ArcInDisk(nodes[j], b);
                if (!(area > 0) || counts[i] == 0) continue;
                double bsdf = (double)counts[i] / settings.Rays * tis / area;
                xs.Add(Math.Sqrt(edges[i] * edges[i + 1]));
                ys.Add(bsdf);
            }
            notes.Add($"The BSDF file is plotted from {settings.Rays} scattered rays (seed 1) in {settings.Bins} bins of |x|; empty bins are left out.");
        }
        else
        {
            double ex = b > 0 ? -l / b : 1, ey = b > 0 ? -m / b : 0;
            const int points = 400;
            for (int i = 0; i <= points; i++)
            {
                double r = xLo * Math.Pow(xHi / xLo, (double)i / points);
                if (r > 1 + b) break;
                double v = distribution.Bsdf(incidence, l + r * ex, m + r * ey);
                xs.Add(r);
                ys.Add(v);
            }
        }
        var keepX = new List<double>();
        var keepY = new List<double>();
        for (int i = 0; i < xs.Count; i++)
            if (ys[i] >= yLo && ys[i] <= yHi) { keepX.Add(xs[i]); keepY.Add(ys[i]); }
        var plot = new LinePlot("BSDF", "|x| (direction cosines)", "BSDF (1/sr)", new[] { new AnalysisCurve(description, keepX, keepY) }) { LogX = true, LogY = true };
        var rows = xs.Select((x, i) => (IReadOnlyList<string>)new[] { StudyReport.Number(x, "g8"), StudyReport.Number(ys[i], "g8") }).ToList();
        var sections = new List<ReportSection>
        {
            new("Scatter function")
            {
                Pairs = new List<(string, string)>
                {
                    ("Model", description),
                    ("Wavelength", $"{StudyReport.Number(micrometers)} µm"),
                    ("Specular ray (l, m, n)", $"{StudyReport.Number(l, "g8")}, {StudyReport.Number(m, "g8")}, {StudyReport.Number(n, "g8")}"),
                    ("TIS", StudyReport.Number(tis, "g10")),
                },
            },
            new("BSDF") { TableHeaders = new[] { "|x|", "BSDF (1/sr)" }, TableRows = rows },
        };
        notes.Add("BSDF is the scattered radiance per unit incident irradiance; TIS is its integral over the projected unit disk (docs/Optics-Conventions.md §74).");
        return OpticsAnalysisResult.FromSections(Name, sections, notes, new AnalysisPlot[] { plot });
    }
}

/// <summary>
/// OpticStudio's Scatter Polar Plot (help, "Scatter Polar Plot"): the BSDF over the scattered
/// hemisphere for a given angle of incidence (in the X–Z plane, the specular ray toward +x), "in
/// standard polar coordinates, with the radial angle extending outwards from the center and the
/// azimuthal angle moving counter-clockwise from the +X axis", drawn on a square map of
/// (θ cos φ, θ sin φ) in degrees whose nodes take the value at the centre of their polar bin
/// (radial bins span the 180° diameter, azimuthal bins the 360°). Nodes beyond 90° are blank.
/// </summary>
public sealed class NscScatterPolarPlotAnalysis : OpticsAnalysis<NscScatterPolarPlotSettings>
{
    public const string SavedId = "NscScatterPolarPlot";
    public override string Id => SavedId;
    public override string Name => "Scatter polar plot";
    public override string Category => "Non-sequential";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscScatterPolarPlotSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        if (settings.RadialBins < 2 || settings.RadialBins > 2000 || settings.AzimuthalBins < 1 || settings.AzimuthalBins > 3600)
            throw new ArgumentException($"{Name}: radial bins must lie in 2–2000 and azimuthal bins in 1–3600.");
        if (!(settings.IncidentAngle >= 0 && settings.IncidentAngle < 90))
            throw new ArgumentException($"{Name}: the incident angle must lie in [0°, 90°).");
        if (!(settings.PlotScale >= 0)) throw new ArgumentException($"{Name}: the plot scale must be zero or positive.");
        var (distribution, micrometers, description) = NscScatterPlotSupport.Build(context, settings, Name, notes);
        double si = Math.Sin(settings.IncidentAngle * Math.PI / 180);
        var incidence = new NscScatterIncidence(si, 0, settings.IncidentAngle);
        double tis = distribution.Tis(incidence);

        int nodes = settings.RadialBins + 1;
        double radialWidth = 180.0 / settings.RadialBins, azimuthWidth = 360.0 / settings.AzimuthalBins;
        var values = new double[nodes, nodes];
        double max = 0;
        for (int row = 0; row < nodes; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            double y = -90 + 180.0 * row / (nodes - 1);
            for (int col = 0; col < nodes; col++)
            {
                double x = -90 + 180.0 * col / (nodes - 1);
                double theta = Math.Sqrt(x * x + y * y);
                if (theta > 90) { values[row, col] = double.NaN; continue; }
                double phi = Math.Atan2(y, x) * 180 / Math.PI;
                if (phi < 0) phi += 360;
                double tc = Math.Min(90, (Math.Floor(theta / radialWidth) + 0.5) * radialWidth);
                double pc = (Math.Floor(phi / azimuthWidth) + 0.5) * azimuthWidth;
                double st = Math.Sin(tc * Math.PI / 180);
                double v = distribution.Bsdf(incidence, st * Math.Cos(pc * Math.PI / 180), st * Math.Sin(pc * Math.PI / 180));
                values[row, col] = v;
                max = Math.Max(max, v);
            }
        }
        if (settings.Normalize && max > 0)
            for (int row = 0; row < nodes; row++)
                for (int col = 0; col < nodes; col++) values[row, col] /= max;
        var plot = new AnalysisMapPlot("Scatter polar plot", "θ cos φ (°)", "θ sin φ (°)", new GridField(values, -90, 90, -90, 90),
            settings.Normalize ? "relative" : "1/sr")
        {
            Style = settings.ShowAs,
            Maximum = settings.PlotScale > 0 ? settings.PlotScale : double.NaN,
            Minimum = 0,
        };
        var sections = new List<ReportSection>
        {
            new("Scatter polar plot")
            {
                Pairs = new List<(string, string)>
                {
                    ("Model", description),
                    ("Wavelength", $"{StudyReport.Number(micrometers)} µm"),
                    ("Incident angle", $"{StudyReport.Number(settings.IncidentAngle)}° (X–Z plane)"),
                    ("TIS", StudyReport.Number(tis, "g10")),
                    ("Largest BSDF", $"{StudyReport.Number(max, "g8")} 1/sr"),
                },
            },
        };
        notes.Add("The BSDF of the model as traced (fraction × weight × the normalised direction density, docs/Optics-Conventions.md §74); for a BSDF file this is the cell model of the nearest data set, not the file's raw values.");
        return OpticsAnalysisResult.FromSections(Name, sections, notes, new AnalysisPlot[] { plot });
    }
}
