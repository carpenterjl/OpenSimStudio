using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using static OpenSim.Optics.Analysis.RayAnalysisSupport;

namespace OpenSim.Optics.Analysis.StrayLight;

/// <summary>One surface's YNI: the paraxial marginal ray height y (m), the signed index n before
/// the surface, the paraxial angle of incidence i (radians) and YNI = y·n·i (m); NaN entries for
/// a surface where the angle of incidence is not defined (a paraxial surface type).</summary>
public readonly record struct YniSurface(int Surface, double Height, double Index, double Incidence, double Yni);

/// <summary>
/// YNI contributions (OP-46; OpticStudio 2025 R2 help, "YNI Contributions" and the YNIP operand
/// in "Constraints on Paraxial Ray Data", saved under plans/sources/optics/op-46-help/ and
/// op-54-help/): "The YNI contribution of each surface is the product of the paraxial marginal ray
/// height times the index times the angle of incidence at the surface", which the help relates to
/// the surface's narcissus contribution (it cites Applied Optics 21(18) p. 3393, 1982, not on hand;
/// the help's cold-return equation is an image this program does not reproduce).
/// <para>
/// Here i = u + y·c is the paraxial angle of incidence (u the marginal ray's slope before the
/// surface, c its paraxial curvature) and n the signed index before the surface (§11), so n·i is
/// the refraction invariant A = n(u + y c) of the Seidel sums (§32) and is the same on either side
/// of the surface; YNI = y·A. The sign follows: positive y and A give a positive YNI. The help
/// does not say which side's index is meant; with A invariant the choice does not matter.
/// </para>
/// </summary>
public static class YniContributions
{
    /// <summary>The YNI of every surface 1 … last at 0-based wavelength row <paramref name="row"/>.</summary>
    public static IReadOnlyList<YniSurface> Compute(SequentialLens lens, OpticalSystemData system, int row)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        var paraxialLens = lens.ToParaxialLens();
        var p = ParaxialSystem.Create(paraxialLens, system, row);
        var list = new List<YniSurface>();
        for (int j = 1; j <= lens.LastSurfaceIndex; j++) list.Add(Of(p, j));
        return list;
    }

    /// <summary>The YNI of surface <paramref name="j"/> of a paraxial model.</summary>
    public static YniSurface Of(ParaxialSystem p, int j)
    {
        ArgumentNullException.ThrowIfNull(p);
        var s = p.Lens.Surfaces[j];
        double y = p.MarginalRay.Heights[j];
        double n = p.SignedIndices[j - 1];
        if (s.ThinLensPower != 0 || s.SlopeMatrix is not null)
            return new YniSurface(j, y, n, double.NaN, double.NaN);
        double u = p.MarginalRay.Slopes[j - 1];
        double i = u + y * s.Curvature;
        return new YniSurface(j, y, n, i, y * n * i);
    }
}

/// <summary>Settings of the <see cref="YniContributionsAnalysis"/> (help: Wavelength).</summary>
public sealed record YniContributionsSettings
{
    [DisplayName("Wavelength"), Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }
}

/// <summary>The YNI Contributions listing (OP-46): y, n, i and YNI per surface, and their sum.</summary>
public sealed class YniContributionsAnalysis : OpticsAnalysis<YniContributionsSettings>
{
    public const string SavedId = "YniContributions";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public YniContributionsAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "YNI contributions";
    public override string Category => "Stray light";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, YniContributionsSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        int wave = OneWavelength(Name, settings.Wavelength, system);
        var p = ParaxialSystem.Create(lens.ToParaxialLens(), system, wave);
        cancellation.ThrowIfCancellationRequested();
        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        double perMeter = OpticsConventions.FromMeters(1, unit);
        var rows = new List<IReadOnlyList<string>>();
        var xs = new List<double>(); var ys = new List<double>();
        var lines = new List<string>();
        double sum = 0;
        for (int j = 1; j <= lens.LastSurfaceIndex; j++)
        {
            var y = YniContributions.Of(p, j);
            if (double.IsNaN(y.Yni))
            {
                rows.Add(new[] { SurfaceName(lens, j), Lens(y.Height, unit), StudyReport.Number(y.Index, "g8"), "—", "—" });
                lines.Add($"Surface {j} is a paraxial surface type (thin-lens power or ABCD matrix): it has no angle of incidence, so no YNI.");
                continue;
            }
            rows.Add(new[] { SurfaceName(lens, j), Lens(y.Height, unit), StudyReport.Number(y.Index, "g8"), StudyReport.Number(y.Incidence, "g8"), Lens(y.Yni, unit) });
            xs.Add(j); ys.Add(y.Yni * perMeter);
            sum += y.Yni;
        }
        lines.Add($"Sum of YNI over the listed surfaces: {Lens(sum, unit)} {u}.");
        var sections = new[]
        {
            new ReportSection("YNI contributions")
            {
                TableHeaders = new[] { "Surface", $"Marginal y ({u})", "Index n (signed)", "Incidence i (rad)", $"YNI ({u})" },
                TableRows = rows, Lines = lines,
            },
        };
        var plots = new AnalysisPlot[] { new LinePlot("YNI contributions", "Surface", $"YNI ({u})", new[] { new AnalysisCurve("YNI", xs, ys) }) };
        var assumptions = new List<string>
        {
            $"Paraxial marginal ray at {WaveLabel(system, wave)} (docs/Optics-Conventions.md §11); YNI = y·n·i with i = u + y·c the paraxial angle of incidence and n the signed index before the surface, so n·i is the refraction invariant (§49).",
            "The help: the narcissus intensity is related to the cold return of all refractive surfaces, and to reduce it the YNI should be as large as possible; the cold-return equation itself is not evaluated.",
            "Mirrors are listed with their YNI like any surface.",
        };
        assumptions.AddRange(p.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}
