using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Geometry.Export;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.CadExport;

/// <summary>
/// The rays of a CAD export as polylines (OP-96): each ray of the pattern traced by the real ray
/// trace (<see cref="SequentialTrace"/>, launched by <see cref="RayLauncher"/> in normalised field
/// and pupil coordinates, docs/Optics-Conventions.md §13), its intersection points in the global
/// frame on the surfaces from the first to the last exported, the surfaces marked "skip rays" left
/// out as the layouts leave them out (<see cref="RayPath.DrawnHits"/>). A ray that fails ends at
/// the surface it failed at and is kept, unless Delete Vignetted is set (never dropped silently: the
/// log counts them).
/// <para>
/// Patterns (help page "3D Viewer": "evenly distributed along the fan of the pupil, or around the
/// perimeter if Ring"): a fan of n rays at −1 + 2k/(n − 1), k = 0 … n − 1 (one ray: the chief
/// ray); a ring of n rays at angles 360°·k/n from +y; a grid of n × n points of [−1, 1]² at the same
/// spacing as the fan, those inside the unit circle.
/// </para>
/// </summary>
public static class CadRays
{
    /// <summary>The pupil points of a pattern.</summary>
    public static IReadOnlyList<(double Px, double Py)> Pupil(CadRayPattern pattern, int n)
    {
        var points = new List<(double, double)>();
        if (n <= 0 || pattern == CadRayPattern.None) return points;
        double At(int k) => n == 1 ? 0 : -1 + 2.0 * k / (n - 1);
        switch (pattern)
        {
            case CadRayPattern.YFan:
                for (int k = 0; k < n; k++) points.Add((0, At(k)));
                break;
            case CadRayPattern.XFan:
                for (int k = 0; k < n; k++) points.Add((At(k), 0));
                break;
            case CadRayPattern.XYFan:
                for (int k = 0; k < n; k++) points.Add((At(k), 0));
                for (int k = 0; k < n; k++) points.Add((0, At(k)));
                break;
            case CadRayPattern.Ring:
                for (int k = 0; k < n; k++)
                {
                    var (s, c) = OpticsConventions.SinCosDegrees(360.0 * k / n);
                    points.Add((s, c));
                }
                break;
            case CadRayPattern.Grid:
                for (int a = 0; a < n; a++)
                    for (int b = 0; b < n; b++)
                    {
                        double px = At(a), py = At(b);
                        if (px * px + py * py <= 1 + 1e-12) points.Add((px, py));
                    }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pattern), pattern, "Unknown ray pattern.");
        }
        return points;
    }

    public static IReadOnlyList<CadPolyline> Trace(SequentialLens lens, OpticalSystemData system, CadExportSettings settings,
        int first, int last, List<string> notes, CancellationToken cancellation = default)
    {
        var pupil = Pupil(settings.RayPattern, settings.NumberOfRays);
        var result = new List<CadPolyline>();
        if (pupil.Count == 0) return result;
        int fieldCount = system.Fields.Points.Count, waveCount = system.Wavelengths.Wavelengths.Count;
        if (settings.Field > fieldCount) throw new ArgumentException($"CAD export: field {settings.Field} does not exist (the system has {fieldCount}).");
        if (settings.Wavelength > waveCount) throw new ArgumentException($"CAD export: wavelength {settings.Wavelength} does not exist (the system has {waveCount}).");
        var fields = settings.Field == 0 ? Enumerable.Range(0, fieldCount).ToArray() : new[] { settings.Field - 1 };
        var waves = settings.Wavelength == 0 ? Enumerable.Range(0, waveCount).ToArray() : new[] { settings.Wavelength - 1 };
        var launcher = new RayLauncher(lens, system);
        int failed = 0, deleted = 0;
        foreach (int w in waves)
        {
            var trace = SequentialTrace.Create(lens, system, w);
            foreach (int f in fields)
            {
                var (hx, hy) = system.Fields.Normalized(f);
                foreach (var (px, py) in pupil)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = trace.TracePath(launcher.Launch(trace, hx, hy, px, py));
                    if (!path.Final.IsOk)
                    {
                        failed++;
                        if (settings.DeleteVignetted) { deleted++; continue; }
                    }
                    var points = path.DrawnHits(lens).Where(h => h.Surface >= first && h.Surface <= last).Select(h => h.GlobalPosition).ToList();
                    if (points.Count < 2) continue;
                    string name = string.Create(CultureInfo.InvariantCulture, $"Ray F{f + 1} W{w + 1} Px {px:0.###} Py {py:0.###}");
                    result.Add(new CadPolyline(name, points));
                }
            }
        }
        notes.Add($"Rays: {result.Count} polyline(s) from surface {first} to {last} ({settings.RayPattern}, {settings.NumberOfRays} per field and wavelength).");
        if (failed > 0)
            notes.Add(settings.DeleteVignetted
                ? $"Rays: {deleted} vignetted or failed ray(s) deleted (Delete Vignetted)."
                : $"Rays: {failed} ray(s) were vignetted or failed; each is exported up to the surface it stopped at.");
        return result;
    }
}
