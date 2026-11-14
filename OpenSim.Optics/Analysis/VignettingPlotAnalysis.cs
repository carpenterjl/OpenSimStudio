using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="VignettingPlotAnalysis"/>.</summary>
public sealed record VignettingPlotSettings
{
    [DisplayName("Ray density")]
    [Description("n: a (2n + 1) × (2n + 1) grid of rays at each field, those inside the pupil traced; at least 5.")]
    public int RayDensity { get; init; } = 10;

    [DisplayName("Field density")]
    [Description("Fields from the axis to the largest +y field, evenly spaced; at least 2.")]
    public int FieldDensity { get; init; } = 21;

    [DisplayName("Remove vignetting factors")]
    [Description("Replace the fields' vignetting factors with surface apertures first; off traces through the factors.")]
    public bool RemoveVignettingFactors { get; init; } = true;
}

/// <summary>
/// OpticStudio's Vignetting Plot (OP-22): the fraction of rays that reach the image surface
/// against field along +y, at the primary wavelength (<see cref="VignettingTools.Curve"/>). By
/// default the fields' vignetting factors are first replaced with surface apertures
/// (<see cref="VignettingTools.ReplaceWithApertures"/>), as the help's default does ("If
/// 'Remove Vignetting Factors' is checked on (the default), any defined vignetting factors will
/// automatically be replaced with surface apertures"; Ansys OpticStudio 2025 R2 help, "Vignetting
/// Plot", ansyshelp.ansys.com). The ray density's minimum of 5 is the same page's.
/// <para>
/// Like <see cref="SingleRayTraceAnalysis"/>, the lens comes from a source given to the
/// constructor; the default has none until the lens data editor (OP-31), and the result then
/// says so.
/// </para>
/// </summary>
public sealed class VignettingPlotAnalysis : OpticsAnalysis<VignettingPlotSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "VignettingPlot";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for none (until OP-31).</param>
    public VignettingPlotAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? (_ => null);
    }

    public override string Id => SavedId;
    public override string Name => "Vignetting plot";
    public override string Category => "Rays";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, VignettingPlotSettings settings,
        CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null)
        {
            return OpticsAnalysisResult.FromSections(Name, new[]
            {
                new ReportSection("No surfaces")
                {
                    Lines = new[] { "The system has no sequential surfaces yet: the lens data editor is a later plan item (OP-31), so there is nothing to trace." }
                }
            }, new[] { "Nothing is traced." });
        }
        if (settings.RayDensity < 5)
            throw new ArgumentException($"Vignetting plot: the ray density must be at least 5 (it is {settings.RayDensity}).");
        if (settings.FieldDensity < 2)
            throw new ArgumentException($"Vignetting plot: the field density must be at least 2 (it is {settings.FieldDensity}).");

        bool replaced = false;
        if (settings.RemoveVignettingFactors && system.Fields.Points.Any(p => p.IsVignetted))
        {
            var r = VignettingTools.ReplaceWithApertures(lens, system, cancellation: cancellation);
            lens = r.Lens;
            system = r.System;
            replaced = true;
            notes.AddRange(r.Notes);
        }
        var curve = VignettingTools.Curve(lens, system, settings.RayDensity, settings.FieldDensity, cancellation: cancellation);

        var unit = system.Units.LensUnit;
        bool angle = FieldTypes.IsAngle(system.Fields.Type);
        string fieldUnit = angle ? "°" : ParaxialReport.UnitName(unit);
        double maxY = system.Fields.Scale.Y;
        double ToShown(double hy) => angle ? hy * maxY : OpticsConventions.FromMeters(hy * maxY, unit);
        var x = curve.Hy.Select(ToShown).ToArray();
        var rows = new List<IReadOnlyList<string>>();
        for (int i = 0; i < x.Length; i++)
            rows.Add(new[] { StudyReport.Number(curve.Hy[i], "g6"), StudyReport.Number(x[i], "g8"), StudyReport.Number(curve.Fraction[i], "g6") });

        string fieldLabel = $"Y field ({fieldUnit})";
        var plot = new LinePlot("Fraction of unvignetted rays", fieldLabel, "Fraction unvignetted",
            new[] { new AnalysisCurve("Primary wavelength", x, curve.Fraction) });
        var sections = new[]
        {
            new ReportSection("Vignetting")
            {
                TableHeaders = new[] { "Hy", fieldLabel, "Fraction unvignetted" },
                TableRows = rows,
                Lines = new[] { $"{curve.RaysPerField} rays per field, primary wavelength, fields along +y." }
            },
        };
        var assumptions = new List<string>
        {
            "A ray counts when it reaches the image surface; one that misses a surface, is totally internally reflected, is stopped by an aperture or cannot be aimed is vignetted.",
            replaced ? "The fields' vignetting factors were replaced with surface apertures before tracing (docs/Optics-Conventions.md §16)."
                : system.Fields.Points.Any(p => p.IsVignetted)
                    ? "The vignetting factors were kept: rays go into each field's remapped pupil (interpolated between fields), and the fraction is multiplied by the area it keeps, (1 − VCX)(1 − VCY)."
                    : "No field has vignetting factors.",
            $"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.",
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }
}
