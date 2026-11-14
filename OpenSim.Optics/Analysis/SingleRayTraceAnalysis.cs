using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="SingleRayTraceAnalysis"/>.</summary>
public sealed record SingleRayTraceSettings
{
    [DisplayName("Hx")]
    [Description("Normalised field x.")]
    public double Hx { get; init; }

    [DisplayName("Hy")]
    [Description("Normalised field y.")]
    public double Hy { get; init; }

    [DisplayName("Px")]
    [Description("Normalised pupil x (paraxial entrance pupil; ray aiming is not built).")]
    public double Px { get; init; }

    [DisplayName("Py")]
    [Description("Normalised pupil y (paraxial entrance pupil; ray aiming is not built).")]
    public double Py { get; init; }

    /// <summary>The wavelength row (1 = the first); 0 is the primary wavelength.</summary>
    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Global coordinates")]
    [Description("List positions in surface 1's frame instead of each surface's own.")]
    public bool GlobalCoordinates { get; init; }
}

/// <summary>
/// One real ray, surface by surface (OpticStudio's Single Ray Trace), from the real ray trace
/// (<see cref="SequentialTrace"/>, OP-20): coordinates, direction cosines, angles of incidence
/// and exit, segment lengths and optical path, and where and why the ray failed, if it did.
/// <para>
/// The project has no saved sequential surfaces until the lens data editor (OP-31), so the lens
/// comes from a source given to the constructor; the default has none, and the result then
/// says so.
/// </para>
/// </summary>
public sealed class SingleRayTraceAnalysis : OpticsAnalysis<SingleRayTraceSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "SingleRayTrace";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for none (until OP-31).</param>
    public SingleRayTraceAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? (_ => null);
    }

    public override string Id => SavedId;
    public override string Name => "Single ray trace";
    public override string Category => "Rays";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, SingleRayTraceSettings settings,
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
        int count = system.Wavelengths.Wavelengths.Count;
        if (settings.Wavelength < 0 || settings.Wavelength > count)
            throw new ArgumentException($"Single ray trace: the wavelength row must be 0 (primary) or 1 to {count} (it is {settings.Wavelength}).");
        int? row = settings.Wavelength == 0 ? null : settings.Wavelength - 1;

        IReadOnlyList<double>? semiDiameters = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semiDiameters = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
        var trace = SequentialTrace.Create(lens, system, row, semiDiameters);
        var launcher = new RayLauncher(lens, system);
        cancellation.ThrowIfCancellationRequested();
        var path = trace.TracePath(launcher.Launch(trace, settings.Hx, settings.Hy, settings.Px, settings.Py));

        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        string L(double meters) => StudyReport.Number(OpticsConventions.FromMeters(meters, unit), "g10");
        string N(double v) => StudyReport.Number(v, "g10");
        var rows = new List<IReadOnlyList<string>>();
        foreach (var h in path.Hits)
        {
            var p = settings.GlobalCoordinates ? h.GlobalPosition : h.Position;
            var d = settings.GlobalCoordinates ? trace.GlobalPlacement(h.Surface).ApplyDirection(h.Direction) : h.Direction;
            string name = h.Surface == 0 ? "OBJ" : h.Surface == lens.ImageIndex ? "IMA"
                : h.Surface == lens.StopIndex ? $"{h.Surface} (stop)" : h.Surface.ToString();
            rows.Add(new[]
            {
                name, L(p.X), L(p.Y), L(p.Z), N(d.X), N(d.Y), N(d.Z),
                h.Surface == 0 ? "—" : StudyReport.Number(h.AngleOfIncidenceDegrees, "g8"),
                h.Surface == 0 ? "—" : StudyReport.Number(h.AngleOfExitDegrees, "g8"),
                h.Surface == 0 ? "—" : L(h.SegmentLength),
                L(h.OpticalPath),
                RayStatuses.Label(h.Status),
            });
        }
        var final = path.Final;
        var pairs = new List<(string, string)>
        {
            ("Field (Hx, Hy)", $"{N(settings.Hx)}, {N(settings.Hy)}"),
            ("Pupil (Px, Py)", $"{N(settings.Px)}, {N(settings.Py)}"),
            ("Wavelength", $"{StudyReport.Number(trace.AirMicrometers)} µm in air (row {trace.WavelengthIndex + 1}); {StudyReport.Number(trace.VacuumWavelength * 1e6, "g10")} µm in vacuum"),
            ("Status", final.IsOk ? "Reached the image surface" : $"{RayStatuses.Label(final.Status)} at surface {final.FailedAtSurface}"),
            ("Optical path", $"{L(final.OpticalPath)} {u}"),
        };
        var sections = new[]
        {
            new ReportSection("Ray") { Pairs = pairs },
            new ReportSection("Surfaces")
            {
                TableHeaders = new[] { "Surface", $"X ({u})", $"Y ({u})", $"Z ({u})", "L", "M", "N", "Incidence (°)", "Exit (°)", $"Path length ({u})", $"Optical path ({u})", "Status" },
                TableRows = rows,
                Lines = new[]
                {
                    (settings.GlobalCoordinates ? "Positions and direction cosines in surface 1's frame." : "Positions and direction cosines in each surface's local frame.")
                    + " Direction cosines are after the surface; path length is from the previous surface; optical path is from the launch point.",
                    lens.ObjectAtInfinity ? "Object at infinity: the OBJ row is the launch point on the plane through surface 1's vertex at right angles to the ray." : "The OBJ row is the object point.",
                }
            },
        };
        var assumptions = new List<string>
        {
            "Real ray trace of rotationally symmetric conic surfaces (docs/Optics-Conventions.md §13).",
            "Ray aiming is off: the pupil coordinates are on the paraxial entrance pupil of the primary wavelength (ray aiming is OP-22).",
            "Indices and the optical path are relative to the system's air. " + system.Environment.AirModelDescription,
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions);
    }
}
