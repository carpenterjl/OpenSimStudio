using System.ComponentModel;
using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Polarization;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// ---------------------------------------------------------------------------------------------
// Settings (OP-51). Each analysis takes its own Jones vector, as OpticStudio's do (its help pages
// of the same names); the reference rule and the thin-film phase conversion come from the
// system polarization.
// ---------------------------------------------------------------------------------------------

/// <summary>Settings of the <see cref="PolarizationRayTraceAnalysis"/>.</summary>
public sealed record PolarizationRayTraceSettings
{
    [DisplayName("Jx"), Description("Field magnitude along S (normalised with Jy).")]
    public double Jx { get; init; } = 1;
    [DisplayName("Jy"), Description("Field magnitude along P.")]
    public double Jy { get; init; }
    [DisplayName("X-phase (°)"), Description("Phase of Jx, OpticStudio's convention.")]
    public double XPhaseDegrees { get; init; }
    [DisplayName("Y-phase (°)"), Description("Phase of Jy, OpticStudio's convention.")]
    public double YPhaseDegrees { get; init; }
    [DisplayName("Hx")] public double Hx { get; init; }
    [DisplayName("Hy")] public double Hy { get; init; }
    [DisplayName("Px"), Description("Normalised pupil x (paraxial entrance pupil; ray aiming is not built).")]
    public double Px { get; init; }
    [DisplayName("Py"), Description("Normalised pupil y (paraxial entrance pupil; ray aiming is not built).")]
    public double Py { get; init; }
    [DisplayName("Wavelength"), Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }
    [DisplayName("Global coordinates"), Description("List fields in surface 1's frame instead of each surface's own.")]
    public bool GlobalCoordinates { get; init; }
    [DisplayName("Bulk absorption"), Description("Apply the internal transmittance of the glasses.")]
    public bool BulkAbsorption { get; init; } = true;
}

/// <summary>Settings of the <see cref="PolarizationPupilMapAnalysis"/>.</summary>
public sealed record PolarizationPupilMapSettings
{
    [DisplayName("Jx")] public double Jx { get; init; } = 1;
    [DisplayName("Jy")] public double Jy { get; init; }
    [DisplayName("X-phase (°)")] public double XPhaseDegrees { get; init; }
    [DisplayName("Y-phase (°)")] public double YPhaseDegrees { get; init; }
    [DisplayName("Field"), Description("Field number, 1 for the first.")]
    public int Field { get; init; } = 1;
    [DisplayName("Wavelength"), Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }
    [DisplayName("Surface"), Description("The surface whose field is shown (after it); 0 for the image surface.")]
    public int Surface { get; init; }
    [DisplayName("Sampling"), Description("Points across the pupil (square grid, points inside the unit circle kept).")]
    public int Sampling { get; init; } = 11;
    [DisplayName("Bulk absorption")] public bool BulkAbsorption { get; init; } = true;
}

/// <summary>Settings of the <see cref="PolarizationTransmissionAnalysis"/>.</summary>
public sealed record PolarizationTransmissionSettings
{
    [DisplayName("Jx")] public double Jx { get; init; } = 1;
    [DisplayName("Jy")] public double Jy { get; init; }
    [DisplayName("X-phase (°)")] public double XPhaseDegrees { get; init; }
    [DisplayName("Y-phase (°)")] public double YPhaseDegrees { get; init; }
    [DisplayName("Unpolarized"), Description("Ignore the Jones vector; average (1, 0) and (0, 1).")]
    public bool Unpolarized { get; init; }
    [DisplayName("Sampling"), Description("Points across the pupil (square grid, points inside the unit circle kept).")]
    public int Sampling { get; init; } = 16;
    [DisplayName("Bulk absorption")] public bool BulkAbsorption { get; init; } = true;
}

/// <summary>Settings of the <see cref="PolarizationPhaseAberrationAnalysis"/>.</summary>
public sealed record PolarizationPhaseAberrationSettings
{
    [DisplayName("Jx")] public double Jx { get; init; } = 1;
    [DisplayName("Jy")] public double Jy { get; init; }
    [DisplayName("X-phase (°)")] public double XPhaseDegrees { get; init; }
    [DisplayName("Y-phase (°)")] public double YPhaseDegrees { get; init; }
    [DisplayName("Field"), Description("Field number, 1 for the first.")]
    public int Field { get; init; } = 1;
    [DisplayName("Wavelength"), Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }
    [DisplayName("Points"), Description("Rays along each fan, from −1 to 1.")]
    public int Points { get; init; } = 21;
    [DisplayName("Bulk absorption")] public bool BulkAbsorption { get; init; } = true;
}

/// <summary>Settings of the <see cref="PolarizationTransmissionFanAnalysis"/>.</summary>
public sealed record PolarizationTransmissionFanSettings
{
    [DisplayName("Jx")] public double Jx { get; init; } = 1;
    [DisplayName("Jy")] public double Jy { get; init; }
    [DisplayName("X-phase (°)")] public double XPhaseDegrees { get; init; }
    [DisplayName("Y-phase (°)")] public double YPhaseDegrees { get; init; }
    [DisplayName("Unpolarized"), Description("Ignore the Jones vector; average (1, 0) and (0, 1).")]
    public bool Unpolarized { get; init; }
    [DisplayName("Field"), Description("Field number, 1 for the first; 0 for all.")]
    public int Field { get; init; }
    [DisplayName("Wavelength"), Description("Row of the wavelength table, 1 for the first; 0 for all.")]
    public int Wavelength { get; init; }
    [DisplayName("Points"), Description("Rays along each fan, from −1 to 1.")]
    public int Points { get; init; } = 21;
    [DisplayName("Bulk absorption")] public bool BulkAbsorption { get; init; } = true;
}

// ---------------------------------------------------------------------------------------------
// Shared plumbing.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// What the five polarization analyses share: the lens source (no saved surfaces until OP-31,
/// as for <see cref="SingleRayTraceAnalysis"/>), the system and coating catalog of the snapshot,
/// one <see cref="PolarizationTracer"/> per wavelength, and the formatting of fields and phases.
/// Phases are reported in OpticStudio's convention (−arg of this program's amplitude,
/// docs/Optics-Conventions.md §12 and §21).
/// </summary>
public abstract class PolarizationAnalysisBase<TSettings> : OpticsAnalysis<TSettings> where TSettings : class, new()
{
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    protected PolarizationAnalysisBase(Func<OpticsAnalysisContext, SequentialLens?>? lensSource) =>
        _lensSource = lensSource ?? (_ => null);

    public override string Category => "Polarization";

    /// <summary>Everything a compute needs, resolved once.</summary>
    protected sealed record Setup(SequentialLens Lens, OpticalSystemData System, CoatingCatalog Catalog, RayLauncher Launcher,
        IReadOnlyList<double>? SemiDiameters, List<string> Notes, PolarizationOptions Options)
    {
        public PolarizationTracer Tracer(int row) =>
            new(SequentialTrace.Create(Lens, System, row, SemiDiameters), Catalog, Options);
    }

    /// <summary>The setup, or null when the system has no surfaces.</summary>
    protected Setup? Prepare(OpticsAnalysisContext context, bool bulkAbsorption, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return null;
        var catalog = CoatingCatalogSettings.FromSettings(context.System.Coatings, notes);
        IReadOnlyList<double>? semiDiameters = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semiDiameters = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
        return new Setup(lens, system, catalog, new RayLauncher(lens, system), semiDiameters, notes,
            PolarizationOptions.From(system.Polarization, bulkAbsorption));
    }

    protected OpticsAnalysisResult NoSurfaces() => OpticsAnalysisResult.FromSections(Name, new[]
    {
        new ReportSection("No surfaces")
        {
            Lines = new[] { "The system has no sequential surfaces yet: the lens data editor is a later plan item (OP-31), so there is nothing to trace." }
        }
    }, new[] { "Nothing is traced." });

    /// <summary>0-based wavelength row from a setting where 0 is the primary.</summary>
    protected static int Row(int setting, OpticalSystemData system, string who)
    {
        int count = system.Wavelengths.Wavelengths.Count;
        if (setting < 0 || setting > count)
            throw new ArgumentException($"{who}: the wavelength row must be 0 (primary) or 1 to {count} (it is {setting}).");
        return setting == 0 ? system.Wavelengths.PrimaryIndex : setting - 1;
    }

    /// <summary>0-based field from a 1-based setting.</summary>
    protected static int FieldIndex(int setting, OpticalSystemData system, string who)
    {
        int count = system.Fields.Points.Count;
        if (setting < 1 || setting > count)
            throw new ArgumentException($"{who}: the field must be 1 to {count} (it is {setting}).");
        return setting - 1;
    }

    protected static JonesVector Jones(double jx, double jy, double xPhase, double yPhase, string who)
    {
        try { return JonesVector.FromAmplitudes(jx, jy, xPhase, yPhase); }
        catch (ArgumentException e) { throw new ArgumentException($"{who}: {e.Message}", e); }
    }

    protected static string N(double v, string format = "g6") => StudyReport.Number(v, format);

    /// <summary>The phase in OpticStudio's convention, degrees, of an amplitude in ours.</summary>
    protected static double PhaseDegrees(Complex z) => z == Complex.Zero ? 0 : -z.Phase * 180 / Math.PI;

    /// <summary>"|z| ∠ φ°", φ in OpticStudio's convention.</summary>
    protected static string C(Complex z) => $"{N(z.Magnitude)} ∠ {N(PhaseDegrees(z), "f2")}°";

    protected List<string> CommonAssumptions(Setup setup)
    {
        var list = new List<string>
        {
            "Polarization ray trace with three-dimensional polarization matrices (docs/Optics-Conventions.md §21): Fresnel or coating amplitudes in each surface's s/p frame, bulk absorption from the glasses' internal transmittance"
                + (setup.Options.BulkAbsorption ? "." : " (off for this analysis)."),
            $"Launch field from the Jones vector by the {PolarizationReferences.Names.Label(setup.Options.Reference).ToLowerInvariant()} rule; coating phases {(setup.Options.ConvertThinFilmPhase ? "converted to the ray convention" : "in the thin-film (field) convention")}.",
            "Phases are in OpticStudio's convention (−arg of this program's e^(−iωt) amplitude); the propagation phase is not included.",
            "Ray aiming, vignetting factors and pupil apodization are not applied (OP-22).",
            "Indices are relative to the system's air. " + setup.System.Environment.AirModelDescription,
        };
        list.AddRange(setup.Notes);
        return list;
    }

    /// <summary>Evenly spaced fan coordinates from −1 to 1.</summary>
    protected static double[] Fan(int points, string who)
    {
        if (points < 2 || points > 10_001)
            throw new ArgumentException($"{who}: the number of points must lie in 2–10001 (it is {points}).");
        var p = new double[points];
        for (int i = 0; i < points; i++) p[i] = i == points - 1 ? 1 : -1 + 2.0 * i / (points - 1);
        if (points % 2 == 1) p[points / 2] = 0;
        return p;
    }

    /// <summary>
    /// OP-52: an analysis that needs the field (its phase or its ellipse) refuses a surface at or
    /// after a Mueller matrix surface, where the ray carries a Stokes vector and has no single
    /// field (docs/Optics-Conventions.md §30). Throws naming the Mueller surface.
    /// </summary>
    protected static void RefuseAfterMueller(SequentialLens lens, int surface, string who)
    {
        for (int j = 1; j <= Math.Min(surface, lens.ImageIndex - 1); j++)
            if (lens.Surfaces[j].Shape is MuellerMatrixShape && !lens.Surfaces[j].IsIgnored)
                throw new InvalidOperationException(
                    $"{who}: surface {j} is a {MuellerMatrixShape.Name} surface, so from there on a ray carries a Stokes vector (partially polarized light), " +
                    "which has no single field to plot. Use the Polarization ray trace (Stokes vector and degree of polarization) or the transmission analyses, " +
                    "or evaluate a surface before it.");
    }

    /// <summary>Transmission of one launched ray: polarized or the mean of two orthogonal states.</summary>
    protected static double Transmission(PolarizationTracer tracer, RayPath path, bool unpolarized, JonesVector jones) =>
        !path.Final.IsOk ? 0
        : unpolarized ? (tracer.TracePath(path, JonesVector.AlongS).Transmission + tracer.TracePath(path, JonesVector.AlongP).Transmission) / 2
        : tracer.TracePath(path, jones).Transmission;
}

// ---------------------------------------------------------------------------------------------
// The five analyses.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One ray's polarization surface by surface (OpticStudio's Polarization Ray Trace): the launch
/// vectors and field, then at every surface the angle of incidence, the bulk internal
/// transmittance of the segment, the coating's intensity coefficients, the amplitude
/// coefficients applied, the field after the surface and the relative and total transmission;
/// then the system's polarization matrix and its diattenuation.
/// </summary>
public sealed class PolarizationRayTraceAnalysis : PolarizationAnalysisBase<PolarizationRayTraceSettings>
{
    public const string SavedId = "PolarizationRayTrace";

    public PolarizationRayTraceAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Polarization ray trace";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, PolarizationRayTraceSettings settings,
        CancellationToken cancellation)
    {
        var setup = Prepare(context, settings.BulkAbsorption, cancellation);
        if (setup is null) return NoSurfaces();
        var jones = Jones(settings.Jx, settings.Jy, settings.XPhaseDegrees, settings.YPhaseDegrees, Name);
        var tracer = setup.Tracer(Row(settings.Wavelength, setup.System, Name));
        var trace = tracer.Trace;
        cancellation.ThrowIfCancellationRequested();
        var result = tracer.TraceRay(setup.Launcher.Launch(trace, settings.Hx, settings.Hy, settings.Px, settings.Py), jones);
        var lens = setup.Lens;

        RayField Shown(PolarizedSurface s, RayField global) => settings.GlobalCoordinates ? global : s.LocalField(trace, global);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var s in result.Surfaces)
        {
            var e = Shown(s, s.FieldOut);
            bool stokes = s.Coherency is not null;   // OP-52: no single field after a Mueller surface
            string name = s.Surface == lens.ImageIndex ? "IMA" : s.Surface == lens.StopIndex ? $"{s.Surface} (stop)" : s.Surface.ToString();
            bool mirror = lens.Surfaces[s.Surface].IsMirror;
            rows.Add(new[]
            {
                name,
                s.IsInterface ? (s.CoatingName ?? (mirror ? "(aluminium)" : "(bare)")) : "—",
                N(s.Hit.AngleOfIncidenceDegrees, "g8"),
                N(s.BulkTransmittance, "g8"),
                s.Response is { } r ? N(mirror ? r.S.R : r.S.T, "g8") : "—",
                s.Response is { } r2 ? N(mirror ? r2.P.R : r2.P.T, "g8") : "—",
                s.IsInterface ? C(s.AmplitudeS) : "—",
                s.IsInterface ? C(s.AmplitudeP) : "—",
                stokes ? "—" : C(e.X), stokes ? "—" : C(e.Y), stokes ? "—" : C(e.Z),
                N(stokes ? s.TotalTransmission : s.FieldOut.NormSquared, "g8"),
                N(s.RelativeTransmission, "g8"),
                N(s.TotalTransmission, "g8"),
                RayStatuses.Label(s.Hit.Status),
            });
        }
        var k0 = result.Path.Hits[0].Direction;
        var final = result.Final;
        var pairs = new List<(string, string)>
        {
            ("Field (Hx, Hy)", $"{N(settings.Hx)}, {N(settings.Hy)}"),
            ("Pupil (Px, Py)", $"{N(settings.Px)}, {N(settings.Py)}"),
            ("Wavelength", $"{N(trace.AirMicrometers)} µm in air (row {trace.WavelengthIndex + 1})"),
            ("Jones vector", jones.ToString()),
            ("Launch S", V(result.LaunchS)),
            ("Launch P", V(result.LaunchP)),
            ("Launch field (Ex, Ey, Ez)", $"{C(result.LaunchField.X)}; {C(result.LaunchField.Y)}; {C(result.LaunchField.Z)}"),
            ("Status", final.IsOk ? "Reached the image surface" : $"{RayStatuses.Label(final.Status)} at surface {final.FailedAtSurface}"),
            ("Transmission", N(result.Transmission, "g10")),
            ("System diattenuation", result.MuellerSurface is { } mu
                ? $"not defined: surface {mu} is a {MuellerMatrixShape.Name} surface" : N(result.Cumulative.Diattenuation(k0), "g8")),
        };
        // OP-52: the Stokes vector at the last surface reached (docs/Optics-Conventions.md §30).
        var stokesFinal = result.FinalStokes;
        pairs.Add(("Stokes (I, Q, U, V), last surface's local x, y", $"{N(stokesFinal.I, "g8")}, {N(stokesFinal.Q, "g8")}, {N(stokesFinal.U, "g8")}, {N(stokesFinal.V, "g8")}"));
        pairs.Add(("Degree of polarization", Degree(stokesFinal.DegreeOfPolarization)));
        pairs.Add(("Degree of linear / circular polarization", $"{Degree(stokesFinal.DegreeOfLinearPolarization)} / {Degree(stokesFinal.DegreeOfCircularPolarization)}"));
        var matrixRows = new List<IReadOnlyList<string>>();
        var m = result.Cumulative;
        if (result.MuellerSurface is null)
            for (int r = 0; r < 3; r++)
                matrixRows.Add(new[] { "xyz"[r].ToString(), C(m[r, 0]), C(m[r, 1]), C(m[r, 2]) });
        var sections = new[]
        {
            new ReportSection("Ray") { Pairs = pairs },
            new ReportSection("Surfaces")
            {
                TableHeaders = new[] { "Surface", "Coating", "Incidence (°)", "Bulk T", "T or R (s)", "T or R (p)", "a_s", "a_p", "Ex", "Ey", "Ez", "Intensity", "Rel. T", "Tot. T", "Status" },
                TableRows = rows,
                Lines = new[]
                {
                    (settings.GlobalCoordinates ? "Fields in surface 1's frame." : "Fields in each surface's local frame.")
                    + " Fields are after the surface; Bulk T is the internal transmittance of the segment arriving; T or R is the coating's transmittance (reflectance on a mirror); a_s and a_p are the power-normalised amplitude coefficients applied.",
                    "Rel. T is the bulk transmittance of the material before the surface times the surface's transmission; Tot. T is the power left of the launched unit power.",
                }
            },
            new ReportSection("System polarization matrix (surface 1's frame)")
            {
                TableHeaders = new[] { "Row", "x", "y", "z" }, TableRows = matrixRows,
                Lines = result.MuellerSurface is { } mueller
                    ? new[] { $"Not defined: surface {mueller} is a {MuellerMatrixShape.Name} surface, after which the ray carries a Stokes vector (coherency matrix), not a field." }
                    : new[] { "E at the last surface reached = P × launch E. Entries |z| ∠ phase." }
            },
        };
        return OpticsAnalysisResult.FromSections(Name, sections, CommonAssumptions(setup));
    }

    private static string V(Vector3D v) => $"({N(v.X, "g8")}, {N(v.Y, "g8")}, {N(v.Z, "g8")})";

    private static string Degree(double d) => double.IsNaN(d) ? "— (no light)" : N(d, "g8");
}

/// <summary>
/// The polarization ellipse over the pupil at one surface (OpticStudio's Polarization Pupil
/// Map): one ray per grid point; the ellipse traced by Re(E·e^(−iωt)) of the field's x and y
/// components in the surface's local frame (Ez left out, as OpticStudio leaves it out), drawn
/// at the pupil point and scaled so the largest fits half a grid step. The text lists |Ex|, |Ey|,
/// |Ex|² + |Ey|², the phase of Ey minus that of Ex, and the orientation of the major axis,
/// ψ = ½·atan2(2·Re(Ex·Ey*), |Ex|² − |Ey|²) (derived: the principal axis of the time-averaged
/// matrix ½·Re(E E^H)). The transmission is the mean of |E|² (Ez included) over the grid,
/// a ray that does not reach the surface counting zero.
/// </summary>
public sealed class PolarizationPupilMapAnalysis : PolarizationAnalysisBase<PolarizationPupilMapSettings>
{
    public const string SavedId = "PolarizationPupilMap";

    public PolarizationPupilMapAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Polarization pupil map";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, PolarizationPupilMapSettings settings,
        CancellationToken cancellation)
    {
        var setup = Prepare(context, settings.BulkAbsorption, cancellation);
        if (setup is null) return NoSurfaces();
        var jones = Jones(settings.Jx, settings.Jy, settings.XPhaseDegrees, settings.YPhaseDegrees, Name);
        int field = FieldIndex(settings.Field, setup.System, Name);
        var tracer = setup.Tracer(Row(settings.Wavelength, setup.System, Name));
        int image = setup.Lens.ImageIndex;
        int surface = settings.Surface == 0 ? image : settings.Surface;
        if (surface < 1 || surface > image)
            throw new ArgumentException($"{Name}: the surface must be 1 to {image}, or 0 for the image (it is {settings.Surface}).");
        if (settings.Sampling < 1 || settings.Sampling > 257)
            throw new ArgumentException($"{Name}: the sampling must lie in 1–257 (it is {settings.Sampling}).");
        var grid = BatchTracer.SquareGrid(settings.Sampling);
        var (hx, hy) = setup.System.Fields.Normalized(field);
        RefuseAfterMueller(setup.Lens, surface, Name);

        var fields = new (double Px, double Py, RayField? E)[grid.Count];
        double sum = 0;
        for (int i = 0; i < grid.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var (px, py) = grid[i];
            var path = tracer.TraceRay(setup.Launcher.Launch(tracer.Trace, hx, hy, px, py), jones);
            var at = path.Surfaces.FirstOrDefault(s => s.Surface == surface);
            bool reached = at is not null && (at.Hit.Status == RayStatus.Ok);
            RayField? e = reached ? at!.LocalField(tracer.Trace, at.FieldOut) : null;
            if (e is { } ok) sum += at!.FieldOut.NormSquared;
            fields[i] = (px, py, e);
        }
        double transmission = grid.Count > 0 ? sum / grid.Count : 0;

        double maxAmplitude = 0;
        foreach (var f in fields)
            if (f.E is { } e) maxAmplitude = Math.Max(maxAmplitude, Math.Sqrt(Sq(e.X) + Sq(e.Y)));
        double size = 0.9 / settings.Sampling;    // half the grid step 2/n, less a margin
        var xs = new List<double>();
        var ys = new List<double>();
        var rows = new List<IReadOnlyList<string>>();
        const int segments = 48;
        foreach (var (px, py, e) in fields)
        {
            if (e is not { } E)
            {
                rows.Add(new[] { N(px), N(py), "—", "—", "—", "—", "—" });
                continue;
            }
            double ix = Sq(E.X), iy = Sq(E.Y);
            double phase = E.X == Complex.Zero || E.Y == Complex.Zero ? 0
                : CoatingResponse.Wrap((PhaseDegrees(E.Y) - PhaseDegrees(E.X)) * Math.PI / 180) * 180 / Math.PI;
            Complex cross = E.X * Complex.Conjugate(E.Y);
            double orientation = 0.5 * Math.Atan2(2 * cross.Real, ix - iy) * 180 / Math.PI;
            rows.Add(new[] { N(px), N(py), N(E.X.Magnitude), N(E.Y.Magnitude), N(ix + iy), N(phase, "f3"), N(orientation, "f3") });
            if (maxAmplitude > 0)
            {
                for (int t = 0; t <= segments; t++)
                {
                    double wt = 2 * Math.PI * t / segments;
                    var phasor = Complex.FromPolarCoordinates(1, -wt);
                    xs.Add(px + size * (E.X * phasor).Real / maxAmplitude);
                    ys.Add(py + size * (E.Y * phasor).Real / maxAmplitude);
                }
                xs.Add(double.NaN);
                ys.Add(double.NaN);
            }
        }
        var rim = Enumerable.Range(0, 129).Select(i => 2 * Math.PI * i / 128).ToArray();
        var plot = new LinePlot($"Polarization pupil map, field {field + 1}, surface {(surface == image ? "IMA" : surface.ToString())}",
            "Px (ellipses: local x)", "Py (ellipses: local y)", new[]
            {
                new AnalysisCurve("Polarization ellipse", xs, ys),
                new AnalysisCurve("Pupil edge", rim.Select(Math.Cos).ToArray(), rim.Select(Math.Sin).ToArray(), Dashed: true),
            });
        var sections = new[]
        {
            new ReportSection("Pupil map")
            {
                Pairs = new (string, string)[]
                {
                    ("Field", $"{field + 1} (Hx {N(hx)}, Hy {N(hy)})"),
                    ("Wavelength", $"{N(tracer.Trace.AirMicrometers)} µm in air"),
                    ("Surface", surface == image ? "Image" : surface.ToString()),
                    ("Jones vector", jones.ToString()),
                    ("Transmission", N(transmission, "g8")),
                }
            },
            new ReportSection("Rays")
            {
                TableHeaders = new[] { "Px", "Py", "|Ex|", "|Ey|", "Ex² + Ey²", "Phase y − x (°)", "Orientation (°)" },
                TableRows = rows,
                Lines = new[] { "Field components in the surface's local frame, after the surface; a dash marks a ray that did not reach it. The intensity column leaves Ez out; the transmission includes it." }
            },
        };
        return OpticsAnalysisResult.FromSections(Name, sections, CommonAssumptions(setup), new AnalysisPlot[] { plot });
    }

    private static double Sq(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;
}

/// <summary>
/// Integrated and surface-by-surface transmission (OpticStudio's Transmission): for every field
/// and wavelength the mean transmission of a square pupil grid, a ray that fails (aperture,
/// miss, total internal reflection) counting zero, and the chief ray's relative and total
/// transmission at each surface. Unpolarized: the mean of the (1, 0) and (0, 1) traces.
/// </summary>
public sealed class PolarizationTransmissionAnalysis : PolarizationAnalysisBase<PolarizationTransmissionSettings>
{
    public const string SavedId = "PolarizationTransmission";

    public PolarizationTransmissionAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Transmission";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, PolarizationTransmissionSettings settings,
        CancellationToken cancellation)
    {
        var setup = Prepare(context, settings.BulkAbsorption, cancellation);
        if (setup is null) return NoSurfaces();
        var jones = settings.Unpolarized ? JonesVector.AlongS : Jones(settings.Jx, settings.Jy, settings.XPhaseDegrees, settings.YPhaseDegrees, Name);
        if (settings.Sampling < 1 || settings.Sampling > 257)
            throw new ArgumentException($"{Name}: the sampling must lie in 1–257 (it is {settings.Sampling}).");
        var grid = BatchTracer.SquareGrid(settings.Sampling);
        var system = setup.System;
        var lens = setup.Lens;
        var summary = new List<IReadOnlyList<string>>();
        var sections = new List<ReportSection>();
        for (int w = 0; w < system.Wavelengths.Wavelengths.Count; w++)
        {
            var tracer = setup.Tracer(w);
            for (int f = 0; f < system.Fields.Points.Count; f++)
            {
                var (hx, hy) = system.Fields.Normalized(f);
                double sum = 0;
                foreach (var (px, py) in grid)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = tracer.Trace.TracePath(setup.Launcher.Launch(tracer.Trace, hx, hy, px, py));
                    sum += Transmission(tracer, path, settings.Unpolarized, jones);
                }
                double integrated = sum / grid.Count;
                summary.Add(new[] { (f + 1).ToString(), N(tracer.Trace.AirMicrometers), N(100 * integrated, "f4") });

                var chief = tracer.Trace.TracePath(setup.Launcher.Launch(tracer.Trace, hx, hy, 0, 0));
                var states = settings.Unpolarized
                    ? new[] { tracer.TracePath(chief, JonesVector.AlongS), tracer.TracePath(chief, JonesVector.AlongP) }
                    : new[] { tracer.TracePath(chief, jones) };
                var rows = new List<IReadOnlyList<string>>();
                double previous = 1;
                for (int i = 0; i < states[0].Surfaces.Count; i++)
                {
                    double total = states.Average(s => s.Surfaces[i].TotalTransmission);
                    int j = states[0].Surfaces[i].Surface;
                    rows.Add(new[]
                    {
                        j == lens.ImageIndex ? "IMA" : j.ToString(),
                        N(previous > 0 ? total / previous : 0, "f6"),
                        N(total, "f6"),
                    });
                    previous = total;
                }
                sections.Add(new ReportSection($"Chief ray, field {f + 1}, {N(tracer.Trace.AirMicrometers)} µm")
                {
                    TableHeaders = new[] { "Surface", "Rel. tran", "Tot. tran" },
                    TableRows = rows,
                    Lines = chief.Final.IsOk ? Array.Empty<string>()
                        : new[] { $"The chief ray failed: {RayStatuses.Label(chief.Final.Status)} at surface {chief.Final.FailedAtSurface}." },
                });
            }
        }
        sections.Insert(0, new ReportSection("Integrated transmission")
        {
            Pairs = new (string, string)[]
            {
                ("Polarization", settings.Unpolarized ? "Unpolarized (mean of (1, 0) and (0, 1))" : jones.ToString()),
                ("Pupil grid", $"{settings.Sampling} × {settings.Sampling}, {grid.Count} rays inside the pupil"),
            },
            TableHeaders = new[] { "Field", "Wavelength (µm)", "Transmission (%)" },
            TableRows = summary,
            Lines = new[] { "100% is what would arrive with no absorption, reflection or vignetting. Rays that fail count zero." }
        });
        return OpticsAnalysisResult.FromSections(Name, sections, CommonAssumptions(setup));
    }
}

/// <summary>
/// The polarization phase aberration (OpticStudio's Phase Aberration): the phase of the x and
/// y components of the image-space field (image surface's local frame) along the tangential
/// (Py) and sagittal (Px) fans, minus the chief ray's, in waves wrapped to (−½, ½]. When a
/// component of the chief ray is too weak to have a phase (|E_c|² below 10⁻²⁴ of the ray's
/// power), its phase is that of the sum of the unit phasors of the two fan rays nearest the
/// centre, as OpticStudio's help describes averaging two rays on either side. A ray that fails,
/// or whose component is zero, leaves a gap.
/// </summary>
public sealed class PolarizationPhaseAberrationAnalysis : PolarizationAnalysisBase<PolarizationPhaseAberrationSettings>
{
    public const string SavedId = "PolarizationPhaseAberration";

    public PolarizationPhaseAberrationAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Phase aberration";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, PolarizationPhaseAberrationSettings settings,
        CancellationToken cancellation)
    {
        var setup = Prepare(context, settings.BulkAbsorption, cancellation);
        if (setup is null) return NoSurfaces();
        var jones = Jones(settings.Jx, settings.Jy, settings.XPhaseDegrees, settings.YPhaseDegrees, Name);
        int field = FieldIndex(settings.Field, setup.System, Name);
        var tracer = setup.Tracer(Row(settings.Wavelength, setup.System, Name));
        var fan = Fan(settings.Points, Name);
        var (hx, hy) = setup.System.Fields.Normalized(field);
        RefuseAfterMueller(setup.Lens, setup.Lens.ImageIndex, Name);

        RayField? ImageField(double px, double py)
        {
            cancellation.ThrowIfCancellationRequested();
            var p = tracer.TraceRay(setup.Launcher.Launch(tracer.Trace, hx, hy, px, py), jones);
            return p.Final.IsOk ? p.Final.Field : null;
        }
        var chief = ImageField(0, 0) ?? throw new InvalidOperationException($"{Name}: the chief ray of field {field + 1} does not reach the image surface.");
        var tangential = fan.Select(p => ImageField(0, p)).ToArray();
        var sagittal = fan.Select(p => ImageField(p, 0)).ToArray();

        var plots = new List<AnalysisPlot>();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var (label, fields) in new[] { ("Tangential (Py)", tangential), ("Sagittal (Px)", sagittal) })
        {
            var ex = Relative(fields, chief, e => e.X, fan);
            var ey = Relative(fields, chief, e => e.Y, fan);
            plots.Add(new LinePlot($"Phase aberration, {label.ToLowerInvariant()}, field {field + 1}", label, "Waves",
                new[] { new AnalysisCurve("Ex", fan, ex), new AnalysisCurve("Ey", fan, ey, Dashed: true) }));
            for (int i = 0; i < fan.Length; i++)
                rows.Add(new[] { label, N(fan[i]), double.IsNaN(ex[i]) ? "—" : N(ex[i], "f6"), double.IsNaN(ey[i]) ? "—" : N(ey[i], "f6") });
        }
        var sections = new[]
        {
            new ReportSection("Phase aberration")
            {
                Pairs = new (string, string)[]
                {
                    ("Field", $"{field + 1} (Hx {N(hx)}, Hy {N(hy)})"),
                    ("Wavelength", $"{N(tracer.Trace.AirMicrometers)} µm in air"),
                    ("Jones vector", jones.ToString()),
                    ("Chief ray Ex, Ey", $"{C(chief.X)}; {C(chief.Y)}"),
                },
                TableHeaders = new[] { "Fan", "Pupil", "Ex (waves)", "Ey (waves)" },
                TableRows = rows,
                Lines = new[] { "Phase of each field component at the image (local frame) minus the chief ray's, OpticStudio's convention, wrapped to (−½, ½] wave. A dash marks a failed ray or a zero component." }
            },
        };
        return OpticsAnalysisResult.FromSections(Name, sections, CommonAssumptions(setup), plots);
    }

    private static double[] Relative(RayField?[] fields, RayField chief, Func<RayField, Complex> component, double[] fan)
    {
        double power = chief.NormSquared;
        var c = component(chief);
        double reference;
        if (c.Magnitude * c.Magnitude > 1e-24 * power) reference = -c.Phase;
        else
        {
            // The two rays nearest the centre, one on either side.
            Complex sum = Complex.Zero;
            int left = -1, right = -1;
            for (int i = 0; i < fan.Length; i++)
            {
                if (fan[i] < 0 && (left < 0 || fan[i] > fan[left])) left = i;
                if (fan[i] > 0 && (right < 0 || fan[i] < fan[right])) right = i;
            }
            foreach (int i in new[] { left, right })
                if (i >= 0 && fields[i] is { } e && component(e) != Complex.Zero) sum += component(e) / component(e).Magnitude;
            if (sum == Complex.Zero) return fields.Select(_ => double.NaN).ToArray();
            reference = -sum.Phase;
        }
        return fields.Select(f =>
        {
            if (f is not { } e || component(e) == Complex.Zero) return double.NaN;
            return CoatingResponse.Wrap(-component(e).Phase - reference) / (2 * Math.PI);
        }).ToArray();
    }
}

/// <summary>
/// Transmission along the tangential (Py) and sagittal (Px) pupil fans for each field and
/// wavelength (OpticStudio's Transmission Fan). A ray that fails transmits zero.
/// </summary>
public sealed class PolarizationTransmissionFanAnalysis : PolarizationAnalysisBase<PolarizationTransmissionFanSettings>
{
    public const string SavedId = "PolarizationTransmissionFan";

    public PolarizationTransmissionFanAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Transmission fan";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, PolarizationTransmissionFanSettings settings,
        CancellationToken cancellation)
    {
        var setup = Prepare(context, settings.BulkAbsorption, cancellation);
        if (setup is null) return NoSurfaces();
        var jones = settings.Unpolarized ? JonesVector.AlongS : Jones(settings.Jx, settings.Jy, settings.XPhaseDegrees, settings.YPhaseDegrees, Name);
        var system = setup.System;
        var fan = Fan(settings.Points, Name);
        var fieldList = settings.Field == 0 ? Enumerable.Range(0, system.Fields.Points.Count).ToArray() : new[] { FieldIndex(settings.Field, system, Name) };
        int wCount = system.Wavelengths.Wavelengths.Count;
        if (settings.Wavelength < 0 || settings.Wavelength > wCount)
            throw new ArgumentException($"{Name}: the wavelength row must be 0 (all) or 1 to {wCount} (it is {settings.Wavelength}).");
        var waveList = settings.Wavelength == 0 ? Enumerable.Range(0, wCount).ToArray() : new[] { settings.Wavelength - 1 };

        var tangential = new List<AnalysisCurve>();
        var sagittal = new List<AnalysisCurve>();
        var rows = new List<IReadOnlyList<string>>();
        foreach (int w in waveList)
        {
            var tracer = setup.Tracer(w);
            foreach (int f in fieldList)
            {
                var (hx, hy) = system.Fields.Normalized(f);
                double T(double px, double py)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = tracer.Trace.TracePath(setup.Launcher.Launch(tracer.Trace, hx, hy, px, py));
                    return Transmission(tracer, path, settings.Unpolarized, jones);
                }
                var t = fan.Select(p => T(0, p)).ToArray();
                var s = fan.Select(p => T(p, 0)).ToArray();
                string name = $"Field {f + 1}, {N(tracer.Trace.AirMicrometers)} µm";
                tangential.Add(new AnalysisCurve(name, fan, t));
                sagittal.Add(new AnalysisCurve(name, fan, s));
                for (int i = 0; i < fan.Length; i++)
                    rows.Add(new[] { (f + 1).ToString(), N(tracer.Trace.AirMicrometers), N(fan[i]), N(t[i], "f6"), N(s[i], "f6") });
            }
        }
        var plots = new AnalysisPlot[]
        {
            new LinePlot("Transmission fan, tangential", "Py", "Transmission", tangential),
            new LinePlot("Transmission fan, sagittal", "Px", "Transmission", sagittal),
        };
        var sections = new[]
        {
            new ReportSection("Transmission fan")
            {
                Pairs = new (string, string)[] { ("Polarization", settings.Unpolarized ? "Unpolarized (mean of (1, 0) and (0, 1))" : jones.ToString()) },
                TableHeaders = new[] { "Field", "Wavelength (µm)", "Pupil", "Tangential T", "Sagittal T" },
                TableRows = rows,
                Lines = new[] { "Tangential: Px = 0, Py along the fan; sagittal: Py = 0, Px along it. A ray that fails transmits zero." }
            },
        };
        return OpticsAnalysisResult.FromSections(Name, sections, CommonAssumptions(setup), plots);
    }
}
