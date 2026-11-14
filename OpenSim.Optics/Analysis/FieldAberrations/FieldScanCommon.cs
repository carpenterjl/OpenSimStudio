using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// OP-37: what the field, distortion, colour and illumination analyses share.

/// <summary>The direction a field scan runs from the axis (OpticStudio's "Scan Type": +y, +x,
/// −y or −x; Ansys OpticStudio 2025 R2 help, "Field Curvature and Distortion" and "Relative
/// Illumination").</summary>
public enum FieldScan
{
    PlusY = 0,
    PlusX = 1,
    MinusY = 2,
    MinusX = 3,
}

/// <summary>Geometry of a <see cref="FieldScan"/>.</summary>
public static class FieldScans
{
    /// <summary>The unit direction (Hx, Hy) of the scan in normalised field.</summary>
    public static (double Hx, double Hy) Direction(FieldScan scan) => scan switch
    {
        FieldScan.PlusY => (0, 1),
        FieldScan.PlusX => (1, 0),
        FieldScan.MinusY => (0, -1),
        FieldScan.MinusX => (-1, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(scan), scan, "Unknown field scan."),
    };

    /// <summary>"+y", "+x", "−y" or "−x".</summary>
    public static string Label(FieldScan scan) => scan switch
    {
        FieldScan.PlusY => "+y",
        FieldScan.PlusX => "+x",
        FieldScan.MinusY => "−y",
        FieldScan.MinusX => "−x",
        _ => throw new ArgumentOutOfRangeException(nameof(scan), scan, "Unknown field scan."),
    };

    /// <summary>True for a scan along x, whose tangential plane is the xz plane.</summary>
    public static bool AlongX(FieldScan scan) => scan is FieldScan.PlusX or FieldScan.MinusX;

    /// <summary>The sagittal direction in the launch (global) frame: the normal of the scan's
    /// meridional plane, x̂ for a y scan and ŷ for an x scan.</summary>
    public static Vector3D Sagittal(FieldScan scan) => AlongX(scan) ? Vector3D.UnitY : Vector3D.UnitX;
}

/// <summary>A lens and system ready for a field scan, with the trace inputs resolved once.</summary>
internal sealed class FieldScanSetup
{
    public required SequentialLens Lens { get; init; }
    public required OpticalSystemData System { get; init; }
    public IReadOnlyList<double>? SemiDiameters { get; init; }
    public required RayLauncher Launcher { get; init; }
    public required List<string> Notes { get; init; }

    public LensUnit Unit => System.Units.LensUnit;

    /// <summary>The real trace at a 0-based wavelength row.</summary>
    public SequentialTrace Trace(int row) => SequentialTrace.Create(Lens, System, row, SemiDiameters);

    /// <summary>The largest normalised field is 1; this is the field value it stands for along the scan, in the field type's unit (degrees or meters).</summary>
    public double ScanScale(FieldScan scan) => FieldScans.AlongX(scan) ? System.Fields.Scale.X : System.Fields.Scale.Y;

    /// <summary>The field value of normalised scan coordinate H, shown in degrees or lens units.</summary>
    public double FieldShown(double h, FieldScan scan)
    {
        double v = h * ScanScale(scan);
        return FieldTypes.IsAngle(System.Fields.Type) ? v : OpticsConventions.FromMeters(v, Unit);
    }

    /// <summary>"Y field (°)" and the like.</summary>
    public string FieldLabel(FieldScan scan)
    {
        string axis = FieldScans.AlongX(scan) ? "X" : "Y";
        string unit = FieldTypes.IsAngle(System.Fields.Type) ? "°" : ParaxialReport.UnitName(Unit);
        return $"{axis} field ({unit})";
    }

    public string UnitName => ParaxialReport.UnitName(Unit);

    /// <summary>Meters in lens units.</summary>
    public double Shown(double meters) => OpticsConventions.FromMeters(meters, Unit);
}

internal static class FieldScanCommon
{
    /// <summary>
    /// The scan system: the fields normalised radially (so a scan along either axis runs from
    /// the axis to the largest radial field, whatever the saved normalisation), and, when
    /// <paramref name="realImageHeightAsParaxial"/>, a real-image-height system read as
    /// paraxial image heights (OpticStudio switches real image height to field angle for
    /// distortion, "Computing distortion when using real image height as the field type", help
    /// page "Field Curvature and Distortion"; a paraxial image height is linear in the tangent
    /// of the field angle, so it is the same linear reference).
    /// </summary>
    public static OpticalSystemData ScanSystem(OpticalSystemData system, bool realImageHeightAsParaxial, List<string> notes)
    {
        var fields = system.Fields;
        if (fields.Normalization != FieldNormalization.Radial)
        {
            fields = fields with { Normalization = FieldNormalization.Radial };
            notes.Add("The field scan runs to the largest radial field (the fields are normalised radially for the scan).");
        }
        if (realImageHeightAsParaxial && fields.Type == FieldType.RealImageHeight)
        {
            fields = fields with { Type = FieldType.ParaxialImageHeight };
            notes.Add("Real image height fields are read as paraxial image heights for this analysis, so the field coordinate stays linear in the object-space tangent (OpticStudio switches to field angle instead).");
        }
        return system with { Fields = fields };
    }

    /// <summary>The setup, sizing floating apertures when a surface needs them.</summary>
    public static FieldScanSetup Prepare(SequentialLens lens, OpticalSystemData system, List<string> notes, CancellationToken cancellation)
    {
        IReadOnlyList<double>? semiDiameters = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semiDiameters = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
        return new FieldScanSetup
        {
            Lens = lens, System = system, SemiDiameters = semiDiameters, Launcher = new RayLauncher(lens, system), Notes = notes,
        };
    }

    public static OpticsAnalysisResult NoSurfaces(string name) => OpticsAnalysisResult.FromSections(name, new[]
    {
        new ReportSection("No surfaces")
        {
            Lines = new[] { "The system has no sequential surfaces: add them in the Lens Data Editor, so there is something to trace." }
        }
    }, new[] { "Nothing is traced." });

    /// <summary>0-based wavelength rows from a setting: 0 means every row when
    /// <paramref name="zeroIsAll"/>, else the primary; 1…n one row.</summary>
    public static int[] Rows(int setting, OpticalSystemData system, string who, bool zeroIsAll)
    {
        int count = system.Wavelengths.Wavelengths.Count;
        if (setting < 0 || setting > count)
            throw new ArgumentException($"{who}: the wavelength must be 0 ({(zeroIsAll ? "all" : "primary")}) or 1 to {count} (it is {setting}).");
        if (setting > 0) return new[] { setting - 1 };
        return zeroIsAll ? Enumerable.Range(0, count).ToArray() : new[] { system.Wavelengths.PrimaryIndex };
    }

    /// <summary>n evenly spaced values from 0 to <paramref name="top"/>, the last exactly <paramref name="top"/>.</summary>
    public static double[] Even(int n, double top = 1)
    {
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = i == n - 1 ? top : top * i / (n - 1);
        return v;
    }

    public static void CheckCount(int value, int minimum, int maximum, string who, string what)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentException($"{who}: the {what} must lie in {minimum}–{maximum} (it is {value}).");
    }

    /// <summary>Refuses afocal image space: the help's afocal forms (diopters, angles) are not built.</summary>
    public static void RefuseAfocal(OpticalSystemData system, string who)
    {
        if (system.Aperture.AfocalImageSpace)
            throw new InvalidOperationException($"{who}: the system is in afocal image space mode; this program has not built the afocal form of this analysis (diopters or angles instead of lengths). Turn afocal image space off to analyse the image at a finite distance.");
    }

    /// <summary>
    /// Where a ray at the image surface (local frame) crosses the local z axis, as a signed
    /// distance along z from the image surface's vertex, positive in the direction the light
    /// travels: z + t·N with t = −y/M (−x/L for a ray in the xz plane), times the sign of N. NaN
    /// when the ray runs parallel to the axis.
    /// </summary>
    public static double AxisCrossing(Vector3D p, Vector3D d)
    {
        double t;
        if (Math.Abs(d.Y) >= Math.Abs(d.X))
        {
            if (d.Y == 0) return double.NaN;
            t = -p.Y / d.Y;
        }
        else t = -p.X / d.X;
        return (p.Z + t * d.Z) * Math.Sign(d.Z);
    }

    /// <summary>The axis crossing of a paraxial ray at the image surface: −h/u in the system's
    /// frame, positive along the direction of travel (the signed index's sign).</summary>
    public static double ParaxialCrossing(ParaxialSystem p, ParaxialRay ray)
    {
        int image = p.Lens.ImageIndex;
        double h = ray.Heights[image], u = ray.Slopes[image];
        if (u == 0) return double.NaN;
        return -h / u * Math.Sign(p.SignedIndices[image]);
    }

    public static string N(double v, string format = "g8") => double.IsFinite(v) ? StudyReport.Number(v, format) : "—";

    public static string Failure(Ray ray) => $"{RayStatuses.Label(ray.Status)} at surface {ray.FailedAtSurface}";

    /// <summary>A wavelength's label, "0.5876 µm".</summary>
    public static string WavelengthLabel(OpticalSystemData system, int row) =>
        $"{StudyReport.Number(system.Wavelengths.Wavelengths[row].Micrometers, "g6")} µm";
}
