using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// OP-36: Seidel Coefficients and Seidel Diagram (Ansys OpticStudio 2025 R2 help, pages of the same
// names, saved under plans/sources/optics/op-36-help/).

/// <summary>Settings of the <see cref="SeidelCoefficientsAnalysis"/>.</summary>
public sealed record SeidelCoefficientsSettings
{
    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }
}

/// <summary>Settings of the <see cref="SeidelDiagramAnalysis"/>.</summary>
public sealed record SeidelDiagramSettings
{
    [DisplayName("First surface")]
    [Description("The first surface shown; the sum is over the shown range.")]
    public int FirstSurface { get; init; } = 1;

    [DisplayName("Last surface")]
    [Description("The last surface shown; 0 for the last surface before the image.")]
    public int LastSurface { get; init; }

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Ignore distortion")]
    [Description("Leave the distortion bars out.")]
    public bool IgnoreDistortion { get; init; }

    [DisplayName("Ignore chromatic")]
    [Description("Leave the axial and lateral colour bars out.")]
    public bool IgnoreChromatic { get; init; }
}

/// <summary>What the two Seidel analyses share.</summary>
internal static class SeidelCommon
{
    public static string Surface(SequentialLens lens, int j) => j == lens.StopIndex ? $"STO ({j})" : j.ToString();

    public static string N(double v) => double.IsFinite(v) ? StudyReport.Number(v, "g6") : "—";

    public static List<string> Assumptions(SeidelResult r, OpticalSystemData system)
    {
        var a = new List<string>
        {
            $"Paraxial marginal ray of the system aperture and paraxial chief ray of the largest radial field ({N(r.FullField * (FieldTypes.IsAngle(system.Fields.Type) ? 1 : OpticsConventions.FromMeters(1, system.Units.LensUnit)))} {(FieldTypes.IsAngle(system.Fields.Type) ? "°" : ParaxialReport.UnitName(system.Units.LensUnit))}), at {N(r.WavelengthMicrometers)} µm (docs/Optics-Conventions.md §32).",
            "Surface sums from RayOptics' formulation (Welford's form) with signed indices; an aspheric surface adds its fourth-order departure from the paraxial sphere.",
            r.ColourRows is { } rows
                ? $"Colour (CL, CT) differences the indices at the shortest ({N(system.Wavelengths.Wavelengths[rows.Shortest].Micrometers)} µm) and longest ({N(system.Wavelengths.Wavelengths[rows.Longest].Micrometers)} µm) wavelengths, referenced to the selected wavelength."
                : "One wavelength: no colour.",
            "Wave coefficients in waves at the edge of the exit pupil, from S1 = 8 W040, S2 = 2 W131, S3 = 2 W222, S4 = 4 W220 − 2 W222, S5 = 2 W311; W220T = (3 S3 + S4)/4.",
            "Transverse coefficients are image-plane ray errors k·S/(n′u′) with u′ = dy/dz; longitudinal ones k·S/(|n′|u′²), positive when rays cross the axis before the paraxial image. Both are large and of little meaning where the image space is nearly collimated.",
        };
        a.AddRange(r.Notes);
        return a;
    }

    public static double Shown(double meters, LensUnit unit) => OpticsConventions.FromMeters(meters, unit);
}

/// <summary>
/// OpticStudio's Seidel Coefficients (OP-36): "the unconverted Seidel, transverse, longitudinal,
/// and some wavefront coefficients … listed surface by surface, as well as a sum for the entire
/// system" (help page "Seidel Coefficients"), with the Petzval radius. Lengths in lens units,
/// wave coefficients in waves. The definitions are on <see cref="SeidelEngine"/>.
/// </summary>
public sealed class SeidelCoefficientsAnalysis : OpticsAnalysis<SeidelCoefficientsSettings>
{
    public const string SavedId = "SeidelCoefficients";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public SeidelCoefficientsAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Seidel coefficients";
    public override string Category => "Aberrations";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, SeidelCoefficientsSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return FieldScanCommon.NoSurfaces(Name);
        int row = FieldScanCommon.Rows(settings.Wavelength, system, Name, zeroIsAll: false)[0];
        cancellation.ThrowIfCancellationRequested();
        var r = SeidelEngine.Compute(lens, system, row);
        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        string N(double v) => SeidelCommon.N(v);
        string L(double v) => N(SeidelCommon.Shown(v, unit));

        IReadOnlyList<string> Row(string label, double[] values, Func<double, string> f) =>
            new[] { label }.Concat(values.Select(f)).ToArray();
        IEnumerable<(string Label, SeidelTerms Terms)> All() =>
            r.Surfaces.Select(s => (SeidelCommon.Surface(lens, s.Surface), s.Terms)).Append(("Sum", r.Sum));

        var sections = new List<ReportSection>
        {
            new("Seidel aberration coefficients")
            {
                TableHeaders = new[] { "Surface" }.Concat(SeidelTerms.Names.Select(n => $"{n} ({u})")).ToArray(),
                TableRows = All().Select(x => Row(x.Label, x.Terms.ToArray(), L)).ToArray(),
            },
            new("Seidel aberration coefficients in waves")
            {
                TableHeaders = new[] { "Surface" }.Concat(SeidelWaveCoefficients.Names).ToArray(),
                TableRows = All().Select(x => Row(x.Label, r.Wave(x.Terms).ToArray(), N)).ToArray(),
            },
        };
        if (!r.ImageAtInfinity)
        {
            sections.Add(new ReportSection("Transverse aberration coefficients")
            {
                TableHeaders = new[] { "Surface" }.Concat(SeidelRayCoefficients.TransverseNames.Select(n => $"{n} ({u})")).ToArray(),
                TableRows = All().Select(x => Row(x.Label, r.Transverse(x.Terms).ToArray(), L)).ToArray(),
            });
            sections.Add(new ReportSection("Longitudinal aberration coefficients")
            {
                TableHeaders = new[] { "Surface" }.Concat(SeidelRayCoefficients.LongitudinalNames.Select(n => $"{n} ({u})")).ToArray(),
                TableRows = All().Select(x => Row(x.Label, r.Longitudinal(x.Terms).ToArray(), L)).ToArray(),
            });
        }
        var pairs = new List<(string, string)>
        {
            ($"Petzval radius ({u})", double.IsInfinity(r.PetzvalRadius) ? "infinite (zero Petzval sum)" : L(r.PetzvalRadius)),
            ($"Lagrange invariant ({u})", L(r.LagrangeInvariant)),
            ("Image-space n′u′ of the marginal ray", N(r.ImageReducedSlope)),
            ("Wavelength (µm)", N(r.WavelengthMicrometers)),
        };
        var aspheric = r.Surfaces.Where(s => s.Aspheric != default).ToArray();
        var lines = aspheric.Length == 0
            ? new List<string> { "No surface has a fourth-order aspheric departure." }
            : aspheric.Select(s => $"Surface {s.Surface}: aspheric part of S1 … S5 = {string.Join(", ", s.Aspheric.ToArray().Take(5).Select(L))} {u} (included above).").ToList();
        sections.Add(new ReportSection("Summary") { Pairs = pairs, Lines = lines });

        var assumptions = SeidelCommon.Assumptions(r, system);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions);
    }
}

/// <summary>
/// OpticStudio's Seidel Diagram (OP-36): "Seidel unconverted aberration coefficients as a bar
/// chart … surface by surface, as well as a sum for the selected range of surfaces" (help page
/// "Seidel Diagram"). The bars are drawn as rectangles in a line plot (one curve per coefficient,
/// the rectangles separated by gaps); the sum is the last group. The help's Plot Scale setting is
/// not built: the plot scales itself.
/// </summary>
public sealed class SeidelDiagramAnalysis : OpticsAnalysis<SeidelDiagramSettings>
{
    public const string SavedId = "SeidelDiagram";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public SeidelDiagramAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Seidel diagram";
    public override string Category => "Aberrations";

    /// <summary>The coefficient indices (into <see cref="SeidelTerms.ToArray"/>) the settings show.</summary>
    internal static int[] Shown(SeidelDiagramSettings settings) =>
        Enumerable.Range(0, 7).Where(k => !(settings.IgnoreDistortion && k == 4) && !(settings.IgnoreChromatic && k >= 5)).ToArray();

    /// <summary>
    /// The bar outlines of one coefficient: for each group g (x = group's surface number) a
    /// rectangle from 0 to the value, of width <paramref name="width"/>, offset within the group,
    /// separated from the next by a NaN gap.
    /// </summary>
    internal static AnalysisCurve Bars(string name, IReadOnlyList<double> centres, IReadOnlyList<double> values, double width)
    {
        var x = new List<double>();
        var y = new List<double>();
        for (int i = 0; i < centres.Count; i++)
        {
            double x0 = centres[i] - width / 2, x1 = centres[i] + width / 2, v = values[i];
            x.AddRange(new[] { x0, x0, x1, x1, x1 });
            y.AddRange(new[] { 0, v, v, 0, double.NaN });
        }
        return new AnalysisCurve(name, x, y);
    }

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, SeidelDiagramSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return FieldScanCommon.NoSurfaces(Name);
        int row = FieldScanCommon.Rows(settings.Wavelength, system, Name, zeroIsAll: false)[0];
        int lastSurface = lens.LastSurfaceIndex;
        int first = settings.FirstSurface, last = settings.LastSurface == 0 ? lastSurface : settings.LastSurface;
        if (first < 1 || last > lastSurface || first > last)
            throw new ArgumentException($"{Name}: the surface range must run from 1 to {lastSurface} with the first not after the last (it is {settings.FirstSurface} to {settings.LastSurface}; 0 for the last surface).");
        cancellation.ThrowIfCancellationRequested();
        var r = SeidelEngine.Compute(lens, system, row);
        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);

        var range = r.Surfaces.Where(s => s.Surface >= first && s.Surface <= last).ToArray();
        var sum = range.Aggregate(default(SeidelTerms), (acc, s) => acc + s.Terms);
        var groups = range.Select(s => (double)s.Surface).Append(last + 1).ToArray();
        var shown = Shown(settings);
        double width = 0.8 / shown.Length;
        var curves = new List<AnalysisCurve>();
        for (int i = 0; i < shown.Length; i++)
        {
            int k = shown[i];
            double offset = -0.4 + width * (i + 0.5);
            var values = range.Select(s => SeidelCommon.Shown(s.Terms.ToArray()[k], unit)).Append(SeidelCommon.Shown(sum.ToArray()[k], unit)).ToArray();
            curves.Add(Bars(SeidelTerms.Names[k], groups.Select(g => g + offset).ToArray(), values, width));
        }

        var table = range.Select(s => (Label: SeidelCommon.Surface(lens, s.Surface), s.Terms)).Append((Label: "Sum", Terms: sum))
            .Select(x => (IReadOnlyList<string>)new[] { x.Label }.Concat(shown.Select(k => SeidelCommon.N(SeidelCommon.Shown(x.Terms.ToArray()[k], unit)))).ToArray())
            .ToArray();
        var sections = new[]
        {
            new ReportSection($"Seidel coefficients, surfaces {first} to {last}")
            {
                TableHeaders = new[] { "Surface" }.Concat(shown.Select(k => $"{SeidelTerms.Names[k]} ({u})")).ToArray(),
                TableRows = table,
            },
        };
        var assumptions = SeidelCommon.Assumptions(r, system);
        assumptions.Add($"Bars at each surface number; the sum over surfaces {first}–{last} is the group at {last + 1}. The help's Plot Scale is not built (the plot scales itself).");
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[]
        {
            new LinePlot("Seidel diagram", $"Surface (the sum at {last + 1})", $"Coefficient ({u})", curves),
        });
    }
}
