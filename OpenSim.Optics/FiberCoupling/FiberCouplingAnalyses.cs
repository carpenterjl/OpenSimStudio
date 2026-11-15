using System.ComponentModel;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.FiberCoupling;

/// <summary>
/// The settings of the <see cref="SingleModeFiberCouplingAnalysis"/>, after the help's Single
/// Mode Coupling dialog (OpticStudio 2025 R2 help, "Single Mode Coupling"). Decenters are lens
/// units, angles degrees.
/// </summary>
public sealed record SingleModeFiberCouplingSettings
{
    [DisplayName("Source NA x")]
    [Description("Numerical aperture of the source fiber in object space, xz plane (n·sin of the half angle to the 1/e² intensity).")]
    public double SourceNaX { get; init; } = 0.1;

    [DisplayName("Source NA y")]
    public double SourceNaY { get; init; } = 0.1;

    [DisplayName("Source X angle (deg)")]
    [Description("Rotation of the source fiber from its nominal orientation, measured in the xz plane.")]
    public double SourceAngleX { get; init; }

    [DisplayName("Source Y angle (deg)")]
    public double SourceAngleY { get; init; }

    [DisplayName("Field")]
    public int Field { get; init; } = 1;

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Sampling")]
    [Description("The pupil grid, n × n (8 to 4096).")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Align source to chief ray")]
    public bool AlignSourceToChiefRay { get; init; }

    [DisplayName("Ignore source fiber")]
    [Description("Light the pupil by the system apodization; efficiencies are then relative to the power entering the entrance pupil.")]
    public bool IgnoreSourceFiber { get; init; }

    [DisplayName("Use Huygens integral")]
    [Description("Sum the beam on the fiber's plane by the Huygens PSF first, instead of the direct ray-based integration.")]
    public bool UseHuygens { get; init; }

    [DisplayName("Receiver NA x")]
    [Description("Numerical aperture of the receiving fiber in image space, xz plane.")]
    public double ReceiverNaX { get; init; } = 0.1;

    [DisplayName("Receiver NA y")]
    public double ReceiverNaY { get; init; } = 0.1;

    [DisplayName("Tilt about X (deg)")]
    [Description("Rotation of the receiving fiber, first about x, then about y.")]
    public double TiltX { get; init; }

    [DisplayName("Tilt about Y (deg)")]
    public double TiltY { get; init; }

    [DisplayName("Decenter X")]
    [Description("Shift of the receiving fiber, lens units, applied before the tilts.")]
    public double DecenterX { get; init; }

    [DisplayName("Decenter Y")]
    public double DecenterY { get; init; }

    [DisplayName("Decenter Z")]
    public double DecenterZ { get; init; }

    [DisplayName("Align receiver to chief ray")]
    [Description("Centre the receiving fiber on the chief ray's image intercept instead of (0, 0).")]
    public bool AlignReceiverToChiefRay { get; init; }

    [DisplayName("Use polarization")]
    [Description("Polarization-dependent transmission (not built: refused).")]
    public bool UsePolarization { get; init; }

    /// <summary>The engine's request, in meters, with 0-based field and wavelength rows.</summary>
    public SingleModeCouplingRequest ToRequest(OpticalSystemData system, string who)
    {
        int fields = system.Fields.Points.Count, waves = system.Wavelengths.Wavelengths.Count;
        if (Field < 1 || Field > fields) throw new ArgumentException($"{who}: field {Field} is not one of the {fields} fields.");
        if (Wavelength < 0 || Wavelength > waves) throw new ArgumentException($"{who}: wavelength {Wavelength} is not one of the {waves} wavelengths (0 for the primary).");
        double m = OpticsConventions.MetersPer(system.Units.LensUnit);
        return new SingleModeCouplingRequest
        {
            Field = Field - 1, Wavelength = Wavelength == 0 ? system.Wavelengths.PrimaryIndex : Wavelength - 1, Sampling = Sampling,
            Source = new SourceFiber
            {
                Ignore = IgnoreSourceFiber, NaX = SourceNaX, NaY = SourceNaY, AngleX = SourceAngleX, AngleY = SourceAngleY,
                AlignToChiefRay = AlignSourceToChiefRay,
            },
            ReceiverNaX = ReceiverNaX, ReceiverNaY = ReceiverNaY, TiltX = TiltX, TiltY = TiltY,
            DecenterX = DecenterX * m, DecenterY = DecenterY * m, DecenterZ = DecenterZ * m,
            AlignReceiverToChiefRay = AlignReceiverToChiefRay, UseHuygens = UseHuygens, UsePolarization = UsePolarization,
        };
    }
}

/// <summary>
/// Single Mode Coupling (OpticStudio's of that name in the Laser and Fibers group; OP-50): the
/// coupling of a source fiber (or the lit pupil) into a single-mode receiving fiber of Gaussian
/// mode, split into system efficiency S and receiver efficiency T (<see cref="SingleModeFiberCoupling"/>).
/// Shows S, T, S·T and the maximum coupling with the aberrations ignored, and maps of the beam's
/// and the fiber's amplitude over the pupil (and the beam on the fiber's plane with Huygens).
/// </summary>
public sealed class SingleModeFiberCouplingAnalysis : WavefrontAnalysisBase<SingleModeFiberCouplingSettings>
{
    public const string SavedId = "SingleModeFiberCoupling";

    public SingleModeFiberCouplingAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Single Mode Fiber Coupling";
    public override string Category => "Beams";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        SingleModeFiberCouplingSettings settings, CancellationToken cancellation)
    {
        var request = settings.ToRequest(system, Name);
        var result = SingleModeFiberCoupling.Compute(lens, system, request, cancellation);
        var unit = system.Units.LensUnit;
        double per = OpticsConventions.MetersPer(unit);
        string u = Paraxial.ParaxialReport.UnitName(unit);
        string N(double v) => StudyReport.Number(v, "g8");
        var main = new ReportSection("Fiber coupling efficiency")
        {
            Pairs = new[]
            {
                ("Wavelength", $"{StudyReport.Number(result.Pupil.AirWavelength * 1e6)} µm in the system's air"),
                ("Field", settings.Field.ToString()),
                ("Algorithm", request.UseHuygens ? "Huygens integral" : "ray-based (direct) integration"),
                ("Source", request.Source.Ignore ? "ignored (pupil lit by the apodization)" : $"fiber NA {request.Source.NaX:g6} × {request.Source.NaY:g6}, {(request.Source.AlignToChiefRay ? "along the chief ray" : "along object z")}"),
                ("Receiver NA x, y", $"{request.ReceiverNaX:g6}, {request.ReceiverNaY:g6}"),
                ("Receiver waist x, y", $"{StudyReport.Number(result.WaistX / per)}, {StudyReport.Number(result.WaistY / per)} {u}"),
                ("Receiver position", $"{StudyReport.Number(result.FiberCentre.X / per)}, {StudyReport.Number(result.FiberCentre.Y / per)}, {StudyReport.Number(result.FiberCentre.Z / per)} {u}"),
                ("Receiver tilt about X, Y", $"{settings.TiltX:g6}°, {settings.TiltY:g6}°"),
                ("System efficiency", N(result.SystemEfficiency)),
                ("Receiver efficiency", N(result.ReceiverEfficiency)),
                ("Coupling efficiency", N(result.Coupling)),
                ("Maximum efficiency", N(result.MaximumCoupling)),
                ("Amplitude coupling (re, im)", $"{N(result.AmplitudeCoupling.Real)}, {N(result.AmplitudeCoupling.Imaginary)}"),
            },
        };
        var sections = new List<ReportSection> { main };
        if (result.Warnings.Count > 0) sections.Add(new ReportSection("Warnings") { Lines = result.Warnings });

        // Pupil maps: |W| = √(P·J) of the beam and the fiber's spectrum, both in the pupil.
        int n = result.Pupil.Sampling;
        var beam = new double[n, n];
        var fiber = new double[n, n];
        var mode = new GaussianFiberMode(request.ReceiverNaX, request.ReceiverNaY, result.FiberAxis.Alpha, result.FiberAxis.Beta);
        for (int j = 0; j < result.Pupil.Nodes.Count; j++)
        {
            var node = result.Pupil.Nodes[j];
            if (!node.Lit) continue;
            var (r, c) = result.Pupil.Index[j];
            beam[r, c] = Math.Sqrt(node.Power * node.ImageJacobian);
            fiber[r, c] = mode.Spectrum(node.Ray.Direction.X, node.Ray.Direction.Y, result.Pupil.ImageIndex);
        }
        var plots = new List<AnalysisPlot>
        {
            new AnalysisMapPlot("Beam amplitude in the pupil", "Px", "Py", new GridField(beam, -1, 1, -1, 1), "√(P·J)") { Minimum = 0 },
            new AnalysisMapPlot("Fiber mode in the pupil", "Px", "Py", new GridField(fiber, -1, 1, -1, 1), "relative") { Minimum = 0, Maximum = 1 },
        };
        if (result.Image is { } image)
        {
            plots.Add(new AnalysisMapPlot("Beam at the fiber (Huygens)", $"x ({u})", $"y ({u})",
                new GridField(image.Intensity, image.X[0] / per, image.X[^1] / per, image.Y[0] / per, image.Y[^1] / per), "relative") { Minimum = 0 });
        }
        var assumptions = new List<string>(result.Notes)
        {
            "Not built: polarization (coating and Fresnel transmission), materials on the image surface (the help: use POP for the receiver fiber's index), OpticStudio's test that switches the fast algorithm off.",
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}

/// <summary>The settings of the <see cref="MultimodeFiberCouplingAnalysis"/>. Lengths are lens units.</summary>
public sealed record MultimodeFiberCouplingSettings
{
    [DisplayName("Field")]
    public int Field { get; init; } = 1;

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Sampling")]
    [Description("The pupil grid, n × n (8 to 4096).")]
    public int Sampling { get; init; } = 128;

    [DisplayName("Fiber NA")]
    [Description("The fiber's acceptance NA; 0 to compute √(n_core² − n_cladding²).")]
    public double FiberNa { get; init; } = 0.2;

    [DisplayName("Core index")]
    public double CoreIndex { get; init; }

    [DisplayName("Cladding index")]
    public double CladdingIndex { get; init; }

    [DisplayName("Core radius")]
    [Description("Radius of the fiber core on the image surface, lens units.")]
    public double CoreRadius { get; init; } = 0.025;

    [DisplayName("Decenter X")]
    public double DecenterX { get; init; }

    [DisplayName("Decenter Y")]
    public double DecenterY { get; init; }

    [DisplayName("Tilt about X (deg)")]
    public double TiltX { get; init; }

    [DisplayName("Tilt about Y (deg)")]
    public double TiltY { get; init; }

    [DisplayName("Align to chief ray")]
    [Description("Centre the core on the chief ray's image intercept instead of (0, 0).")]
    public bool AlignToChiefRay { get; init; }

    [DisplayName("Ignore source fiber")]
    [Description("Light the pupil by the system apodization (on), or by a Gaussian source fiber at the object point (off).")]
    public bool IgnoreSourceFiber { get; init; } = true;

    [DisplayName("Source NA x")]
    public double SourceNaX { get; init; } = 0.1;

    [DisplayName("Source NA y")]
    public double SourceNaY { get; init; } = 0.1;

    public MultimodeCouplingRequest ToRequest(OpticalSystemData system, string who)
    {
        int fields = system.Fields.Points.Count, waves = system.Wavelengths.Wavelengths.Count;
        if (Field < 1 || Field > fields) throw new ArgumentException($"{who}: field {Field} is not one of the {fields} fields.");
        if (Wavelength < 0 || Wavelength > waves) throw new ArgumentException($"{who}: wavelength {Wavelength} is not one of the {waves} wavelengths (0 for the primary).");
        double m = OpticsConventions.MetersPer(system.Units.LensUnit);
        return new MultimodeCouplingRequest
        {
            Field = Field - 1, Wavelength = Wavelength == 0 ? system.Wavelengths.PrimaryIndex : Wavelength - 1, Sampling = Sampling,
            Source = new SourceFiber { Ignore = IgnoreSourceFiber, NaX = SourceNaX, NaY = SourceNaY },
            FiberNa = FiberNa, CoreIndex = CoreIndex, CladdingIndex = CladdingIndex, CoreRadius = CoreRadius * m,
            DecenterX = DecenterX * m, DecenterY = DecenterY * m, TiltX = TiltX, TiltY = TiltY, AlignToChiefRay = AlignToChiefRay,
        };
    }
}

/// <summary>
/// Multi-Mode Coupling (OP-50): the geometric estimate of coupling into a multimode fiber from its
/// NA and core radius (<see cref="MultimodeFiberCoupling"/>), with the ray intercepts on the
/// image surface marked accepted or not.
/// </summary>
public sealed class MultimodeFiberCouplingAnalysis : WavefrontAnalysisBase<MultimodeFiberCouplingSettings>
{
    public const string SavedId = "MultimodeFiberCoupling";

    public MultimodeFiberCouplingAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Multimode Fiber Coupling";
    public override string Category => "Beams";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        MultimodeFiberCouplingSettings settings, CancellationToken cancellation)
    {
        var request = settings.ToRequest(system, Name);
        var result = MultimodeFiberCoupling.Compute(lens, system, request, cancellation);
        double per = OpticsConventions.MetersPer(system.Units.LensUnit);
        string u = Paraxial.ParaxialReport.UnitName(system.Units.LensUnit);
        var main = new ReportSection("Multimode fiber coupling")
        {
            Pairs = new[]
            {
                ("Fiber NA", StudyReport.Number(result.NumericalAperture)),
                ("Core radius", $"{StudyReport.Number(settings.CoreRadius)} {u}"),
                ("Rays accepted / reaching the image / traced", $"{result.Accepted} / {result.Lit} / {result.Total}"),
                ("System efficiency", StudyReport.Number(result.SystemEfficiency, "g8")),
                ("Coupling efficiency", StudyReport.Number(result.Efficiency, "g8")),
            },
        };
        var sections = new List<ReportSection> { main };
        if (result.Pupil.Warnings.Count > 0) sections.Add(new ReportSection("Warnings") { Lines = result.Pupil.Warnings });
        var accepted = result.Intercepts.Where(p => p.Accepted).Select(p => new AnalysisPoint(p.X / per, p.Y / per, "")).ToList();
        var rejected = result.Intercepts.Where(p => !p.Accepted).Select(p => new AnalysisPoint(p.X / per, p.Y / per, "")).ToList();
        var plots = new List<AnalysisPlot>
        {
            new AnalysisScatterPlot("Ray intercepts from the core centre", $"x ({u})", $"y ({u})", new[]
            {
                new AnalysisScatterSeries("Accepted", accepted), new AnalysisScatterSeries("Rejected", rejected),
            }),
        };
        var assumptions = new List<string>(result.Notes) { "Not built: polarization, materials on the image surface." };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}
