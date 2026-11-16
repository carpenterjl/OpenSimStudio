using OpenSim.Core.Model;

namespace OpenSim.Optics.NonSequential.Detectors;

/// <summary>One parameter of an object type, in OpticStudio's order: its name as the help gives
/// it, its default (null where the help states none), and whether it is a length (lens units
/// when saved).</summary>
public sealed record NscParameterDescriptor(string Name, double? Default, bool IsLength);

/// <summary>
/// One object type as the Non-Sequential Component Editor's catalogue needs it (plan item OP-71
/// joins this list to its own): OpticStudio's type name, the parameters in order, whether it is a
/// volume, its faces, and whether this build traces it (a type that is not built carries the
/// reason it is refused).
/// </summary>
public sealed record NscObjectTypeDescriptor(
    string TypeName, IReadOnlyList<NscParameterDescriptor> Parameters, bool IsVolume,
    IReadOnlyDictionary<int, string> Faces, bool IsBuilt, string? NotBuiltReason = null);

/// <summary>
/// The detector object types (plan item OP-79; docs/Optics-Conventions.md §73): their descriptors
/// and their construction from a saved row. Parameter names and order are from the help pages
/// "Detector Rectangle Object", "Detector Color Object", "Detector Polar Object", "Detector
/// Surface Object" and "Detector Volume Object" (OpticStudio 2025 R2). The only defaults the help
/// states are the angle ranges, −90 to +90 degrees; a saved row may stop after parameter 4
/// (Rectangle, Color) and the rest are then 0, except those angles.
/// </summary>
public static class NscDetectorTypes
{
    public const string Rectangle = "Detector Rectangle", Color = "Detector Color", Polar = "Detector Polar",
        Surface = "Detector Surface", Volume = "Detector Volume";

    private static readonly IReadOnlyDictionary<int, string> AllFaces = new Dictionary<int, string> { [0] = "All Faces" };

    private static NscParameterDescriptor L(string n, double? d = null) => new(n, d, true);
    private static NscParameterDescriptor N(string n, double? d = null) => new(n, d, false);

    private static IReadOnlyList<NscParameterDescriptor> Flat(string p11) => new[]
    {
        L("X Half Width"), L("Y Half Width"), N("# X Pixels"), N("# Y Pixels"),
        N("Data Type", 0), N("Color", 0), N("Smoothing", 0), N("Scale", 0), N("Plot Scale", 0),
        N("Front Only", 0), N(p11, 0),
        N("X Angle Min", -90), N("X Angle Max", 90), N("Y Angle Min", -90), N("Y Angle Max", 90),
        N("Polarization", 0), N("Mirroring", 0),
    };

    /// <summary>The detector types, built and refused.</summary>
    public static IReadOnlyList<NscObjectTypeDescriptor> Descriptors { get; } = new[]
    {
        new NscObjectTypeDescriptor(Rectangle, Flat("PSF Mode Wavenumber"), false, AllFaces, true),
        new NscObjectTypeDescriptor(Color, Flat("Unused"), false, AllFaces, true),
        new NscObjectTypeDescriptor(Polar, new[] { N("Maximum Angle"), L("Radial Size"), N("# Radial Pixels"), N("# Angular Pixels"), N("Mirroring", 0) },
            false, AllFaces, true),
        new NscObjectTypeDescriptor(Surface, new[]
            {
                L("Radius"), N("Conic"), L("Max Aperture"), L("Min Aperture"), N("# A Zones"), N("# R Zones"),
                N("Data Type", 0), N("Color", 0), N("Unused", 0), N("Scale", 0), N("Max Scale", 0), N("Front Only", 0), N("Number of Terms", 0),
            }, false, AllFaces, false, SurfaceReason),
        new NscObjectTypeDescriptor(Volume, new[]
            {
                L("X Half Width"), L("Y Half Width"), L("Z Half Length"), N("# X pixels"), N("# Y pixels"), N("# Z pixels"),
            }, true, new Dictionary<int, string>(), false, VolumeReason),
    };

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
