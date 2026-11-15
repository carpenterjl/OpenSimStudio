using System.ComponentModel;
using System.Numerics;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.PhysicalOptics;

/// <summary>
/// The settings of the <see cref="PhysicalOpticsPropagationAnalysis"/>, after the help's
/// General, Beam Definition, Display and Fiber Data tabs (OpticStudio 2025 R2 help, "Physical
/// Optics Propagation"). Lengths are lens units, as the help has them; angles degrees.
/// </summary>
public sealed record PhysicalOpticsSettings
{
    [DisplayName("Start surface")]
    [Description("The beam starts in the space just before this surface.")]
    public int StartSurface { get; init; } = 1;

    [DisplayName("End surface")]
    [Description("The beam stops just after this surface; 0 for the image surface.")]
    public int EndSurface { get; init; }

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Field")]
    [Description("The field whose chief ray the beam follows (on-axis fields only).")]
    public int Field { get; init; } = 1;

    [DisplayName("Surf to beam")]
    [Description("Distance from the start surface to the beam, lens units; negative when the beam starts to the left of it.")]
    public double SurfaceToBeam { get; init; }

    [DisplayName("Use polarization")]
    [Description("Propagate Ex and Ey (not built: refused).")]
    public bool UsePolarization { get; init; }

    [DisplayName("Separate X, Y")]
    [Description("Separate x and y pilot beams (not built: refused).")]
    public bool SeparateXY { get; init; }

    [DisplayName("X sampling")]
    [Description("Samples across x: a power of 2 from 32 to 16384.")]
    public int SamplingX { get; init; } = 256;

    [DisplayName("Y sampling")]
    public int SamplingY { get; init; } = 256;

    [DisplayName("Auto width")]
    [Description("Set the array widths to ω0·√(π·n) (help, \"Comments about Point Spacing and Sampling\").")]
    public bool AutoWidth { get; init; } = true;

    [DisplayName("X width")]
    [Description("The array's full width in x, lens units (when Auto width is off).")]
    public double WidthX { get; init; } = 20;

    [DisplayName("Y width")]
    public double WidthY { get; init; } = 20;

    [DisplayName("Beam type")]
    public PopBeamType BeamType { get; init; } = PopBeamType.GaussianWaist;

    [DisplayName("File")]
    [Description("Full path of the ZBF (File) or ZMM (Multimode) file.")]
    public string File { get; init; } = "";

    [DisplayName("Normalise to")]
    public PopNormalization Normalization { get; init; } = PopNormalization.TotalPower;

    [DisplayName("Power or peak")]
    [Description("Total power (W), or peak irradiance (W per lens unit²).")]
    public double Power { get; init; } = 1;

    [DisplayName("Waist x")]
    [Description("Gaussian waist and astigmatic Gaussian: 1/e² waist radius; top hat: semi-width; lens units.")]
    public double WaistX { get; init; } = 1;

    [DisplayName("Waist y")]
    public double WaistY { get; init; } = 1;

    [DisplayName("Decenter x")]
    public double DecenterX { get; init; }

    [DisplayName("Decenter y")]
    public double DecenterY { get; init; }

    [DisplayName("Aperture x")]
    [Description("Gaussian waist: truncating aperture semi-axis; 0 for none.")]
    public double ApertureX { get; init; }

    [DisplayName("Aperture y")]
    public double ApertureY { get; init; }

    [DisplayName("Order x")]
    [Description("Gaussian waist: Hermite order l.")]
    public int OrderX { get; init; }

    [DisplayName("Order y")]
    public int OrderY { get; init; }

    [DisplayName("Angle x (deg)")]
    [Description("Gaussian angle and size+angle: far-field half-angle divergence in air.")]
    public double AngleX { get; init; } = 1;

    [DisplayName("Angle y (deg)")]
    public double AngleY { get; init; } = 1;

    [DisplayName("Size x")]
    [Description("Gaussian size+angle: the beam size (not waist), lens units.")]
    public double SizeX { get; init; } = 1;

    [DisplayName("Size y")]
    public double SizeY { get; init; } = 1;

    [DisplayName("Waist position x")]
    [Description("Astigmatic Gaussian: distance from the beam to its waist, negative when the waist is to the left; lens units.")]
    public double WaistPositionX { get; init; }

    [DisplayName("Waist position y")]
    public double WaistPositionY { get; init; }

    [DisplayName("Multimode seed")]
    public long Seed { get; init; }

    [DisplayName("Multimode scale")]
    [Description("Scales every transverse value of the ZMM file; 0 for 1.")]
    public double Scale { get; init; }

    [DisplayName("Surface transfer")]
    [Description("Ray based: the phase from a real ray at every sample. Paraxial thin lens: −φ·r²/2.")]
    public PopTransfer Transfer { get; init; } = PopTransfer.RayBased;

    [DisplayName("Zero phase below")]
    [Description("Relative irradiance below which the phase is shown as zero.")]
    public double ZeroPhaseBelow { get; init; } = 0.001;

    [DisplayName("Save output beam to")]
    [Description("Full path of a ZBF file to write the end beam to; empty for none.")]
    public string SaveOutputBeamTo { get; init; } = "";

    [DisplayName("Save beam at all surfaces")]
    [Description("Also write the beam after every surface, the file name suffixed _nnnn.")]
    public bool SaveBeamAtAllSurfaces { get; init; }

    [DisplayName("Compute fiber coupling")]
    [Description("Fiber coupling is OP-50 (not built: refused).")]
    public bool ComputeFiberCoupling { get; init; }

    /// <summary>The engine's request in meters, or <see cref="NotSupportedException"/> naming a
    /// refused option.</summary>
    public PopRequest ToRequest(LensUnit unit, int wavelengthCount)
    {
        if (UsePolarization)
            throw new NotSupportedException("Physical optics: \"Use polarization\" (propagating Ex and Ey through the polarization ray trace) is not built; this propagation is scalar. Clear the option.");
        if (SeparateXY)
            throw new NotSupportedException("Physical optics: \"Separate X, Y\" (separate x and y pilot beams and a bispherical reference) is not built. Clear the option.");
        if (ComputeFiberCoupling)
            throw new NotSupportedException("Physical optics: fiber coupling is plan item OP-50 and is not built. Clear \"Compute fiber coupling\".");
        if (Wavelength < 0 || Wavelength > wavelengthCount)
            throw new ArgumentException($"Physical optics: the wavelength row must be 0 (primary) or 1 to {wavelengthCount} (it is {Wavelength}).");
        if (!(ZeroPhaseBelow >= 0) || ZeroPhaseBelow > 1)
            throw new ArgumentException($"Physical optics: \"Zero phase below\" must be between 0 and 1 (it is {ZeroPhaseBelow}).");
        double m = OpticsConventions.MetersPer(unit);
        return new PopRequest
        {
            StartSurface = StartSurface,
            EndSurface = EndSurface == 0 ? null : EndSurface,
            WavelengthIndex = Wavelength == 0 ? null : Wavelength - 1,
            Field = Field,
            SurfaceToBeam = SurfaceToBeam * m,
            SamplingX = SamplingX,
            SamplingY = SamplingY,
            WidthX = AutoWidth ? null : WidthX * m,
            WidthY = AutoWidth ? null : WidthY * m,
            Transfer = Transfer,
            Unit = unit,
            KeepSurfaceFields = SaveBeamAtAllSurfaces && !string.IsNullOrWhiteSpace(SaveOutputBeamTo),
            Beam = new PopBeamDefinition
            {
                Type = BeamType, File = File, Normalization = Normalization,
                Power = Normalization == PopNormalization.PeakIrradiance ? Power / (m * m) : Power,
                WaistX = WaistX * m, WaistY = WaistY * m, DecenterX = DecenterX * m, DecenterY = DecenterY * m,
                ApertureX = ApertureX * m, ApertureY = ApertureY * m, OrderX = OrderX, OrderY = OrderY,
                AngleX = AngleX, AngleY = AngleY, SizeX = SizeX * m, SizeY = SizeY * m,
                WaistPositionX = WaistPositionX * m, WaistPositionY = WaistPositionY * m,
                Seed = Seed, Scale = Scale,
            },
        };
    }
}

/// <summary>Runs the propagation an analysis or an operand asks for on a lens and system.</summary>
public static class PopRunner
{
    /// <summary>The propagation of <paramref name="settings"/> through <paramref name="lens"/>,
    /// sizing floating apertures first when the lens has any without a semi-diameter.</summary>
    public static PopRun Run(SequentialLens lens, OpticalSystemData system, PhysicalOpticsSettings settings,
        CancellationToken cancellation = default)
    {
        var request = settings.ToRequest(system.Units.LensUnit, system.Wavelengths.Wavelengths.Count);
        IReadOnlyList<double>? semi = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semi = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
        return PopEngine.Run(lens, system, request, semi, cancellation);
    }
}

/// <summary>The report sections and plots both POP analyses show for a field.</summary>
internal static class PopDisplay
{
    public static string Unit(LensUnit unit) => Paraxial.ParaxialReport.UnitName(unit);

    public static ReportSection BeamSection(string heading, PopField field, PopBeamData data, LensUnit unit, IEnumerable<(string, string)> leading)
    {
        double per = OpticsConventions.MetersPer(unit);
        string u = Unit(unit);
        string L(double m) => double.IsInfinity(m) ? "infinite" : $"{StudyReport.Number(m / per)} {u}";
        var p = field.Pilot;
        var principal = data.PrincipalWidths;
        var pairs = new List<(string, string)>(leading)
        {
            ("Display X width", L(field.WidthX)),
            ("Display Y height", L(field.WidthY)),
            ("Sampling", $"{field.Nx} x {field.Ny}, spacing {StudyReport.Number(field.Dx / per)} x {StudyReport.Number(field.Dy / per)} {u}"),
            ("Reference", field.Reference == PopReference.Plane ? "plane (inside the pilot's Rayleigh range)" : $"sphere of radius {L(field.ReferenceRadius)} (outside the pilot's Rayleigh range)"),
            ("Peak irradiance", $"{StudyReport.Number(data.PeakIrradiance * per * per)} W/{u}²"),
            ("Total power", $"{StudyReport.Number(data.Power)} W"),
            ("Pilot: size", L(p.BeamRadius)),
            ("Pilot: waist", L(p.WaistRadius)),
            ("Pilot: position (past the waist)", L(p.DistanceFromWaist)),
            ("Pilot: Rayleigh range", L(p.RayleighRange)),
            ("Pilot: phase radius", L(p.PhaseRadius)),
            ("Medium index", StudyReport.Number(p.Index)),
            ("Centroid x, y", $"{StudyReport.Number(data.CentroidX / per)}, {StudyReport.Number(data.CentroidY / per)} {u}"),
            ("Beam width x, y (2σ)", $"{StudyReport.Number(data.WidthX / per)}, {StudyReport.Number(data.WidthY / per)} {u}"),
            ("Beam width, principal axes", $"{StudyReport.Number(principal.X / per)}, {StudyReport.Number(principal.Y / per)} {u}"),
            ("M² x, y", $"{StudyReport.Number(data.MSquaredX)}, {StudyReport.Number(data.MSquaredY)}"),
        };
        return new ReportSection(heading) { Pairs = pairs };
    }

    public static IReadOnlyList<AnalysisPlot> Plots(PopField field, PopBeamData data, LensUnit unit, double zeroPhaseBelow, string what)
    {
        double per = OpticsConventions.MetersPer(unit);
        string u = Unit(unit);
        int nx = field.Nx, ny = field.Ny;
        var irradiance = new double[ny, nx];
        var phase = new double[ny, nx];
        double limit = zeroPhaseBelow * data.PeakIrradiance;
        for (int r = 0; r < ny; r++)
            for (int c = 0; c < nx; c++)
            {
                var s = field.Samples[r, c];
                double i = s.Real * s.Real + s.Imaginary * s.Imaginary;
                irradiance[r, c] = i * per * per;
                // The printed phase is the help's sign, −arg U (docs §66).
                phase[r, c] = i < limit || i == 0 ? 0 : -Math.Atan2(s.Imaginary, s.Real);
            }
        double x0 = field.X(0) / per, x1 = field.X(nx - 1) / per, y0 = field.Y(0) / per, y1 = field.Y(ny - 1) / per;
        var plots = new List<AnalysisPlot>
        {
            new AnalysisMapPlot($"{what}: irradiance", $"x ({u})", $"y ({u})", new GridField(irradiance, x0, x1, y0, y1), $"W/{u}²") { Minimum = 0 },
            new AnalysisMapPlot($"{what}: phase", $"x ({u})", $"y ({u})", new GridField(phase, x0, x1, y0, y1), "rad") { Minimum = -Math.PI, Maximum = Math.PI },
        };
        var xs = Enumerable.Range(0, nx).Select(c => field.X(c) / per).ToArray();
        var ys = Enumerable.Range(0, ny).Select(r => field.Y(r) / per).ToArray();
        plots.Add(new LinePlot($"{what}: x cross-section", $"x ({u})", $"Irradiance (W/{u}²)",
            new[] { new AnalysisCurve($"Row {ny / 2} (y = 0)", xs, Enumerable.Range(0, nx).Select(c => irradiance[ny / 2, c]).ToArray()) }));
        plots.Add(new LinePlot($"{what}: y cross-section", $"y ({u})", $"Irradiance (W/{u}²)",
            new[] { new AnalysisCurve($"Column {nx / 2} (x = 0)", ys, Enumerable.Range(0, ny).Select(r => irradiance[r, nx / 2]).ToArray()) }));
        var (radius, fraction) = PopBeamMetrics.EnclosedCurve(field, data.CentroidX, data.CentroidY);
        // Thin the curve to at most 400 points for drawing: every k-th sample radius.
        int step = Math.Max(1, radius.Length / 400);
        var rs = new List<double>();
        var fs = new List<double>();
        for (int i = 0; i < radius.Length; i += step) { rs.Add(radius[i] / per); fs.Add(fraction[i]); }
        rs.Add(radius[^1] / per); fs.Add(fraction[^1]);
        plots.Add(new LinePlot($"{what}: encircled power", $"Radius from the centroid ({u})", "Fraction of the power",
            new[] { new AnalysisCurve("Encircled", rs, fs) }));
        return plots;
    }

    public static IReadOnlyList<string> Assumptions => new[]
    {
        "Physical optics propagation (docs/Optics-Conventions.md §66): one scalar field; angular spectrum (PTP) inside the pilot beam's Rayleigh range, Fresnel with a reference sphere (WTS, STW) outside, the four cases of the help's \"Propagating In and Out of the Rayleigh Range\".",
        "Phase is shown in the help's sign: positive where the wavefront is ahead of the reference (plane inside the Rayleigh range, sphere centred on the pilot's waist outside). Irradiance is W per lens unit².",
        "Beam width is 2σ from the second moments; M² is Siegman's (4π/λ)·√(⟨x²⟩⟨θ²⟩ − ⟨xθ⟩²) with angles in the medium.",
        "Not built: polarized propagation, separate x and y pilots, off-axis and tilted systems, per-surface physical optics settings, fiber coupling (OP-50), projection other than along the beam, zoom.",
    };
}

/// <summary>
/// The Physical Optics Propagation analysis (OpticStudio's of that name; OP-49): the beam of the
/// settings propagated from the start to the end surface, shown as irradiance and phase maps,
/// x and y cross-sections and encircled power, with the beam data and the propagation report.
/// </summary>
public sealed class PhysicalOpticsPropagationAnalysis : OpticsAnalysis<PhysicalOpticsSettings>
{
    public const string SavedId = "PhysicalOpticsPropagation";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public PhysicalOpticsPropagationAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? ProjectLens.Sequential;
    }

    public override string Id => SavedId;
    public override string Name => "Physical Optics Propagation";
    public override string Category => "Beams";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, PhysicalOpticsSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null)
            return OpticsAnalysisResult.FromSections(Name, new[]
            {
                new ReportSection("No surfaces") { Lines = new[] { "The system has no sequential surfaces: add them in the Lens Data Editor, so there is something to propagate the beam through." } }
            }, new[] { "Nothing is propagated." });
        var run = PopRunner.Run(lens, system, settings, cancellation);
        var unit = system.Units.LensUnit;
        double per = OpticsConventions.MetersPer(unit);
        var data = PopBeamMetrics.Compute(run.Field);
        string u = PopDisplay.Unit(unit);
        var leading = new List<(string, string)>
        {
            ("Start surface", run.StartSurface.ToString()),
            ("End surface", run.EndSurface == lens.ImageIndex ? $"{run.EndSurface} (image)" : run.EndSurface.ToString()),
            ("Wavelength", $"{StudyReport.Number(run.AirWavelength * 1e6)} µm in the system's air"),
            ("Beam type", settings.BeamType.ToString()),
            ("Surface transfer", settings.Transfer == PopTransfer.RayBased ? "ray based" : "paraxial thin lens"),
        };
        var sections = new List<ReportSection> { PopDisplay.BeamSection("Beam at the end surface", run.Field, data, unit, leading) };
        var rows = run.Steps.Select(s => (IReadOnlyList<string>)new[]
        {
            s.Where, s.Operator, StudyReport.Number(s.Distance / per), StudyReport.Number(s.Dx / per), StudyReport.Number(s.Dy / per),
            s.Reference == PopReference.Plane ? "plane" : "sphere",
        }).ToList();
        sections.Add(new ReportSection("Propagation report")
        {
            TableHeaders = new[] { "Where", "Operator", $"Distance ({u})", $"Dx ({u})", $"Dy ({u})", "Reference after" },
            TableRows = rows,
            Lines = run.Warnings.Count == 0 ? new[] { "No sampling warnings." } : run.Warnings.ToArray(),
        });

        var written = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.SaveOutputBeamTo))
        {
            string path = settings.SaveOutputBeamTo;
            ZbfFile.Write(path, ZbfFile.FromField(run.Field, unit));
            written.Add(path);
            if (settings.SaveBeamAtAllSurfaces)
            {
                string stem = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
                foreach (var (surface, f) in run.SurfaceFields)
                {
                    string each = $"{stem}_{surface:0000}.ZBF";   // help: "MyBeamData_0012.ZBF"
                    ZbfFile.Write(each, ZbfFile.FromField(f, unit));
                    written.Add(each);
                }
            }
            sections.Add(new ReportSection("Beam files written") { Lines = written });
        }
        var assumptions = PopDisplay.Assumptions.Concat(run.Notes).Concat(notes).ToList();
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, PopDisplay.Plots(run.Field, data, unit, settings.ZeroPhaseBelow, "POP"));
    }
}

/// <summary>The settings of the <see cref="BeamFileViewerAnalysis"/>.</summary>
public sealed record BeamFileViewerSettings
{
    [DisplayName("File")]
    [Description("Full path of the ZBF beam file.")]
    public string File { get; init; } = "";

    [DisplayName("Zero phase below")]
    [Description("Relative irradiance below which the phase is shown as zero.")]
    public double ZeroPhaseBelow { get; init; } = 0.001;
}

/// <summary>
/// The Beam File Viewer (OpticStudio's of that name; OP-49): a ZBF file shown as the POP
/// analysis shows a beam, without propagating it. A polarized file shows the total irradiance
/// and the Ex phase (help, "Physical Optics Propagation": "the 'phase' is always the EX phase"),
/// and its beam data are those of Ex.
/// </summary>
public sealed class BeamFileViewerAnalysis : OpticsAnalysis<BeamFileViewerSettings>
{
    public const string SavedId = "BeamFileViewer";

    public override string Id => SavedId;
    public override string Name => "Beam File Viewer";
    public override string Category => "Beams";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, BeamFileViewerSettings settings, CancellationToken cancellation)
    {
        var beam = ZbfFile.Read(settings.File);
        if (!(beam.Wavelength > 0) || !(beam.Index > 0))
            throw new FormatException($"Beam File Viewer: the file's wavelength ({beam.Wavelength}) and index ({beam.Index}) must be positive to place its pilot beam.");
        var notes = new List<string>();
        double per = OpticsConventions.MetersPer(beam.Unit);
        double air = beam.Wavelength * per * beam.Index;
        var ex = ZbfFile.ComponentToField(beam, beam.Ex, air, beam.Index, notes);
        cancellation.ThrowIfCancellationRequested();
        var data = PopBeamMetrics.Compute(ex);
        var leading = new List<(string, string)>
        {
            ("File", Path.GetFileName(settings.File)),
            ("Polarized", beam.IsPolarized ? "yes (beam data are of Ex)" : "no"),
            ("Wavelength in the medium", $"{StudyReport.Number(beam.Wavelength)} {PopDisplay.Unit(beam.Unit)}"),
        };
        var sections = new List<ReportSection> { PopDisplay.BeamSection("Beam", ex, data, beam.Unit, leading) };
        var display = ex;
        if (beam.IsPolarized)
        {
            var ey = ZbfFile.ComponentToField(beam, beam.Ey!, air, beam.Index, new List<string>());
            double powerY = ey.Power;
            sections.Add(new ReportSection("Polarization") { Pairs = new[] { ("Ex power", $"{StudyReport.Number(data.Power)} W"), ("Ey power", $"{StudyReport.Number(powerY)} W"), ("Total power", $"{StudyReport.Number(data.Power + powerY)} W") } });
            // Total irradiance with the Ex phase: amplitude √(|Ex|² + |Ey|²) on the Ex phase.
            var total = new Complex[ex.Ny, ex.Nx];
            for (int r = 0; r < ex.Ny; r++)
                for (int c = 0; c < ex.Nx; c++)
                {
                    var a = ex.Samples[r, c]; var b = ey.Samples[r, c];
                    double amplitude = Math.Sqrt(a.Real * a.Real + a.Imaginary * a.Imaginary + b.Real * b.Real + b.Imaginary * b.Imaginary);
                    total[r, c] = a == Complex.Zero ? new Complex(amplitude, 0) : Complex.FromPolarCoordinates(amplitude, a.Phase);
                }
            display = new PopField(total, ex.Dx, ex.Dy, ex.Pilot, ex.Reference);
        }
        var shown = beam.IsPolarized ? PopBeamMetrics.Compute(display) : data;
        var assumptions = new List<string>
        {
            "Beam File Viewer: the file's samples are shown as written; its pilot beam places the reference (a file whose pilot waist entries are 0 has its pilot fitted to the field, help \"Beam File Viewer\").",
            "Phase is shown in the help's sign: positive where the wavefront is ahead of the reference. Irradiance is W per (file unit)².",
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, PopDisplay.Plots(display, shown, beam.Unit, settings.ZeroPhaseBelow, "Beam file"));
    }
}
