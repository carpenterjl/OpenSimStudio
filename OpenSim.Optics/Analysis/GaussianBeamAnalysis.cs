using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="GaussianBeamAnalysis"/>. Lengths are millimeters,
/// whatever the lens unit, so a saved beam does not change size when the lens unit does.</summary>
public sealed record GaussianBeamSettings
{
    /// <summary>The wavelength row (1 = the first); 0 is the primary wavelength.</summary>
    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Waist radius x (mm)")]
    [Description("1/e² intensity radius of the beam's waist in x, of the real beam when M² > 1.")]
    public double WaistX { get; init; } = 0.5;

    [DisplayName("Waist position x (mm)")]
    [Description("Signed distance along z from surface 1's vertex to the waist in x; negative in front of surface 1.")]
    public double WaistPositionX { get; init; }

    [DisplayName("M² x")]
    [Description("Beam quality in x, at least 1 (1 is a perfect Gaussian).")]
    public double MSquaredX { get; init; } = 1;

    [DisplayName("Same in y")]
    [Description("Use the x waist, position and M² in y too (a round beam).")]
    public bool SameInY { get; init; } = true;

    [DisplayName("Waist radius y (mm)")]
    public double WaistY { get; init; } = 0.5;

    [DisplayName("Waist position y (mm)")]
    public double WaistPositionY { get; init; }

    [DisplayName("M² y")]
    public double MSquaredY { get; init; } = 1;

    [DisplayName("Plot points per gap")]
    [Description("Intervals the beam-size plot samples in each gap between surfaces.")]
    public int PointsPerGap { get; init; } = 40;

    /// <summary>The input beam these settings describe, in meters.</summary>
    public GaussianBeamInput ToInput()
    {
        const double mm = 1e-3;
        var x = new GaussianBeamPlane(WaistX * mm, WaistPositionX * mm, MSquaredX);
        var y = SameInY ? x : new GaussianBeamPlane(WaistY * mm, WaistPositionY * mm, MSquaredY);
        return new GaussianBeamInput(x, y);
    }
}

/// <summary>
/// The paraxial Gaussian beam (OpticStudio's Paraxial Gaussian Beam analysis; OP-48): a beam
/// given by its waist, the waist's position and M² in x and y, carried surface by surface
/// through the paraxial model (<see cref="GaussianBeamTrace"/>), listed per surface and
/// plotted against the distance along the axis.
/// <para>
/// Like <see cref="ParaxialDataAnalysis"/>, the lens comes from a source given to the
/// constructor; the default reads the project's saved surfaces (<see cref="ProjectLens"/>,
/// OP-31), and the result says so when there are none.
/// </para>
/// </summary>
public sealed class GaussianBeamAnalysis : OpticsAnalysis<GaussianBeamSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "ParaxialGaussianBeam";

    private readonly Func<OpticsAnalysisContext, ParaxialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for the project's saved surfaces (OP-31).</param>
    public GaussianBeamAnalysis(Func<OpticsAnalysisContext, ParaxialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? ProjectLens.Paraxial;
    }

    public override string Id => SavedId;
    public override string Name => "Paraxial Gaussian beam";
    public override string Category => "Beams";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, GaussianBeamSettings settings,
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
                    Lines = new[] { "The system has no sequential surfaces: add them in the Lens Data Editor, so there is something to propagate the beam through." }
                }
            }, new[] { "Nothing is traced." });
        }
        int count = system.Wavelengths.Wavelengths.Count;
        if (settings.Wavelength < 0 || settings.Wavelength > count)
            throw new ArgumentException($"Paraxial Gaussian beam: the wavelength row must be 0 (primary) or 1 to {count} (it is {settings.Wavelength}).");
        if (settings.PointsPerGap < 1 || settings.PointsPerGap > 10000)
            throw new ArgumentException($"Paraxial Gaussian beam: the plot points per gap must be 1 to 10000 (it is {settings.PointsPerGap}).");
        int? row = settings.Wavelength == 0 ? null : settings.Wavelength - 1;
        var model = ParaxialSystem.Create(lens, system, row);
        cancellation.ThrowIfCancellationRequested();
        var trace = GaussianBeamTrace.Create(model, settings.ToInput());
        cancellation.ThrowIfCancellationRequested();
        var profile = trace.Profile(settings.PointsPerGap);

        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        double per = OpticsConventions.MetersPer(unit);
        bool round = trace.Input.IsRound;

        var sections = new List<ReportSection> { InputSection(trace, round, u, per) };
        if (round)
            sections.Add(Table("Paraxial Gaussian Beam", trace.X, lens, u, per));
        else
        {
            sections.Add(Table("Paraxial Gaussian Beam, x", trace.X, lens, u, per));
            sections.Add(Table("Paraxial Gaussian Beam, y", trace.Y, lens, u, per));
        }

        var curves = new List<AnalysisCurve>();
        double[] Scaled(IReadOnlyList<double> v) => v.Select(m => m / per).ToArray();
        var path = Scaled(profile.Path);
        bool embedded = trace.Input.X.MSquared != 1 || trace.Input.Y.MSquared != 1;
        if (round)
        {
            curves.Add(new AnalysisCurve(embedded ? "Beam radius (M²)" : "Beam radius", path, Scaled(profile.RadiusX)));
            if (embedded) curves.Add(new AnalysisCurve("Embedded Gaussian", path, Scaled(profile.EmbeddedRadiusX), Dashed: true));
        }
        else
        {
            curves.Add(new AnalysisCurve("Beam radius x", path, Scaled(profile.RadiusX)));
            curves.Add(new AnalysisCurve("Beam radius y", path, Scaled(profile.RadiusY)));
            if (embedded)
            {
                curves.Add(new AnalysisCurve("Embedded x", path, Scaled(profile.EmbeddedRadiusX), Dashed: true));
                curves.Add(new AnalysisCurve("Embedded y", path, Scaled(profile.EmbeddedRadiusY), Dashed: true));
            }
        }
        var plot = new LinePlot("Beam radius along the axis", $"Distance from surface 1, unfolded ({u})", $"1/e² radius ({u})", curves);

        var assumptions = new List<string>
        {
            "Paraxial Gaussian beam: the complex beam parameter is carried through the vertex curvatures, thicknesses and indices of the paraxial model; apertures, conics, aspheric terms and aberrations do not enter.",
            "Beam radius is the 1/e² intensity radius. With M² > 1 the embedded-Gaussian model is used: the real beam's radius, waist and divergence are √M² times the embedded Gaussian's; the Rayleigh range, wavefront radius and Gouy phase are the embedded beam's.",
            "The wavelength is the table's, in the system's air; in a medium of relative index n it is λ/n.",
            "The x and y planes are propagated independently (simple astigmatism); a generally astigmatic beam is not modelled.",
        };
        assumptions.AddRange(ParaxialReport.Assumptions(model).Skip(1).Take(2));
        assumptions.AddRange(model.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }

    private static ReportSection InputSection(GaussianBeamTrace trace, bool round, string u, double per)
    {
        string L(double meters) => $"{StudyReport.Number(meters / per)} {u}";
        var pairs = new List<(string, string)>
        {
            ("Wavelength", $"{StudyReport.Number(trace.Model.WavelengthMicrometers)} µm (row {trace.Model.WavelengthIndex + 1}, in the system's air)"),
            ("Object space index", StudyReport.Number(trace.Model.Indices[0])),
        };
        void Plane(string suffix, GaussianBeamPlane p, GaussianBeamState s)
        {
            pairs.Add(($"Waist radius{suffix}", L(p.WaistRadius)));
            pairs.Add(($"Waist position{suffix} (from surface 1)", L(p.WaistPosition)));
            pairs.Add(($"M²{suffix}", StudyReport.Number(p.MSquared)));
            pairs.Add(($"Rayleigh range{suffix}", L(s.RayleighRange)));
            pairs.Add(($"Divergence{suffix} (half angle)", $"{StudyReport.Number(s.Divergence)} rad"));
        }
        if (round) Plane("", trace.Input.X, trace.X[0]);
        else
        {
            Plane(" x", trace.Input.X, trace.X[0]);
            Plane(" y", trace.Input.Y, trace.Y[0]);
        }
        return new ReportSection("Input beam") { Pairs = pairs };
    }

    private static ReportSection Table(string heading, IReadOnlyList<GaussianBeamState> states, ParaxialLens lens, string u, double per)
    {
        string L(double meters) => double.IsInfinity(meters) ? "infinite" : StudyReport.Number(meters / per);
        var rows = new List<IReadOnlyList<string>>();
        for (int j = 0; j < states.Count; j++)
        {
            var s = states[j];
            string name = j == 0 ? "In (before 1)" : j == lens.ImageIndex ? "IMA" : j == lens.StopIndex ? $"{j} (stop)" : j.ToString();
            rows.Add(new[]
            {
                name, L(s.BeamRadius), L(s.WaistRadius), L(s.WaistOffset), L(s.RayleighRange),
                StudyReport.Number(s.Divergence), L(s.WavefrontRadius), StudyReport.Number(s.GouyPhase * (180 / Math.PI)),
            });
        }
        return new ReportSection(heading)
        {
            TableHeaders = new[]
            {
                "Surface", $"Beam radius ({u})", $"Waist radius ({u})", $"Waist position ({u})", $"Rayleigh range ({u})",
                "Divergence (rad)", $"Wavefront radius ({u})", "Gouy phase (°)",
            },
            TableRows = rows,
            Lines = new[]
            {
                "Each row is the beam just after the surface (the first row arriving at surface 1, the last arriving at the image surface).",
                "Waist position: signed distance along z from the surface's vertex to the waist of the beam in the space after it (negative toward −z). Wavefront radius: positive when diverging. Gouy phase: accumulated from surface 1.",
            }
        };
    }
}
