using System.ComponentModel;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis;

// ---------------------------------------------------------------------------------------------
// OP-35: optical path difference and wavefront analyses. Each reads the project's saved lens
// (ProjectLens) unless a lens source is given, and measures OPD with OpdEngine. The analysis
// settings and their meaning come from the Ansys OpticStudio 2025 R2 help pages of the same names
// ("Optical Path Difference", "Ray Aberration (Aberrations)", "Wavefront Map", "Interferogram",
// "Foucault Analysis (Wavefront)", "Pupil Aberration"), saved under
// plans/sources/optics/op-35-help/.
// ---------------------------------------------------------------------------------------------

/// <summary>Settings of the <see cref="OpdFanAnalysis"/>.</summary>
public sealed record OpdFanSettings
{
    [DisplayName("Number of rays"), Description("Rays on each side of the pupil centre in each fan (OpticStudio's \"Number of Rays\"); at least 1.")]
    public int NumberOfRays { get; init; } = 20;

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for every wavelength (then referenced to the primary wavelength's reference sphere).")]
    public int Wavelength { get; init; }

    [DisplayName("Reference OPD"), Description("Exit pupil (the reference sphere), Infinity, Absolute or Absolute 2.")]
    public OpdReference Reference { get; init; } = OpdReference.ExitPupil;

    [DisplayName("Reference point"), Description("Centre the reference sphere on the chief ray or on the centroid.")]
    public OpdReferencePoint ReferencePoint { get; init; } = OpdReferencePoint.ChiefRay;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture are left out; off traces through every aperture.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="WavefrontMapAnalysis"/>.</summary>
public sealed record WavefrontMapSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes from −1 to 1 (odd n puts a node on the chief ray); 5 to 1025.")]
    public int Sampling { get; init; } = 65;

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Reference OPD"), Description("Exit pupil (the reference sphere), Infinity, Absolute or Absolute 2.")]
    public OpdReference Reference { get; init; } = OpdReference.ExitPupil;

    [DisplayName("Reference point"), Description("Centre the reference sphere on the chief ray or on the centroid.")]
    public OpdReferencePoint ReferencePoint { get; init; } = OpdReferencePoint.ChiefRay;

    [DisplayName("Reference to primary"), Description("Use the primary wavelength's reference sphere, so lateral colour shows as tilt.")]
    public bool ReferenceToPrimary { get; init; }

    [DisplayName("Remove tilt"), Description("Take out the least-squares x and y tilt.")]
    public bool RemoveTilt { get; init; }

    [DisplayName("Remove focus"), Description("Take out the least-squares focus term 2ρ² − 1.")]
    public bool RemoveFocus { get; init; }

    [DisplayName("Show as"), Description("False-colour image or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture are left blank; off traces through every aperture.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="InterferogramAnalysis"/>.</summary>
public sealed record InterferogramSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes from −1 to 1; 5 to 1025.")]
    public int Sampling { get; init; } = 129;

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Scale factor"), Description("Fringes per wave of OPD (2 for a double-pass interferometer).")]
    public double ScaleFactor { get; init; } = 1;

    [DisplayName("X-tilt"), Description("Fringes of tilt added across the pupil diameter in x, after the scale factor.")]
    public double XTilt { get; init; }

    [DisplayName("Y-tilt"), Description("Fringes of tilt added across the pupil diameter in y, after the scale factor.")]
    public double YTilt { get; init; }

    [DisplayName("Show as"), Description("Grey or false-colour image, or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Reference OPD"), Description("Exit pupil (the reference sphere), Infinity, Absolute or Absolute 2.")]
    public OpdReference Reference { get; init; } = OpdReference.ExitPupil;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture are left blank.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="FoucaultAnalysis"/>.</summary>
public sealed record FoucaultSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes, 8 to 513; raised automatically (and said so) when the aberration needs more so the image field does not alias.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Knife"), Description("Which half of the image plane the knife blocks.")]
    public KnifeEdge Knife { get; init; } = KnifeEdge.VerticalLeft;

    [DisplayName("Position (µm)"), Description("The knife's position from the chief ray's image point, in x for a vertical knife and y for a horizontal one.")]
    public double PositionMicrometers { get; init; }

    [DisplayName("Logarithmic"), Description("Show log10 of the relative intensity (floored at 10⁻⁶).")]
    public bool Logarithmic { get; init; }

    [DisplayName("Reference OPD"), Description("Exit pupil (the reference sphere), Infinity, Absolute or Absolute 2.")]
    public OpdReference Reference { get; init; } = OpdReference.ExitPupil;

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="PupilAberrationAnalysis"/>.</summary>
public sealed record PupilAberrationSettings
{
    [DisplayName("Number of rays"), Description("Rays on each side of the pupil centre in each fan; at least 1.")]
    public int NumberOfRays { get; init; } = 20;

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for every wavelength.")]
    public int Wavelength { get; init; }
}

/// <summary>The lens source, the "No surfaces" result and the row checks the OP-35 analyses share.</summary>
public abstract class WavefrontAnalysisBase<TSettings> : OpticsAnalysis<TSettings> where TSettings : class, new()
{
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for the project's saved surfaces.</param>
    protected WavefrontAnalysisBase(Func<OpticsAnalysisContext, SequentialLens?>? lensSource) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    protected abstract OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        TSettings settings, CancellationToken cancellation);

    protected sealed override OpticsAnalysisResult Compute(OpticsAnalysisContext context, TSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null)
            return OpticsAnalysisResult.FromSections(Name, new[]
            {
                new ReportSection("No surfaces")
                {
                    Lines = new[] { "The system has no sequential surfaces: add them in the Lens Data Editor, so there is something to trace." }
                }
            }, new[] { "Nothing is traced." });
        return Compute(lens, system, notes, settings, cancellation);
    }

    /// <summary>The 0-based rows a 1-based setting names; 0 means all when <paramref name="allowAll"/>.</summary>
    protected IReadOnlyList<int> Rows(int setting, int count, bool allowAll, string what)
    {
        if (setting == 0 && allowAll) return Enumerable.Range(0, count).ToArray();
        if (setting < 1 || setting > count)
            throw new ArgumentException($"{Name}: {what} {setting} is not one of the {count} {what}s{(allowAll ? " (0 for all)" : "")}.");
        return new[] { setting - 1 };
    }

    /// <summary>The 0-based wavelength row: 0 for the primary.</summary>
    protected int WavelengthRow(int setting, OpticalSystemData system)
    {
        int count = system.Wavelengths.Wavelengths.Count;
        if (setting == 0) return system.Wavelengths.PrimaryIndex;
        if (setting < 1 || setting > count)
            throw new ArgumentException($"{Name}: wavelength {setting} is not one of the {count} wavelengths (0 for the primary).");
        return setting - 1;
    }

    protected static void Check(bool ok, string message)
    {
        if (!ok) throw new ArgumentException(message);
    }

    protected static string W(double v) => StudyReport.Number(v, "g6");

    /// <summary>A square map of a pupil function on n × n nodes: [row = Py, column = Px].</summary>
    internal static (List<(double Px, double Py)> Pupil, List<(int Row, int Column)> Index) MapNodes(int n)
    {
        var pupil = new List<(double, double)>();
        var index = new List<(int, int)>();
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double px = OpdEngine.Node(k, n), py = OpdEngine.Node(i, n);
                if (px * px + py * py > 1) continue;
                pupil.Add((px, py));
                index.Add((i, k));
            }
        return (pupil, index);
    }

    internal static OpdOptions Options(OpdReference reference, OpdReferencePoint point, bool primary, bool checkApertures, bool allow) => new()
    {
        Reference = reference, ReferencePoint = point, ReferenceToPrimary = primary,
        IgnoreApertures = !checkApertures, AllowOpdLimitations = allow,
    };

    internal static IEnumerable<string> Describe(OpdResult r)
    {
        foreach (var n in r.Notes) yield return n;
        foreach (var l in r.Limitations) yield return "OPD limitation: " + l;
    }
}

/// <summary>
/// OpticStudio's Optical Path Difference fan (OP-35): OPD against the normalised pupil coordinate
/// along the tangential (Py, Px = 0) and sagittal (Px, Py = 0) lines, in waves. "If 'All'
/// wavelengths are shown, then the plot is referenced to the primary wavelength based reference
/// sphere" (help page "Optical Path Difference"); here the sphere's centre is the primary's and
/// each wavelength's OPD is against its own chief ray (no piston between wavelengths), a reading
/// listed in docs/Optics-Conventions.md §10.
/// </summary>
public sealed class OpdFanAnalysis : WavefrontAnalysisBase<OpdFanSettings>
{
    public const string SavedId = "OpdFan";

    public OpdFanAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Optical path difference";
    public override string Category => "Wavefront";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        OpdFanSettings settings, CancellationToken cancellation)
    {
        Check(settings.NumberOfRays >= 1 && settings.NumberOfRays <= 1000, $"{Name}: the number of rays must be 1 to 1000 (it is {settings.NumberOfRays}).");
        var fields = Rows(settings.Field, system.Fields.Points.Count, true, "field");
        var waves = Rows(settings.Wavelength, system.Wavelengths.Wavelengths.Count, true, "wavelength");
        bool all = waves.Count > 1;
        int m = settings.NumberOfRays;
        var line = Enumerable.Range(-m, 2 * m + 1).Select(i => i == m ? 1.0 : i == -m ? -1.0 : (double)i / m).ToArray();
        var pupil = line.Select(p => (0.0, p)).Concat(line.Select(p => (p, 0.0))).ToArray();
        var options = Options(settings.Reference, settings.ReferencePoint, all, settings.CheckApertures, settings.AllowOpdLimitations);

        var plots = new List<AnalysisPlot>();
        var sections = new List<ReportSection>();
        var assumptions = new List<string>();
        int failed = 0;
        foreach (int f in fields)
        {
            var tangential = new List<AnalysisCurve>();
            var sagittal = new List<AnalysisCurve>();
            var columns = new List<double[]>();
            var headers = new List<string> { "Pupil" };
            foreach (int w in waves)
            {
                cancellation.ThrowIfCancellationRequested();
                var r = OpdEngine.Compute(lens, system, f, w, pupil, options, cancellation);
                if (assumptions.Count == 0) assumptions.AddRange(Describe(r));
                failed += r.FailedCount;
                var t = r.Samples.Take(line.Length).ToArray();
                var s = r.Samples.Skip(line.Length).ToArray();
                string name = $"{system.Wavelengths.Wavelengths[w].Micrometers:g6} µm";
                tangential.Add(Curve(name, t, true));
                sagittal.Add(Curve(name, s, false));
                columns.Add(t.Select(x => x.Waves).ToArray());
                columns.Add(s.Select(x => x.Waves).ToArray());
                headers.Add($"W tangential, {name} (waves)");
                headers.Add($"W sagittal, {name} (waves)");
            }
            plots.Add(new LinePlot($"OPD, field {f + 1}, tangential", "Py", "OPD (waves)", tangential));
            plots.Add(new LinePlot($"OPD, field {f + 1}, sagittal", "Px", "OPD (waves)", sagittal));
            var rows = new List<IReadOnlyList<string>>();
            for (int i = 0; i < line.Length; i++)
                rows.Add(new[] { W(line[i]) }.Concat(columns.Select(c => double.IsFinite(c[i]) ? W(c[i]) : "failed")).ToArray());
            sections.Add(new ReportSection($"Field {f + 1}") { TableHeaders = headers, TableRows = rows });
        }
        assumptions.Add($"{2 * m + 1} rays per fan, the pupil coordinate normalised to the entrance pupil (docs/Optics-Conventions.md §7).");
        if (all) assumptions.Add("Every wavelength is referenced to the primary wavelength's reference point; each wavelength's OPD is against its own chief ray.");
        assumptions.Add(failed == 0 ? "Every ray reached the reference." : $"{failed} rays failed or missed the reference and are left out of the curves (marked \"failed\" in the tables).");
        assumptions.Add($"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.");
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }

    private static AnalysisCurve Curve(string name, OpdSample[] samples, bool tangential)
    {
        var valid = samples.Where(s => s.IsValid).ToArray();
        return new AnalysisCurve(name, valid.Select(s => tangential ? s.Py : s.Px).ToArray(), valid.Select(s => s.Waves).ToArray());
    }
}

/// <summary>
/// OpticStudio's Wavefront Map (OP-35): the OPD over the pupil as a map, with its peak-to-valley
/// and RMS. "Remove Tilt … is equivalent to referencing the OPD data to the centroid" (help page
/// "Wavefront Map"); here it is a least-squares fit (<see cref="WavefrontTools.Remove"/>), and the
/// centroid reference is offered separately. The RMS has the mean taken out (help page "RMS vs.
/// Field", "Comments about RMS wavefront computations"), every node weighted equally.
/// </summary>
public sealed class WavefrontMapAnalysis : WavefrontAnalysisBase<WavefrontMapSettings>
{
    public const string SavedId = "WavefrontMap";

    public WavefrontMapAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Wavefront map";
    public override string Category => "Wavefront";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        WavefrontMapSettings settings, CancellationToken cancellation)
    {
        Check(settings.Sampling >= 5 && settings.Sampling <= 1025, $"{Name}: the sampling must be 5 to 1025 (it is {settings.Sampling}).");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        int w = WavelengthRow(settings.Wavelength, system);
        int n = settings.Sampling;
        var (pupil, index) = MapNodes(n);
        var r = OpdEngine.Compute(lens, system, f, w, pupil,
            Options(settings.Reference, settings.ReferencePoint, settings.ReferenceToPrimary, settings.CheckApertures, settings.AllowOpdLimitations), cancellation);
        var values = WavefrontTools.Remove(pupil.Select(p => p.Px).ToArray(), pupil.Select(p => p.Py).ToArray(),
            r.Samples.Select(s => s.Waves).ToArray(), settings.RemoveTilt, settings.RemoveFocus, out var removed);
        var stats = WavefrontTools.Statistics(values);

        var grid = new double[n, n];
        for (int i = 0; i < n; i++) for (int k = 0; k < n; k++) grid[i, k] = double.NaN;
        for (int j = 0; j < pupil.Count; j++) grid[index[j].Row, index[j].Column] = values[j];
        var plot = new AnalysisMapPlot($"Wavefront map, field {f + 1}, {system.Wavelengths.Wavelengths[w].Micrometers:g6} µm", "Px", "Py",
            new GridField(grid, -1, 1, -1, 1), "waves") { Style = settings.ShowAs };

        var pairs = new List<(string, string)>
        {
            ("Peak to valley (waves)", W(stats.PeakToValley)),
            ("RMS, mean removed (waves)", W(stats.Rms)),
            ("Mean (waves)", W(stats.Mean)),
            ("Valid nodes", $"{stats.Points} of {pupil.Count}"),
        };
        if (settings.RemoveTilt) { pairs.Add(("Removed x tilt (waves)", W(removed.TiltX))); pairs.Add(("Removed y tilt (waves)", W(removed.TiltY))); }
        if (settings.RemoveFocus) pairs.Add(("Removed focus, coefficient of 2ρ² − 1 (waves)", W(removed.Focus)));
        var assumptions = new List<string>(Describe(r))
        {
            $"{n} × {n} pupil nodes from −1 to 1, those inside the unit circle traced; failed rays ({r.FailedCount}) are blank and left out of the statistics.",
            settings.RemoveTilt || settings.RemoveFocus
                ? $"Removed by a joint least-squares fit of Fringe Zernike terms 1–{(settings.RemoveFocus ? 4 : 3)}: {(settings.RemoveTilt ? "tilt" : "")}{(settings.RemoveTilt && settings.RemoveFocus ? " and " : "")}{(settings.RemoveFocus ? "focus" : "")}."
                : "Nothing removed.",
            $"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.",
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, new[] { new ReportSection("Wavefront") { Pairs = pairs } }, assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>
/// OpticStudio's Interferogram (OP-35) for one beam against the reference beam of zero OPD: the
/// fringe phase Δ = s·W + (t_x·Px + t_y·Py)/2 (s the scale factor, t the tilt fringes), and the
/// intensity (1 − cos 2πΔ)/2, dark where Δ is a whole number: "the phase at the center of each
/// beam is zero, resulting in a 'dark' fringe at the center" (help page "Interferogram"). Reading:
/// "the number of fringes of tilt" is taken across the full pupil diameter.
/// <para>Not built: a second beam from another configuration (OP-30), "Ref Beam To Vertex",
/// "Use Exit Pupil Shape", "Consider Path Length Difference", subapertures.</para>
/// </summary>
public sealed class InterferogramAnalysis : WavefrontAnalysisBase<InterferogramSettings>
{
    public const string SavedId = "Interferogram";

    public InterferogramAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Interferogram";
    public override string Category => "Wavefront";

    /// <summary>The intensity of the fringe pattern at one pupil point.</summary>
    public static double Intensity(double waves, double scale, double xTilt, double yTilt, double px, double py)
    {
        double phase = scale * waves + (xTilt * px + yTilt * py) / 2;
        return (1 - Math.Cos(2 * Math.PI * (phase - Math.Floor(phase)))) / 2;
    }

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        InterferogramSettings settings, CancellationToken cancellation)
    {
        Check(settings.Sampling >= 5 && settings.Sampling <= 1025, $"{Name}: the sampling must be 5 to 1025 (it is {settings.Sampling}).");
        Check(double.IsFinite(settings.ScaleFactor) && double.IsFinite(settings.XTilt) && double.IsFinite(settings.YTilt), $"{Name}: the scale factor and tilts must be finite.");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        int w = WavelengthRow(settings.Wavelength, system);
        int n = settings.Sampling;
        var (pupil, index) = MapNodes(n);
        var r = OpdEngine.Compute(lens, system, f, w, pupil,
            Options(settings.Reference, OpdReferencePoint.ChiefRay, false, settings.CheckApertures, settings.AllowOpdLimitations), cancellation);
        var grid = new double[n, n];
        for (int i = 0; i < n; i++) for (int k = 0; k < n; k++) grid[i, k] = double.NaN;
        for (int j = 0; j < pupil.Count; j++)
        {
            var s = r.Samples[j];
            if (s.IsValid) grid[index[j].Row, index[j].Column] = Intensity(s.Waves, settings.ScaleFactor, settings.XTilt, settings.YTilt, s.Px, s.Py);
        }
        var stats = WavefrontTools.Statistics(r.Samples.Select(s => s.Waves).ToArray());
        var plot = new AnalysisMapPlot($"Interferogram, field {f + 1}, {system.Wavelengths.Wavelengths[w].Micrometers:g6} µm", "Px", "Py",
            new GridField(grid, -1, 1, -1, 1), "relative intensity") { Style = settings.ShowAs, Minimum = 0, Maximum = 1 };
        var assumptions = new List<string>(Describe(r))
        {
            "Beam 1 is this system; beam 2 is the reference beam of zero OPD. Intensity (1 − cos 2πΔ)/2, Δ = scale × OPD + (x-tilt × Px + y-tilt × Py)/2.",
            $"{n} × {n} pupil nodes; failed rays ({r.FailedCount}) are blank.",
        };
        assumptions.AddRange(notes);
        var pairs = new List<(string, string)>
        {
            ("Scale factor", W(settings.ScaleFactor)), ("X-tilt (fringes)", W(settings.XTilt)), ("Y-tilt (fringes)", W(settings.YTilt)),
            ("Peak to valley of the OPD (waves)", W(stats.PeakToValley)), ("RMS of the OPD, mean removed (waves)", W(stats.Rms)),
        };
        return OpticsAnalysisResult.FromSections(Name, new[] { new ReportSection("Interferogram") { Pairs = pairs } }, assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>
/// OpticStudio's Foucault Analysis (OP-35): the knife-edge shadowgram, computed as
/// <see cref="FoucaultShadowgram"/> describes. Not built: the reference shadowgram bitmap, the
/// difference display and its registration, the FOUC operand, polarization; "Show As: cross
/// section" is the map window's movable cross-section.
/// </summary>
public sealed class FoucaultAnalysis : WavefrontAnalysisBase<FoucaultSettings>
{
    public const string SavedId = "FoucaultAnalysis";

    public FoucaultAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Foucault analysis";
    public override string Category => "Wavefront";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        FoucaultSettings settings, CancellationToken cancellation)
    {
        Check(settings.Sampling >= 8 && settings.Sampling <= 513, $"{Name}: the sampling must be 8 to 513 (it is {settings.Sampling}).");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        int w = WavelengthRow(settings.Wavelength, system);
        var r = FoucaultShadowgram.Compute(lens, system, f, w, settings.Sampling, settings.Knife, settings.PositionMicrometers * 1e-6,
            Options(settings.Reference, OpdReferencePoint.ChiefRay, false, settings.CheckApertures, settings.AllowOpdLimitations), cancellation);
        var field = r.Intensity;
        if (settings.Logarithmic)
        {
            var log = new double[field.Rows, field.Columns];
            for (int i = 0; i < field.Rows; i++)
                for (int k = 0; k < field.Columns; k++)
                    log[i, k] = double.IsFinite(field.Values[i, k]) ? Math.Log10(Math.Max(field.Values[i, k], 1e-6)) : double.NaN;
            field = new GridField(log, -1, 1, -1, 1);
        }
        var plot = new AnalysisMapPlot($"Foucault shadowgram, field {f + 1}, {system.Wavelengths.Wavelengths[w].Micrometers:g6} µm", "Px", "Py",
            field, settings.Logarithmic ? "log10 relative intensity" : "relative intensity");
        var assumptions = new List<string>(Describe(r.Opd))
        {
            $"Knife {settings.Knife} at {W(settings.PositionMicrometers)} µm from the chief ray's image point, in the image surface's frame.",
            "Relative intensity: 1 is the mean intensity of the same reconstruction without the knife.",
        };
        assumptions.AddRange(r.Notes);
        assumptions.AddRange(notes);
        var pairs = new List<(string, string)>
        {
            ("Energy passed by the knife", StudyReport.Number(r.PassedFraction, "P3")),
            ("Image samples", $"{r.SamplesX} × {r.SamplesY}"),
            ("Image spacing (µm)", $"{W(r.SpacingX * 1e6)} × {W(r.SpacingY * 1e6)}"),
        };
        return OpticsAnalysisResult.FromSections(Name, new[] { new ReportSection("Shadowgram") { Pairs = pairs } }, assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>One ray of a pupil aberration fan.</summary>
/// <param name="Percent">(real stop intercept − paraxial intercept) / paraxial stop radius × 100,
/// the y component for the tangential fan and x for the sagittal; NaN when the ray does not reach
/// the stop.</param>
public readonly record struct PupilAberrationPoint(double Pupil, double Percent, string? Failure);

/// <summary>
/// Entrance pupil aberration (OP-35), as OpticStudio's help page "Pupil Aberration" defines it:
/// "the difference between the real ray intercept on the stop surface and the on axis primary
/// wavelength paraxial ray intercept as a percentage of the paraxial stop radius". The paraxial
/// intercept of pupil coordinate P is P times the primary wavelength's paraxial marginal ray
/// height at the stop. Apertures are ignored (the question is where the rays land, not whether
/// they pass).
/// </summary>
public static class PupilAberration
{
    /// <summary>The tangential (Px = 0, y component against Py) or sagittal (Py = 0, x component
    /// against Px) fan at the given pupil coordinates.</summary>
    public static IReadOnlyList<PupilAberrationPoint> Fan(SequentialLens lens, OpticalSystemData system, int field, int wavelength,
        IReadOnlyList<double> pupil, bool tangential, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(pupil);
        var trace = SequentialTrace.Create(lens, system, wavelength);
        var launcher = new RayLauncher(lens, system);
        int stop = lens.StopIndex;
        double r = Math.Abs(launcher.Paraxial.MarginalRay.Heights[stop]);
        if (!(r > 0))
            throw new InvalidOperationException($"Pupil aberration: the paraxial marginal ray height at the stop (surface {stop}) is zero, so there is no stop radius to compare with.");
        var (hx, hy) = system.Fields.Normalized(field);
        var options = new TraceOptions { IgnoreApertures = true };
        var result = new PupilAberrationPoint[pupil.Count];
        for (int i = 0; i < pupil.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            double p = pupil[i];
            var launched = tangential ? launcher.Launch(trace, hx, hy, 0, p) : launcher.Launch(trace, hx, hy, p, 0);
            var traced = trace.TracePath(launched, options);
            var hit = traced.Hits.FirstOrDefault(h => h.Surface == stop);
            if (hit is null || hit.Status != RayStatus.Ok)
            {
                result[i] = new PupilAberrationPoint(p, double.NaN,
                    $"{RayStatuses.Label(traced.Status)} at surface {traced.FailedAtSurface}, before the stop");
                continue;
            }
            double real = tangential ? hit.Position.Y : hit.Position.X;
            result[i] = new PupilAberrationPoint(p, 100 * (real - p * r) / r, null);
        }
        return result;
    }
}

/// <summary>OpticStudio's Pupil Aberration fan (OP-35), see <see cref="PupilAberration"/>.</summary>
public sealed class PupilAberrationAnalysis : WavefrontAnalysisBase<PupilAberrationSettings>
{
    public const string SavedId = "PupilAberration";

    public PupilAberrationAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Pupil aberration";
    public override string Category => "Aberrations";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        PupilAberrationSettings settings, CancellationToken cancellation)
    {
        Check(settings.NumberOfRays >= 1 && settings.NumberOfRays <= 1000, $"{Name}: the number of rays must be 1 to 1000 (it is {settings.NumberOfRays}).");
        var fields = Rows(settings.Field, system.Fields.Points.Count, true, "field");
        var waves = Rows(settings.Wavelength, system.Wavelengths.Wavelengths.Count, true, "wavelength");
        int m = settings.NumberOfRays;
        var line = Enumerable.Range(-m, 2 * m + 1).Select(i => i == m ? 1.0 : i == -m ? -1.0 : (double)i / m).ToArray();
        var plots = new List<AnalysisPlot>();
        var sections = new List<ReportSection>();
        int failed = 0;
        double worst = 0;
        foreach (int f in fields)
        {
            var tCurves = new List<AnalysisCurve>();
            var sCurves = new List<AnalysisCurve>();
            var rows = new List<IReadOnlyList<string>>();
            foreach (int w in waves)
            {
                var t = PupilAberration.Fan(lens, system, f, w, line, true, cancellation);
                var s = PupilAberration.Fan(lens, system, f, w, line, false, cancellation);
                string name = $"{system.Wavelengths.Wavelengths[w].Micrometers:g6} µm";
                tCurves.Add(new AnalysisCurve(name, t.Where(x => x.Failure is null).Select(x => x.Pupil).ToArray(), t.Where(x => x.Failure is null).Select(x => x.Percent).ToArray()));
                sCurves.Add(new AnalysisCurve(name, s.Where(x => x.Failure is null).Select(x => x.Pupil).ToArray(), s.Where(x => x.Failure is null).Select(x => x.Percent).ToArray()));
                failed += t.Count(x => x.Failure is not null) + s.Count(x => x.Failure is not null);
                double maxT = t.Where(x => x.Failure is null).Select(x => Math.Abs(x.Percent)).DefaultIfEmpty(double.NaN).Max();
                double maxS = s.Where(x => x.Failure is null).Select(x => Math.Abs(x.Percent)).DefaultIfEmpty(double.NaN).Max();
                if (double.IsFinite(maxT)) worst = Math.Max(worst, maxT);
                if (double.IsFinite(maxS)) worst = Math.Max(worst, maxS);
                rows.Add(new[] { name, W(maxT), W(maxS) });
            }
            plots.Add(new LinePlot($"Pupil aberration, field {f + 1}, tangential", "Py", "Pupil aberration (%)", tCurves));
            plots.Add(new LinePlot($"Pupil aberration, field {f + 1}, sagittal", "Px", "Pupil aberration (%)", sCurves));
            sections.Add(new ReportSection($"Field {f + 1}")
            {
                TableHeaders = new[] { "Wavelength", "Largest |tangential| (%)", "Largest |sagittal| (%)" }, TableRows = rows,
            });
        }
        var assumptions = new List<string>
        {
            "Pupil aberration = (real ray's stop intercept − pupil coordinate × the on-axis primary-wavelength paraxial stop radius) / that radius × 100 (OpticStudio help, \"Pupil Aberration\"); the tangential fan plots the y component, the sagittal the x.",
            $"{2 * m + 1} rays per fan; apertures ignored. {(failed == 0 ? "Every ray reached the stop." : $"{failed} rays did not reach the stop and are left out.")}",
            $"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}. With aiming on the aberration is near zero by construction; a few percent or more without it suggests aiming is needed.",
            $"Largest |pupil aberration|: {W(worst)} %.",
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}
