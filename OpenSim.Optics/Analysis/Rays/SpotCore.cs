using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>One spot as the spot diagrams use it: the rays that passed (meters, the surface's
/// local frame, moved by the defocus), their wavelength rows, the chief ray used as the
/// reference (or why there is none), and the radii.</summary>
internal sealed record Spot
{
    public required IReadOnlyList<SpotPoint> Points { get; init; }
    public required IReadOnlyList<int> WaveOf { get; init; }
    public (double X, double Y)? Chief { get; init; }
    public string? ChiefFailure { get; init; }
    public SpotStatistics? Statistics { get; init; }
    public string? Failure { get; init; }
    public int Launched { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
}

/// <summary>The spot computation shared by the four spot diagrams (OP-34).</summary>
internal static class SpotCore
{
    /// <summary>The (x, y) a ray reaches on the plane <paramref name="defocus"/> (meters) along
    /// the surface's local z from where it met the surface: x + dz·L/N, y + dz·M/N.</summary>
    internal static (double X, double Y) Defocused(SampledRay ray, double defocus)
    {
        if (defocus == 0) return (ray.Position.X, ray.Position.Y);
        var d = ray.Direction;
        double t = defocus / d.Z;
        if (!double.IsFinite(t))
            throw new InvalidOperationException("Spot diagram: a ray runs parallel to the focal plane, so it has no position at a defocus.");
        return (ray.Position.X + t * d.X, ray.Position.Y + t * d.Y);
    }

    /// <summary>
    /// The spot of the f-th listed field over the listed wavelengths <paramref name="waves"/>
    /// (positions in <paramref name="sample"/>.Wavelengths). Each passing ray weighs its
    /// wavelength's weight. The chief ray is the primary wavelength's
    /// (<paramref name="chiefWave"/> null) or that of the given listed wavelength.
    /// </summary>
    internal static Spot Compute(RaySample sample, OpticalSystemData system, int f, IReadOnlyList<int> waves,
        SpotReference reference, double defocus, int? chiefWave)
    {
        var points = new List<SpotPoint>();
        var waveOf = new List<int>();
        var all = new List<SampledRay>();
        foreach (int wi in waves)
        {
            // A monochromatic spot weighs its rays equally; a polychromatic one by the
            // wavelengths' weights, leaving out a wavelength of weight zero.
            double weight = waves.Count == 1 ? 1 : system.Wavelengths.Wavelengths[sample.Wavelengths[wi]].Weight;
            if (!(weight > 0)) continue;
            foreach (var ray in sample.Set(f, wi))
            {
                all.Add(ray);
                if (!ray.Passed) continue;
                var (x, y) = Defocused(ray, defocus);
                points.Add(new SpotPoint(x, y, weight));
                waveOf.Add(sample.Wavelengths[wi]);
            }
        }
        var chiefRay = chiefWave is int cw ? sample.ChiefAt(f, cw) : sample.PrimaryChiefs[f];
        (double, double)? chief = null;
        string? chiefFailure = null;
        if (chiefRay.AtSurface is null)
            chiefFailure = $"the chief ray failed ({RayStatuses.Label(chiefRay.Final).ToLowerInvariant()} at surface {chiefRay.FinalSurface}), apertures ignored";
        else
            chief = Defocused(chiefRay, defocus);

        SpotStatistics? stats = null;
        string? failure = null;
        if (points.Count == 0) failure = "no ray reached the surface";
        else if (reference == SpotReference.ChiefRay && chief is null) failure = $"no reference point: {chiefFailure}";
        else stats = SpotStatistics.Compute(points, reference, chief ?? (double.NaN, double.NaN));
        return new Spot
        {
            Points = points, WaveOf = waveOf, Chief = chief, ChiefFailure = chiefFailure, Statistics = stats, Failure = failure,
            Launched = all.Count, Failures = RaySampler.DescribeFailures(all),
        };
    }

    /// <summary>One scatter series per wavelength of the spot, in µm from (ox, oy) scaled by
    /// <paramref name="scale"/> about it.</summary>
    internal static IEnumerable<AnalysisScatterSeries> Series(Spot spot, OpticalSystemData system, double ox, double oy,
        double unitsPerMeter, string? prefix = null)
    {
        foreach (int w in spot.WaveOf.Distinct().OrderBy(w => w))
        {
            var pts = new List<AnalysisPoint>();
            for (int i = 0; i < spot.Points.Count; i++)
                if (spot.WaveOf[i] == w)
                    pts.Add(new AnalysisPoint((spot.Points[i].X - ox) * unitsPerMeter, (spot.Points[i].Y - oy) * unitsPerMeter, ""));
            yield return new AnalysisScatterSeries((prefix ?? "") + RayAnalysisSupport.WaveLabel(system, w), pts);
        }
    }

    internal static string ReferenceLabel(SpotReference r) => r switch
    {
        SpotReference.ChiefRay => "chief ray (primary wavelength)",
        SpotReference.Centroid => "centroid",
        SpotReference.Middle => "middle",
        SpotReference.Vertex => "vertex (local 0, 0)",
        _ => r.ToString(),
    };

    internal static string ReferenceAssumption(SpotReference r) =>
        $"Radii are about the {ReferenceLabel(r)}. GEO is the distance from the reference to the farthest ray; RMS is the root of the wavelength-weighted mean squared distance (OpticStudio 2025 R2 help, \"Standard Spot Diagram\"; docs/Optics-Conventions.md §25)."
        + (r == SpotReference.ChiefRay ? " The chief ray is traced with apertures ignored, so an obscured chief ray still gives a reference." : "");

    internal const string CountingAssumption =
        "Every listed wavelength traces the full pattern and each ray weighs its wavelength's weight, a wavelength of weight zero being left out of a polychromatic spot (OpticStudio thins the grids of lower-weight wavelengths instead). Rays that fail or are vignetted are not drawn nor used in the radii, as in OpticStudio; they are counted and listed by status and surface.";
}
