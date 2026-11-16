using OpenSim.Core.Model;
using OpenSim.Optics.NonSequential.Editing;

namespace OpenSim.Optics.NonSequential.Detectors;

/// <summary>
/// The detector object types (plan item OP-79; docs/Optics-Conventions.md §73): their descriptors
/// for the Non-Sequential Component Editor's catalogue (<see cref="NscObjectTypeDescriptor"/>,
/// joined in <see cref="NscObjectCatalogue.Default"/>) and their construction from a saved row.
/// Parameter names and order are from the help pages "Detector Rectangle Object", "Detector Color
/// Object", "Detector Polar Object", "Detector Surface Object" and "Detector Volume Object"
/// (OpticStudio 2025 R2). The only defaults the help states are the angle ranges, −90 to +90
/// degrees; a saved row may stop after parameter 4 (Rectangle, Color) and the rest are then 0,
/// except those angles. The editor's defaults for a new row where the help states none (half
/// widths and radial size 1, 100 × 100 pixels, a 90° polar detector of 18 × 36 pixels) are this
/// build's own, chosen to give a valid detector. The ZPL type codes (NSC_DETE, NSC_DETC,
/// NSC_DETP, NSC_DETS, NSC_DETV) are from the help's "SETNSCPROPERTY (keywords)" table.
/// </summary>
public static class NscDetectorTypes
{
    public const string Rectangle = "Detector Rectangle", Color = "Detector Color", Polar = "Detector Polar",
        Surface = "Detector Surface", Volume = "Detector Volume";

    private static readonly IReadOnlyDictionary<int, string> AllFaces = new Dictionary<int, string> { [0] = "All Faces" };

    private static NscParameterDescriptor L(string n, double d = 0) => new(n, d, NscParameterKind.Length);
    private static NscParameterDescriptor N(string n, double d = 0) => new(n, d, NscParameterKind.Number);

    private static IReadOnlyList<NscParameterDescriptor> Flat(string p11) => new[]
    {
        L("X Half Width", 1), L("Y Half Width", 1), N("# X Pixels", 100), N("# Y Pixels", 100),
        N("Data Type", 0), N("Color", 0), N("Smoothing", 0), N("Scale", 0), N("Plot Scale", 0),
        N("Front Only", 0), N(p11, 0),
        N("X Angle Min", -90), N("X Angle Max", 90), N("Y Angle Min", -90), N("Y Angle Max", 90),
        N("Polarization", 0), N("Mirroring", 0),
    };

    private static NscObjectTypeDescriptor Built(string name, string code, IReadOnlyList<NscParameterDescriptor> parameters, string page) => new()
    {
        TypeName = name, ZplCode = code, Category = NscObjectCategory.Detector, Parameters = parameters, Faces = AllFaces, IsVolume = false,
        Source = $"OpticStudio 2025 R2 help, \"{page}\"", PlanItem = "OP-79",
        SavedGeometry = (p, unit) => Create(name, p, v => OpticsConventions.ToMeters(v, unit), new OpticsNscObjectSettings()),
    };

    /// <summary>The detector types, built and refused.</summary>
    public static IReadOnlyList<NscObjectTypeDescriptor> Descriptors { get; } = new[]
    {
        Built(Rectangle, "NSC_DETE", Flat("PSF Mode Wavenumber"), "Detector Rectangle Object"),
        Built(Color, "NSC_DETC", Flat("Unused"), "Detector Color Object"),
        Built(Polar, "NSC_DETP", new[] { N("Maximum Angle", 90), L("Radial Size", 1), N("# Radial Pixels", 18), N("# Angular Pixels", 36), N("Mirroring", 0) },
            "Detector Polar Object"),
        new NscObjectTypeDescriptor
        {
            TypeName = Surface, ZplCode = "NSC_DETS", Category = NscObjectCategory.Detector, Faces = AllFaces, IsVolume = false,
            Parameters = new[]
            {
                L("Radius"), N("Conic"), L("Max Aperture"), L("Min Aperture"), N("# A Zones"), N("# R Zones"),
                N("Data Type", 0), N("Color", 0), N("Unused", 0), N("Scale", 0), N("Max Scale", 0), N("Front Only", 0), N("Number of Terms", 0),
            },
            Source = "OpticStudio 2025 R2 help, \"Detector Surface Object\"", PlanItem = "OP-72", NotBuiltReason = SurfaceReason,
        },
        new NscObjectTypeDescriptor
        {
            TypeName = Volume, ZplCode = "NSC_DETV", Category = NscObjectCategory.Detector, Faces = new Dictionary<int, string>(), IsVolume = true,
            Parameters = new[] { L("X Half Width"), L("Y Half Width"), L("Z Half Length"), N("# X pixels"), N("# Y pixels"), N("# Z pixels") },
            Source = "OpticStudio 2025 R2 help, \"Detector Volume Object\"", PlanItem = "OP-79", NotBuiltReason = VolumeReason,
        },
    };

    /// <summary>The names of the built detector types.</summary>
    public static IReadOnlyList<string> BuiltNames { get; } = Descriptors.Where(d => d.IsBuilt).Select(d => d.TypeName).ToArray();

    internal const string SurfaceReason =
        "Detector Surface is not built (OP-79): its pixels are flat triangles on an Aspheric Surface shape, and neither the aspheric object (OP-72) nor triangle pixels exist on the non-sequential core.";
    internal const string VolumeReason =
        "Detector Volume is not built (OP-79): it records the flux entering and absorbed in voxels of a volume that may straddle other objects, and the core's medium stack refuses straddling volumes and has no per-segment hook through a volume.";

    /// <summary>The reason "Object Is A Detector" is refused on other objects.</summary>
    public const string ObjectsAsDetectorsReason =
        "Objects as detectors (\"Object Is A Detector\") are not built (OP-79): each pixel is a triangle of the object's drawing, and the core's objects are exact analytic shapes without triangles. OP-70's detecting stand-in counts a surface object's total flux.";

    /// <summary>True for every detector type name, built or not.</summary>
    public static bool IsDetectorType(string type) => Descriptors.Any(d => d.TypeName == type);

    /// <summary>
    /// The detector shape of a saved row (lengths converted with <paramref name="toMeters"/>).
    /// Throws <see cref="ArgumentException"/> naming the problem, and for Detector Surface and
    /// Detector Volume names why they are not built.
    /// </summary>
    public static NscGeometry Create(string type, IReadOnlyList<double> p, Func<double, double> toMeters, OpticsNscObjectSettings row)
    {
        double At(int i, double fallback) => i < p.Count ? p[i] : fallback;
        int Int(int i, string name, double fallback = 0)
        {
            double v = At(i, fallback);
            if (v != Math.Round(v) || Math.Abs(v) > int.MaxValue) throw new ArgumentException($"{type}: {name} (parameter {i + 1}) must be a whole number; it is {v}.");
            return (int)v;
        }
        switch (type)
        {
            case Rectangle:
            case Color:
            {
                if (p.Count < 4) throw new ArgumentException($"{type}: at least the first four parameters (half widths and pixel counts) are needed; {p.Count} are saved.");
                double hx = toMeters(p[0]), hy = toMeters(p[1]);
                int nx = Int(2, "# X Pixels"), ny = Int(3, "# Y Pixels");
                var display = Enumerable.Range(4, 5).Select(i => At(i, 0)).ToArray();
                bool front = At(9, 0) != 0;
                double xMin = At(11, -90), xMax = At(12, 90), yMin = At(13, -90), yMax = At(14, 90);
                int polarization = Int(15, "Polarization"), mirroring = Int(16, "Mirroring");
                bool interpolation = row.DetectorPixelInterpolation ?? false;
                if (type == Rectangle)
                    return new NscDetectorRectangle(hx, hy, nx, ny, front, xMin, xMax, yMin, yMax, polarization, mirroring,
                        Int(10, "PSF Mode Wavenumber"), interpolation, row.DetectorNormalizeCoherentPower ?? true, display);
                (int, double, double)? spectral = row.DetectorSpectralBins is int bins
                    ? (bins, row.DetectorSpectralMinMicrometers ?? 0, row.DetectorSpectralMaxMicrometers ?? 0)
                    : null;
                return new NscDetectorColor(hx, hy, nx, ny, front, xMin, xMax, yMin, yMax, polarization, mirroring, interpolation, spectral, display);
            }
            case Polar:
                if (p.Count < 4) throw new ArgumentException($"{type}: at least the first four parameters are needed; {p.Count} are saved.");
                return new NscDetectorPolar(p[0], toMeters(p[1]), Int(2, "# Radial Pixels"), Int(3, "# Angular Pixels"), Int(4, "Mirroring"));
            case Surface:
                throw new ArgumentException(SurfaceReason);
            case Volume:
                throw new ArgumentException(VolumeReason);
            default:
                throw new ArgumentException($"'{type}' is not a detector type.");
        }
    }
}
