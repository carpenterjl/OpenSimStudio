using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis.Rays;

/// <summary>
/// One ray of a <see cref="RaySample"/> at the surface the sample looks at.
/// <para>
/// <see cref="AtSurface"/> is null when the ray never reached that surface (it failed before,
/// or could not be aimed); otherwise it is <see cref="RayStatus.Ok"/> when the ray passed the
/// surface, or the status it was stopped there with (an aperture, total internal reflection).
/// <see cref="Final"/> and <see cref="FinalSurface"/> are what became of it at the image
/// surface: <see cref="RayStatus.Ok"/> and <see cref="Ray.NoSurface"/> when it got there.
/// Position and direction are in the surface's local frame (the direction after the surface),
/// meters; NaN when the ray did not reach it.
/// </para>
/// </summary>
public readonly record struct SampledRay(double Px, double Py, RayStatus? AtSurface, RayStatus Final, int FinalSurface,
    Vector3D Position, Vector3D Direction)
{
    /// <summary>True when the ray reached the surface and passed it.</summary>
    public bool Passed => AtSurface == RayStatus.Ok;

    internal static readonly Vector3D NaNVector = new(double.NaN, double.NaN, double.NaN);
}

/// <summary>What <see cref="RaySampler.Trace"/> traces: fields and wavelengths (0-based rows),
/// pupil points, and the surface to read the rays at (null for the image surface).</summary>
public sealed record RaySampleRequest
{
    public required IReadOnlyList<int> Fields { get; init; }
    public required IReadOnlyList<int> Wavelengths { get; init; }
    public required IReadOnlyList<(double Px, double Py)> Pupil { get; init; }

    /// <summary>The surface index (1 to the image); null for the image surface.</summary>
    public int? Surface { get; init; }

    /// <summary>Trace through every aperture as if it were not there.</summary>
    public bool IgnoreApertures { get; init; }

    /// <summary>The thread limit; null for none. The result is the same bits at any value.</summary>
    public int? MaxDegreeOfParallelism { get; init; }
}

/// <summary>The rays of a <see cref="RaySampler.Trace"/>, field by wavelength by pupil point,
/// with the chief rays the analyses reference against.</summary>
public sealed record RaySample
{
    public required IReadOnlyList<int> Fields { get; init; }
    public required IReadOnlyList<int> Wavelengths { get; init; }
    public required IReadOnlyList<(double Px, double Py)> Pupil { get; init; }

    /// <summary>The surface the rays are read at.</summary>
    public required int Surface { get; init; }

    /// <summary>Flat, index (field·W + wavelength)·P + pupil.</summary>
    public required IReadOnlyList<SampledRay> Rays { get; init; }

    /// <summary>The chief ray (pupil 0, 0) of each listed field and wavelength, index
    /// field·W + wavelength, traced with apertures ignored so that an obscured or clipped chief
    /// ray still gives a reference point.</summary>
    public required IReadOnlyList<SampledRay> Chiefs { get; init; }

    /// <summary>The chief ray of each listed field at the primary wavelength, apertures ignored.</summary>
    public required IReadOnlyList<SampledRay> PrimaryChiefs { get; init; }

    /// <summary>The launcher's notes (a real stop radius that could not be traced, say).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public SampledRay At(int f, int w, int p) => Rays[(f * Wavelengths.Count + w) * Pupil.Count + p];

    public SampledRay ChiefAt(int f, int w) => Chiefs[f * Wavelengths.Count + w];

    /// <summary>The rays of the f-th listed field and w-th listed wavelength, in pupil order.</summary>
    public IEnumerable<SampledRay> Set(int f, int w)
    {
        int start = (f * Wavelengths.Count + w) * Pupil.Count;
        for (int p = 0; p < Pupil.Count; p++) yield return Rays[start + p];
    }
}

/// <summary>
/// The real trace behind the ray-fan, spot and footprint analyses (OP-34): rays from normalised
/// field and pupil coordinates, launched by <see cref="RayLauncher"/> (ray aiming and the
/// fields' vignetting factors as the system says), read at one surface. At the image surface
/// the rays come from <see cref="BatchTracer"/>; at another surface each ray is traced with
/// <see cref="SequentialTrace.TracePath"/> and its hit there is read. Both run one work item per
/// field and wavelength through <see cref="ParallelSlots"/>, so the sample is the same bits at
/// any degree of parallelism. No ray is dropped: a ray that fails keeps its status and surface,
/// and the analyses count and list it (<see cref="RaySampler.DescribeFailures"/>).
/// </summary>
public static class RaySampler
{
    public static RaySample Trace(SequentialLens lens, OpticalSystemData system, RaySampleRequest request,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);
        int image = lens.ImageIndex;
        int surface = request.Surface ?? image;
        if (surface < 1 || surface > image)
            throw new ArgumentOutOfRangeException(nameof(request), surface,
                $"Ray sample: surface {surface} is not one of the surfaces 1 to {image} (the image).");
        foreach (int f in request.Fields)
            if (f < 0 || f >= system.Fields.Points.Count)
                throw new ArgumentOutOfRangeException(nameof(request), f, $"Ray sample: field {f + 1} is not one of the {system.Fields.Points.Count} fields.");
        foreach (int w in request.Wavelengths)
            if (w < 0 || w >= system.Wavelengths.Wavelengths.Count)
                throw new ArgumentOutOfRangeException(nameof(request), w, $"Ray sample: wavelength {w + 1} is not one of the {system.Wavelengths.Wavelengths.Count} wavelengths.");

        IReadOnlyList<double>? semiDiameters = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semiDiameters = SemiDiameterSizing.Compute(lens, system, maxDegreeOfParallelism: request.MaxDegreeOfParallelism,
                cancellation: cancellation).Clear;

        var fields = request.Fields;
        var waves = request.Wavelengths;
        var pupil = request.Pupil;
        int wCount = waves.Count, pCount = pupil.Count;
        var options = new TraceOptions { IgnoreApertures = request.IgnoreApertures };
        var launcher = new RayLauncher(lens, system);
        var traces = waves.Select(w => SequentialTrace.Create(lens, system, w, semiDiameters)).ToArray();
        int primary = system.Wavelengths.PrimaryIndex;
        var primaryTrace = SequentialTrace.Create(lens, system, primary, semiDiameters);
        var chiefOptions = new TraceOptions { IgnoreApertures = true };

        SampledRay[] rays;
        if (surface == image)
        {
            var batch = BatchTracer.Trace(lens, system, new BatchTraceRequest
            {
                Fields = fields, Wavelengths = waves, Pupil = pupil, Options = options, SemiDiameters = semiDiameters,
            }, request.MaxDegreeOfParallelism, cancellation);
            rays = new SampledRay[batch.Rays.Count];
            for (int i = 0; i < rays.Length; i++)
            {
                var (px, py) = pupil[i % pCount];
                rays[i] = FromImage(batch.Rays[i].Ray, px, py, image);
            }
        }
        else
        {
            var slots = ParallelSlots.Compute(fields.Count * wCount, request.MaxDegreeOfParallelism, slot =>
            {
                int fi = slot / wCount, wi = slot % wCount;
                var trace = traces[wi];
                var (hx, hy) = system.Fields.Normalized(fields[fi]);
                var set = new SampledRay[pCount];
                for (int p = 0; p < pCount; p++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var (px, py) = pupil[p];
                    set[p] = AtSurface(trace, launcher, hx, hy, px, py, surface, options);
                }
                return set;
            }, cancellation);
            rays = new SampledRay[fields.Count * wCount * pCount];
            for (int slot = 0; slot < slots.Length; slot++) Array.Copy(slots[slot], 0, rays, slot * pCount, pCount);
        }

        cancellation.ThrowIfCancellationRequested();
        var chiefs = new SampledRay[fields.Count * wCount];
        var primaryChiefs = new SampledRay[fields.Count];
        for (int fi = 0; fi < fields.Count; fi++)
        {
            var (hx, hy) = system.Fields.Normalized(fields[fi]);
            for (int wi = 0; wi < wCount; wi++)
                chiefs[fi * wCount + wi] = AtSurface(traces[wi], launcher, hx, hy, 0, 0, surface, chiefOptions);
            primaryChiefs[fi] = AtSurface(primaryTrace, launcher, hx, hy, 0, 0, surface, chiefOptions);
        }
        return new RaySample
        {
            Fields = fields, Wavelengths = waves, Pupil = pupil, Surface = surface, Rays = rays,
            Chiefs = chiefs, PrimaryChiefs = primaryChiefs, Notes = launcher.Notes.ToArray(),
        };
    }

    /// <summary>One ray traced surface by surface and read at <paramref name="surface"/>.</summary>
    public static SampledRay AtSurface(SequentialTrace trace, RayLauncher launcher, double hx, double hy, double px, double py,
        int surface, TraceOptions options)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(launcher);
        var path = trace.TracePath(launcher.Launch(trace, hx, hy, px, py), options);
        var final = path.Final;
        foreach (var hit in path.Hits)
        {
            if (hit.Surface != surface) continue;
            return new SampledRay(px, py, hit.Status, final.Status, final.FailedAtSurface, hit.Position, hit.Direction);
        }
        if (final.IsOk)
            throw new InvalidOperationException($"Ray sample: a ray reached the image without a hit at surface {surface}; the trace did not record it.");
        return new SampledRay(px, py, null, final.Status, final.FailedAtSurface, SampledRay.NaNVector, SampledRay.NaNVector);
    }

    private static SampledRay FromImage(Ray ray, double px, double py, int image)
    {
        if (ray.IsOk) return new SampledRay(px, py, RayStatus.Ok, RayStatus.Ok, Ray.NoSurface, ray.Position, ray.Direction);
        if (ray.FailedAtSurface == image && ray.Status is RayStatus.Vignetted)
            return new SampledRay(px, py, ray.Status, ray.Status, image, ray.Position, ray.Direction);
        return new SampledRay(px, py, null, ray.Status, ray.FailedAtSurface, SampledRay.NaNVector, SampledRay.NaNVector);
    }

    /// <summary>
    /// The rays that did not pass, counted by status and surface, one phrase each ("3 stopped by
    /// an aperture at surface 4"); empty when every ray passed. A ray that passed the sampled
    /// surface but failed later is listed only when <paramref name="countLaterFailures"/>.
    /// </summary>
    public static IReadOnlyList<string> DescribeFailures(IEnumerable<SampledRay> rays, bool countLaterFailures = false)
    {
        var groups = new SortedDictionary<(int Surface, RayStatus Status), int>();
        foreach (var r in rays)
        {
            (int, RayStatus)? key = null;
            if (r.AtSurface is null) key = (r.FinalSurface, r.Final);
            else if (r.AtSurface != RayStatus.Ok) key = (r.FinalSurface, r.AtSurface.Value);
            else if (countLaterFailures && r.Final != RayStatus.Ok) key = (r.FinalSurface, r.Final);
            if (key is { } k) groups[k] = groups.TryGetValue(k, out int c) ? c + 1 : 1;
        }
        return groups.Select(g => $"{g.Value} {RayStatuses.Label(g.Key.Status).ToLowerInvariant()} at surface {g.Key.Surface}").ToArray();
    }
}
