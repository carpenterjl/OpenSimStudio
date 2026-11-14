using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential;

/// <summary>What a batch trace covers: fields, wavelengths (0-based rows; null for all) and
/// normalised pupil points.</summary>
public sealed record BatchTraceRequest
{
    public IReadOnlyList<int>? Fields { get; init; }
    public IReadOnlyList<int>? Wavelengths { get; init; }
    public required IReadOnlyList<(double Px, double Py)> Pupil { get; init; }
    public TraceOptions Options { get; init; }

    /// <summary>Clear semi-diameters for floating apertures on surfaces without a fixed one;
    /// null sizes them automatically when a floating aperture needs them.</summary>
    public IReadOnlyList<double>? SemiDiameters { get; init; }
}

/// <summary>
/// One ray of a batch: the ray at the image surface (or where it failed, with its status and
/// surface), and its optical path to the image surface minus the chief ray's of the same field
/// and wavelength, summed segment by segment (NaN when either ray failed).
/// </summary>
public readonly record struct TracedRay(Ray Ray, double PathFromChief);

/// <summary>The rays of a batch trace, field by wavelength by pupil point.</summary>
public sealed record BatchTraceResult
{
    public required IReadOnlyList<int> Fields { get; init; }
    public required IReadOnlyList<int> Wavelengths { get; init; }
    public required IReadOnlyList<(double Px, double Py)> Pupil { get; init; }

    /// <summary>Flat, index (field·W + wavelength)·P + pupil.</summary>
    public required IReadOnlyList<TracedRay> Rays { get; init; }

    /// <summary>The chief ray (pupil 0, 0) of each field and wavelength, index field·W + wavelength.</summary>
    public required IReadOnlyList<Ray> ChiefRays { get; init; }

    /// <summary>The ray of the f-th listed field, w-th listed wavelength and p-th pupil point.</summary>
    public TracedRay At(int f, int w, int p) => Rays[(f * Wavelengths.Count + w) * Pupil.Count + p];
}

/// <summary>
/// The real trace over fields × wavelengths × pupil points (docs/Optics-Conventions.md §13).
/// Each field and wavelength is one work item of <see cref="ParallelSlots"/>: it traces its
/// chief ray, then its pupil points in order, into its own slot, so the result is bitwise the
/// same at any degree of parallelism. Path differences against the chief ray are summed segment
/// by segment, each a difference of two nearly equal segment paths, so a long system does not
/// lose the digits that a difference of two totals would.
/// </summary>
public static class BatchTracer
{
    /// <summary>Square-grid pupil points inside the unit circle: n × n points spaced 2/n apart,
    /// centred, those with Px² + Py² ≤ 1 kept, in row order (Py falling, then Px rising).</summary>
    public static IReadOnlyList<(double Px, double Py)> SquareGrid(int n)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), n, "The grid needs at least one point per side.");
        var points = new List<(double, double)>();
        for (int i = 0; i < n; i++)
        {
            double py = 1 - (2 * i + 1.0) / n;
            for (int k = 0; k < n; k++)
            {
                double px = -1 + (2 * k + 1.0) / n;
                if (px * px + py * py <= 1) points.Add((px, py));
            }
        }
        return points;
    }

    public static BatchTraceResult Trace(SequentialLens lens, OpticalSystemData system, BatchTraceRequest request,
        int? maxDegreeOfParallelism = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);
        var fields = request.Fields ?? Enumerable.Range(0, system.Fields.Points.Count).ToArray();
        var waves = request.Wavelengths ?? Enumerable.Range(0, system.Wavelengths.Wavelengths.Count).ToArray();
        var pupil = request.Pupil ?? throw new ArgumentException("A batch trace needs pupil points.", nameof(request));
        foreach (int f in fields)
            if (f < 0 || f >= system.Fields.Points.Count)
                throw new ArgumentOutOfRangeException(nameof(request), f, $"Batch trace: field {f} is not one of the {system.Fields.Points.Count} fields (0-based).");

        var semiDiameters = request.SemiDiameters;
        if (semiDiameters is null && !request.Options.IgnoreApertures
            && lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semiDiameters = SemiDiameterSizing.Compute(lens, system, maxDegreeOfParallelism: maxDegreeOfParallelism,
                cancellation: cancellation).Clear;

        var launcher = new RayLauncher(lens, system);
        var traces = waves.Select(w => SequentialTrace.Create(lens, system, w, semiDiameters)).ToArray();
        int count = lens.Surfaces.Count, wCount = waves.Count, pCount = pupil.Count;
        var options = request.Options;

        var slots = ParallelSlots.Compute(fields.Count * wCount, maxDegreeOfParallelism, slot =>
        {
            int fi = slot / wCount, wi = slot % wCount;
            var trace = traces[wi];
            var (hx, hy) = system.Fields.Normalized(fields[fi]);
            var chiefSegments = new double[count];
            var segments = new double[count];
            var chief = trace.Trace(launcher.Launch(trace, hx, hy, 0, 0), chiefSegments, options);
            var rays = new TracedRay[pCount];
            for (int p = 0; p < pCount; p++)
            {
                cancellation.ThrowIfCancellationRequested();
                var (px, py) = pupil[p];
                var ray = trace.Trace(launcher.Launch(trace, hx, hy, px, py), segments, options);
                double difference = double.NaN;
                if (ray.IsOk && chief.IsOk)
                {
                    difference = 0;
                    for (int j = 0; j < count; j++) difference += segments[j] - chiefSegments[j];
                }
                rays[p] = new TracedRay(ray, difference);
            }
            return (chief, rays);
        }, cancellation);

        var all = new TracedRay[fields.Count * wCount * pCount];
        var chiefs = new Ray[fields.Count * wCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            chiefs[slot] = slots[slot].chief;
            Array.Copy(slots[slot].rays, 0, all, slot * pCount, pCount);
        }
        return new BatchTraceResult { Fields = fields, Wavelengths = waves, Pupil = pupil, Rays = all, ChiefRays = chiefs };
    }
}
