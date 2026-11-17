using System.ComponentModel;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.NonSequential;

/// <summary>Settings of the <see cref="NscSingleRayTraceAnalysis"/> (OpticStudio help, "NSC
/// Single Ray Trace", "Ray Trace Settings").</summary>
public sealed record NscSingleRayTraceSettings
{
    [DisplayName("X")][Description("The ray's start x, lens units.")] public double X { get; init; }
    [DisplayName("Y")][Description("The ray's start y, lens units.")] public double Y { get; init; }
    [DisplayName("Z")][Description("The ray's start z, lens units.")] public double Z { get; init; }
    [DisplayName("L")][Description("Direction cosine along x (normalised with M and N).")] public double L { get; init; }
    [DisplayName("M")][Description("Direction cosine along y.")] public double M { get; init; }
    [DisplayName("N")][Description("Direction cosine along z.")] public double N { get; init; } = 1;

    [DisplayName("Use polarization")]
    [Description("Account for Fresnel and coating reflection, transmission and absorption with the polarization trace. Splitting turns it on.")]
    public bool UsePolarization { get; init; } = true;

    [DisplayName("Split NSC ray")]
    [Description("Split rays at refractive interfaces (simple ray splitting when the system setting says so).")]
    public bool SplitRays { get; init; }

    // OP-80
    [DisplayName("Scatter NSC rays")]
    [Description("Scatter rays by the faces' scatter models (the random choices use seed 1 and ray number 0).")]
    public bool ScatterRays { get; init; }

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Reference object")]
    [Description("0: the start point and direction are in the scene frame; otherwise in this object's local frame.")]
    public int ReferenceObject { get; init; }

    [DisplayName("Inside of")]
    [Description("The object the ray starts inside (0 for none).")]
    public int InsideOf { get; init; }

    [DisplayName("Ignore trace errors")]
    [Description("Leave trace errors out of the listing's error section (the segments keep their Z mark).")]
    public bool IgnoreTraceErrors { get; init; }
}

/// <summary>
/// OpticStudio's NSC Single Ray Trace (help, "NSC Single Ray Trace"): one ray through the
/// project's non-sequential objects (<see cref="ProjectNonSequential"/>), listed segment by
/// segment in the ray database layout (help, "Ray Database Viewer"): segment, parent, level, in,
/// hit, face, the event letters, global position, direction and normal, intensity, path and the
/// medium, with the energy ledger of the ray (docs/Optics-Conventions.md §69).
/// </summary>
public sealed class NscSingleRayTraceAnalysis : OpticsAnalysis<NscSingleRayTraceSettings>
{
    public const string SavedId = "NscSingleRayTrace";

    public override string Id => SavedId;
    public override string Name => "NSC single ray trace";
    public override string Category => "Non-sequential";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscSingleRayTraceSettings settings,
        CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var unit = system.Units.LensUnit;
        var scene = ProjectNonSequential.Scene(context, unit);
        if (scene is null)
        {
            return OpticsAnalysisResult.FromSections(Name, new[]
            {
                new ReportSection("No objects")
                {
                    Lines = new[] { "The system has no non-sequential objects, so there is nothing to trace. (Objects are added in the Non-Sequential Component Editor; they are read from the project's saved non-sequential section.)" }
                }
            }, new[] { "Nothing is traced." });
        }
        int count = system.Wavelengths.Wavelengths.Count;
        if (settings.Wavelength < 0 || settings.Wavelength > count)
            throw new ArgumentException($"NSC single ray trace: the wavelength row must be 0 (primary) or 1 to {count} (it is {settings.Wavelength}).");
        int row = settings.Wavelength == 0 ? system.Wavelengths.PrimaryIndex : settings.Wavelength - 1;
        double micrometers = system.Wavelengths.Wavelengths[row].Micrometers;

        var saved = context.System.NonSequential;
        var splitting = !settings.SplitRays ? NscSplitting.None
            : saved?.SimpleRaySplitting == true ? NscSplitting.Simple : NscSplitting.Split;
        var traceSettings = ProjectNonSequential.Settings(saved, unit, new NscTraceSettings
        {
            UsePolarization = settings.UsePolarization,
            Splitting = splitting,
            ScatterRays = settings.ScatterRays,   // OP-80
            Environment = system.Environment,
            Coatings = CoatingCatalogSettings.FromSettings(context.System.Coatings, notes),
            PrimaryMicrometers = system.Wavelengths.Primary.Micrometers,
        });
        var tracer = new NscTracer(scene, micrometers, traceSettings);
        cancellation.ThrowIfCancellationRequested();

        var start = new Vector3D(OpticsConventions.ToMeters(settings.X, unit), OpticsConventions.ToMeters(settings.Y, unit), OpticsConventions.ToMeters(settings.Z, unit));
        var direction = new Vector3D(settings.L, settings.M, settings.N);
        if (!(direction.Length > 0))
            throw new ArgumentException("NSC single ray trace: the direction cosines L, M, N are all zero.");
        if (settings.ReferenceObject != 0)
        {
            if (settings.ReferenceObject < 0 || settings.ReferenceObject > scene.Objects.Count)
                throw new ArgumentException($"NSC single ray trace: the reference object must be 0 or 1 to {scene.Objects.Count} (it is {settings.ReferenceObject}).");
            var placement = tracer.GlobalPlacement(settings.ReferenceObject);
            start = placement.Apply(start);
            direction = placement.ApplyDirection(direction);
        }
        var tree = tracer.TraceSingle(new NscLaunchRay { Position = start, Direction = direction, InsideOf = settings.InsideOf });

        string u = ParaxialReport.UnitName(unit);
        string Len(double meters) => StudyReport.Number(OpticsConventions.FromMeters(meters, unit), "g10");
        string Num(double v) => StudyReport.Number(v, "g10");
        var rows = tree.Segments.Select(sg => (IReadOnlyList<string>)new[]
        {
            sg.Number.ToString(), sg.Parent < 0 ? "—" : sg.Parent.ToString(), sg.Level.ToString(), sg.Inside.ToString(),
            sg.Hit.ToString(), sg.Face.ToString(), NscFormat.Letters(sg.Events),
            Len(sg.Position.X), Len(sg.Position.Y), Len(sg.Position.Z),
            Num(sg.Direction.X), Num(sg.Direction.Y), Num(sg.Direction.Z),
            sg.Hit == 0 ? "—" : Num(sg.Normal.X), sg.Hit == 0 ? "—" : Num(sg.Normal.Y), sg.Hit == 0 ? "—" : Num(sg.Normal.Z),
            Num(sg.Intensity), Len(sg.PathLength), Len(sg.OpticalPath), StudyReport.Number(sg.Index, "g8"), sg.Fate,
        }).ToList();

        var sections = new List<ReportSection>
        {
            new("Ray")
            {
                Pairs = new List<(string, string)>
                {
                    ("Start", $"{Len(start.X)}, {Len(start.Y)}, {Len(start.Z)} {u} (scene frame)"),
                    ("Direction", $"{Num(direction.X / direction.Length)}, {Num(direction.Y / direction.Length)}, {Num(direction.Z / direction.Length)}"),
                    ("Wavelength", $"{StudyReport.Number(micrometers)} µm in air (row {row + 1})"),
                    ("Segments", tree.Segments.Count.ToString()),
                    ("Trace errors", tree.Errors.Count.ToString()),
                }
            },
            new("Segments")
            {
                TableHeaders = new[] { "Seg#", "Prnt", "Levl", "In", "Hit", "Face", "XRTGZI", $"X ({u})", $"Y ({u})", $"Z ({u})", "L", "M", "N", "Nx", "Ny", "Nz", "Intensity", $"Path ({u})", $"Optical path ({u})", "Index", "Fate" },
                TableRows = rows,
                Lines = new[]
                {
                    "Segment 0 is the source point. Every other segment's position, normal and intensity are at its end, on the object hit, before the interaction there; L, M, N are the segment's own direction.",
                    "Letters: X terminated, R reflected, T transmitted, G ghost (reflected from a refractive face at its start), Z error, I total internal reflection, S scattered at the end, F scattered from the parent's hit (OP-80). 'In' is the object the segment runs through (0 the background); 'Hit' 0 is a miss.",
                    "Path is the geometric length from the parent's end; the optical path is from the source point, relative to the system's air.",
                }
            },
            new("Energy") { Pairs = tree.Ledger.Rows("") },
        };
        if (!settings.IgnoreTraceErrors && tree.Errors.Count > 0)
            sections.Insert(1, new ReportSection("Ray trace errors") { Lines = tree.Errors });

        var assumptions = new List<string>(tracer.Notes());
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions);
    }
}
