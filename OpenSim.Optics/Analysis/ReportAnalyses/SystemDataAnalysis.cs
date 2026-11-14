// OP-44
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="SystemDataAnalysis"/> and the
/// <see cref="SystemSummaryGraphicAnalysis"/>: OpticStudio's reports have none.</summary>
public sealed record SystemReportSettings { }

/// <summary>
/// OpticStudio's System Data report (2025 R2 help, "System Data": "many system-related
/// parameters, such as pupil positions and sizes, magnification, F/#, etc."), assembled from the
/// analyses that already compute them: the system summary (<see cref="SystemSummaryAnalysis"/>:
/// project, mode, environment), the system inputs (aperture, fields, wavelengths), and the
/// paraxial system data with the focal data at every wavelength
/// (<see cref="ParaxialDataAnalysis"/>, without its ray table and cardinal points, which have
/// their own reports).
/// </summary>
public sealed class SystemDataAnalysis : OpticsAnalysis<SystemReportSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "SystemData";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for the project's saved surfaces.</param>
    public SystemDataAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? ProjectLens.Sequential;
    }

    public override string Id => SavedId;
    public override string Name => "System data";
    public override string Category => "Reports";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, SystemReportSettings settings,
        CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var summary = new SystemSummaryAnalysis().Compute(context,
            new SystemSummarySettings { Units = system.Units.LensUnit, ShowConventions = false }, cancellation);
        var lens = _lensSource(context);
        var sections = new List<ReportSection>(summary.Sections)
        {
            PrescriptionDataAnalysis.SystemInputs("System inputs", system, lens),
        };
        var assumptions = new List<string>();
        if (lens is null)
            sections.Add(new ReportSection("No surfaces") { Lines = new[] { "The system has no sequential surfaces, so there are no first-order data." } });
        else
        {
            var paraxial = ParaxialDataAnalysis.FromSequential(_ => lens).Compute(context,
                new ParaxialDataSettings { ShowCardinalPoints = false, ShowRayData = false }, cancellation);
            sections.AddRange(paraxial.Sections);
            assumptions.AddRange(paraxial.Assumptions);
        }
        foreach (var a in summary.Assumptions.Concat(notes))
            if (!assumptions.Contains(a)) assumptions.Add(a);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions);
    }
}

/// <summary>
/// OpticStudio's System Summary Graphic (2025 R2 help, "System Summary Graphic": "a summary of
/// the system data, similar to the text based system data report", used mainly on a Report
/// Graphic page): the few numbers a report page shows (focal lengths, F/#, pupil, track, field,
/// wavelengths). The window and the report graphic show it as text; it is not drawn as a picture.
/// </summary>
public sealed class SystemSummaryGraphicAnalysis : OpticsAnalysis<SystemReportSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "SystemSummaryGraphic";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for the project's saved surfaces.</param>
    public SystemSummaryGraphicAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? ProjectLens.Sequential;
    }

    public override string Id => SavedId;
    public override string Name => "System summary graphic";
    public override string Category => "Reports";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, SystemReportSettings settings,
        CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return ReportText.NoSurfaces(Name);
        var unit = system.Units.LensUnit;
        string u = ConventionNames.Symbol(unit);
        string L(double? meters) => meters is double m ? $"{ReportText.Length(m, unit, "g6")} {u}" : "—";
        var model = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        cancellation.ThrowIfCancellationRequested();
        bool angle = FieldTypes.IsAngle(system.Fields.Type);
        var pairs = new List<(string, string)>
        {
            ("System", context.ProjectName),
            ("Surfaces", $"{lens.Surfaces.Count - 2}, stop on surface {lens.StopIndex}"),
            ("Effective focal length", model.IsAfocal ? "afocal" : L(model.EffectiveFocalLength)),
            ("Back focal length", model.IsAfocal ? "afocal" : L(model.BackFocalLength)),
            ("Image space F/#", model.ImageSpaceFNumber is double f ? StudyReport.Number(f, "g6") : "—"),
            ("Paraxial working F/#", model.WorkingFNumber is double w ? StudyReport.Number(w, "g6") : "—"),
            ("Entrance pupil diameter", L(model.EntrancePupilDiameter)),
            ("Total track", L(model.TotalTrack)),
            ("Maximum field", angle ? $"{StudyReport.Number(model.FullField, "g6")}°" : L(model.FullField)),
            ("Wavelengths (µm)", string.Join(", ", system.Wavelengths.Wavelengths.Select(x => StudyReport.Number(x.Micrometers, "g6")))
                + $" (primary {system.Wavelengths.PrimaryIndex + 1})"),
        };
        var assumptions = new List<string>(ParaxialReport.Assumptions(model))
        {
            "Shown as text: the graphic panel of OpticStudio's System Summary Graphic is not drawn.",
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, new[] { new ReportSection("System summary") { Pairs = pairs } }, assumptions);
    }
}
