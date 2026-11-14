using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>What the OP-34 ray and spot analyses share: the lens and system of the snapshot,
/// reading the field, wavelength and surface settings, labels, and the assumptions every one
/// of them states.</summary>
internal static class RayAnalysisSupport
{
    internal const double MicrometersPerMeter = 1e6;

    /// <summary>The result shown when the project has no surfaces.</summary>
    internal static OpticsAnalysisResult NoSurfaces(string name) => OpticsAnalysisResult.FromSections(name, new[]
    {
        new ReportSection("No surfaces")
        {
            Lines = new[] { "The system has no sequential surfaces: add them in the Lens Data Editor, so there is something to trace." }
        }
    }, new[] { "Nothing is traced." });

    /// <summary>0 for every field, else the 1-based field row; the 0-based rows to trace.</summary>
    internal static IReadOnlyList<int> Fields(string analysis, int setting, OpticalSystemData system)
    {
        int count = system.Fields.Points.Count;
        if (setting < 0 || setting > count)
            throw new ArgumentException($"{analysis}: the field must be 0 (all) or 1 to {count} (it is {setting}).");
        return setting == 0 ? Enumerable.Range(0, count).ToArray() : new[] { setting - 1 };
    }

    /// <summary>0 for every wavelength, else the 1-based row; the 0-based rows to trace.</summary>
    internal static IReadOnlyList<int> Wavelengths(string analysis, int setting, OpticalSystemData system)
    {
        int count = system.Wavelengths.Wavelengths.Count;
        if (setting < 0 || setting > count)
            throw new ArgumentException($"{analysis}: the wavelength must be 0 (all) or 1 to {count} (it is {setting}).");
        return setting == 0 ? Enumerable.Range(0, count).ToArray() : new[] { setting - 1 };
    }

    /// <summary>0 for the primary wavelength, else the 1-based row; the 0-based row.</summary>
    internal static int OneWavelength(string analysis, int setting, OpticalSystemData system)
    {
        int count = system.Wavelengths.Wavelengths.Count;
        if (setting < 0 || setting > count)
            throw new ArgumentException($"{analysis}: the wavelength must be 0 (primary) or 1 to {count} (it is {setting}).");
        return setting == 0 ? system.Wavelengths.PrimaryIndex : setting - 1;
    }

    /// <summary>0 for the image surface, else 1 to the image; the surface index.</summary>
    internal static int Surface(string analysis, int setting, SequentialLens lens)
    {
        int image = lens.ImageIndex;
        if (setting < 0 || setting > image)
            throw new ArgumentException($"{analysis}: the surface must be 0 (the image) or 1 to {image} (it is {setting}).");
        return setting == 0 ? image : setting;
    }

    internal static void CheckDensity(string analysis, int density, int minimum, string what)
    {
        if (density < minimum)
            throw new ArgumentException($"{analysis}: the {what} must be at least {minimum} (it is {density}).");
    }

    internal static string SurfaceName(SequentialLens lens, int j) =>
        j == lens.ImageIndex ? "the image surface" : j == lens.StopIndex ? $"surface {j} (stop)" : $"surface {j}";

    internal static string FieldLabel(OpticalSystemData system, int f)
    {
        var p = system.Fields.Points[f];
        bool angle = FieldTypes.IsAngle(system.Fields.Type);
        string u = angle ? "°" : " " + ParaxialReport.UnitName(system.Units.LensUnit);
        string V(double v) => angle ? StudyReport.Number(v, "g6") : StudyReport.Number(OpticsConventions.FromMeters(v, system.Units.LensUnit), "g6");
        return $"Field {f + 1} ({V(p.X)}, {V(p.Y)}{u})";
    }

    internal static string WaveLabel(OpticalSystemData system, int w) =>
        $"{StudyReport.Number(system.Wavelengths.Wavelengths[w].Micrometers, "g6")} µm";

    internal static string Um(double meters) => StudyReport.Number(meters * MicrometersPerMeter, "g6");

    internal static string Lens(double meters, LensUnit unit) => StudyReport.Number(OpticsConventions.FromMeters(meters, unit), "g8");

    /// <summary>The lines every ray analysis states about how its rays were launched.</summary>
    internal static List<string> LaunchAssumptions(OpticalSystemData system)
    {
        var lines = new List<string>
        {
            "Real ray trace (docs/Optics-Conventions.md §13); rays launched from normalised field and pupil coordinates (§7).",
            system.RayAiming.IsOn
                ? $"Ray aiming {RayAimingModes.Names.Label(system.RayAiming.Mode).ToLowerInvariant()}: pupil coordinates are on the stop (§16)."
                : "Ray aiming is off: pupil coordinates are on the paraxial entrance pupil of the primary wavelength.",
            system.Fields.Points.Any(p => p.IsVignetted)
                ? "The fields' vignetting factors remap the pupil coordinates before launch (§16)."
                : "No field has vignetting factors.",
        };
        if (system.Aperture.Apodization != ApodizationType.Uniform)
            lines.Add($"Pupil apodization ({ApodizationTypes.Names.Label(system.Aperture.Apodization)}) is NOT applied: the pupil is sampled and weighted uniformly (apodized sampling is not built).");
        lines.Add("Polarization and ray transmission are not considered: every ray that passes counts with its wavelength's weight.");
        return lines;
    }

    /// <summary>Closed curve of <paramref name="count"/> points on a circle, for an overlay series.</summary>
    internal static IReadOnlyList<AnalysisPoint> Circle(double cx, double cy, double radius, int count = 121)
    {
        var points = new AnalysisPoint[count];
        for (int i = 0; i < count; i++)
        {
            double a = 2 * Math.PI * i / (count - 1);
            points[i] = new AnalysisPoint(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a), "");
        }
        return points;
    }
}
