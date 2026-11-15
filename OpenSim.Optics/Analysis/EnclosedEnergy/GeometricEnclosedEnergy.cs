using OpenSim.Core.Numerics;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis.EnclosedEnergy;

/// <summary>The rays of one field's geometric enclosed energy where they meet the surface, with
/// the reference point and what did not arrive (OP-41).</summary>
public sealed record GeometricEnclosedEnergySpot
{
    /// <summary>The rays that passed the surface: position (meters, the surface's local frame)
    /// and weight (wavelength weight × pupil apodization A²).</summary>
    public required IReadOnlyList<SpotPoint> Points { get; init; }

    /// <summary>The 0-based wavelength row of each point.</summary>
    public required IReadOnlyList<int> WaveOf { get; init; }

    public required double ReferenceX { get; init; }
    public required double ReferenceY { get; init; }
    public required EnclosedEnergyReference Reference { get; init; }

    /// <summary>The chief ray's (x, y) on the surface (apertures ignored), or null when it failed.</summary>
    public (double X, double Y)? Chief { get; init; }

    public int Field { get; init; }
    public IReadOnlyList<int> Wavelengths { get; init; } = Array.Empty<int>();
    public int Surface { get; init; }

    /// <summary>Rays launched, and those that did not pass, by status and surface.</summary>
    public int Launched { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

    /// <summary>The fraction of the weight within <paramref name="size"/> of the reference.</summary>
    public double Fraction(EnclosedEnergyType type, double size) =>
        GeometricEnclosedEnergy.Fraction(Points, ReferenceX, ReferenceY, type, size);

    public double[] Fractions(EnclosedEnergyType type, IReadOnlyList<double> sizes) =>
        GeometricEnclosedEnergy.Fractions(Points, ReferenceX, ReferenceY, type, sizes);

    /// <summary>The largest distance of a ray from the reference in the region's metric: the
    /// size that holds every ray.</summary>
    public double MaximumDistance(EnclosedEnergyType type) =>
        Points.Max(p => EnclosedEnergyRegions.Distance(type, p.X - ReferenceX, p.Y - ReferenceY));
}

/// <summary>
/// OpticStudio's Geometric Enclosed Energy and Geometric Line/Edge Spread (OP-41; help,
/// "Geometric": "Computes encircled energy using ray-image surface intercepts"; "The X- and Y-only
/// options will compute the fraction of rays which are contained with plus or minus the specified
/// distance from either the chief ray or the image centroid"). Rays on an n × n grid of pupil
/// nodes inside the unit circle are traced to the surface (<see cref="RaySampler"/>); each ray that
/// passes carries its wavelength's weight (1 for one wavelength) times the pupil apodization's
/// intensity A(ρ)², so the grid's equal pupil areas carry the light the pupil gives them. The
/// fraction at a size counts the rays at a distance at most that size. Public so that other items
/// (sensitivity criteria) can ask for the fraction at one size.
/// </summary>
public static class GeometricEnclosedEnergy
{
    /// <summary>The seed of the shuffle in <see cref="SmallestEnclosingCircle"/> (any fixed value:
    /// the circle does not depend on the order, only the work does).</summary>
    public const long ShuffleSeed = 0x0041_E11C_1E5E;

    /// <summary>Σ wᵢ over the points within <paramref name="size"/> of (rx, ry) in the region's
    /// metric (<see cref="EnclosedEnergyRegions.Distance"/>, inclusive), over Σ wᵢ.</summary>
    public static double Fraction(IReadOnlyList<SpotPoint> points, double rx, double ry, EnclosedEnergyType type, double size) =>
        Fractions(points, rx, ry, type, new[] { size })[0];

    /// <summary><see cref="Fraction"/> at each size, from one sort of the distances.</summary>
    public static double[] Fractions(IReadOnlyList<SpotPoint> points, double rx, double ry, EnclosedEnergyType type, IReadOnlyList<double> sizes)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(sizes);
        if (points.Count == 0) throw new ArgumentException("Geometric enclosed energy: no ray reached the surface.", nameof(points));
        var d = new double[points.Count];
        var w = new double[points.Count];
        double total = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!(p.Weight >= 0) || !double.IsFinite(p.Weight) || !double.IsFinite(p.X) || !double.IsFinite(p.Y))
                throw new ArgumentException($"Geometric enclosed energy: ray {i + 1} has position ({p.X:g6}, {p.Y:g6}) and weight {p.Weight:g6}; all must be finite and the weight not negative.", nameof(points));
            d[i] = EnclosedEnergyRegions.Distance(type, p.X - rx, p.Y - ry);
            w[i] = p.Weight;
            total += p.Weight;
        }
        if (!(total > 0)) throw new ArgumentException("Geometric enclosed energy: every ray weighs zero.", nameof(points));
        Array.Sort(d, w);
        var cumulative = new double[d.Length];
        double run = 0;
        for (int i = 0; i < d.Length; i++) { run += w[i]; cumulative[i] = run; }
        var result = new double[sizes.Count];
        for (int s = 0; s < sizes.Count; s++)
        {
            double size = sizes[s];
            if (!(size >= 0) || !double.IsFinite(size))
                throw new ArgumentOutOfRangeException(nameof(sizes), size, "Geometric enclosed energy: a size must be finite and not negative.");
            // The number of distances ≤ size.
            int lo = 0, hi = d.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (d[mid] <= size) lo = mid + 1; else hi = mid;
            }
            result[s] = lo == 0 ? 0 : lo == d.Length ? 1 : cumulative[lo - 1] / total;
        }
        return result;
    }

    /// <summary>
    /// The smallest circle holding every point (the help's "middle": "the coordinate upon which
    /// the smallest circle enclosing all the ray intercepts is centered"), by the incremental
    /// (Welzl-type) algorithm over a fixed-seed shuffle: a point outside the current circle lies
    /// on the boundary of the smallest circle of the points so far, so the circle is rebuilt
    /// through it and at most two more boundary points. "Outside" allows 10⁻¹² of the radius.
    /// </summary>
    public static (double X, double Y, double Radius) SmallestEnclosingCircle(IReadOnlyList<SpotPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) throw new ArgumentException("Smallest enclosing circle: no points.", nameof(points));
        var p = points.Select(q => (q.X, q.Y)).ToArray();
        var random = new SplitMix64(ShuffleSeed);
        for (int i = p.Length - 1; i > 0; i--)
        {
            int j = (int)(random.Next() % (ulong)(i + 1));
            (p[i], p[j]) = (p[j], p[i]);
        }
        double scale = p.Max(q => Math.Max(Math.Abs(q.X), Math.Abs(q.Y)));
        double slack = 1e-12 * Math.Max(scale, double.Epsilon);
        bool Outside((double X, double Y) c, double r, (double X, double Y) q) => Hypot(q.X - c.X, q.Y - c.Y) > r + slack + 1e-12 * r;
        (double X, double Y) c = p[0];
        double r = 0;
        for (int i = 1; i < p.Length; i++)
        {
            if (!Outside(c, r, p[i])) continue;
            c = p[i]; r = 0;
            for (int j = 0; j < i; j++)
            {
                if (!Outside(c, r, p[j])) continue;
                c = ((p[i].X + p[j].X) / 2, (p[i].Y + p[j].Y) / 2);
                r = Hypot(p[i].X - c.X, p[i].Y - c.Y);
                for (int k = 0; k < j; k++)
                {
                    if (!Outside(c, r, p[k])) continue;
                    (c, r) = Circumcircle(p[i], p[j], p[k]);
                }
            }
        }
        return (c.X, c.Y, r);
    }

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>The circle through three points; for (nearly) collinear points, the circle on
    /// the farthest pair as diameter.</summary>
    private static ((double X, double Y), double) Circumcircle((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        double bx = b.X - a.X, by = b.Y - a.Y, cx = c.X - a.X, cy = c.Y - a.Y;
        double d = 2 * (bx * cy - by * cx);
        double b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
        if (Math.Abs(d) <= 1e-14 * Math.Max(b2, c2))
        {
            var pairs = new[] { (a, b), (a, c), (b, c) };
            var far = pairs.OrderByDescending(q => Hypot(q.Item1.X - q.Item2.X, q.Item1.Y - q.Item2.Y)).First();
            var mid = ((far.Item1.X + far.Item2.X) / 2, (far.Item1.Y + far.Item2.Y) / 2);
            return (mid, Hypot(far.Item1.X - mid.Item1, far.Item1.Y - mid.Item2));
        }
        double ux = (cy * b2 - by * c2) / d, uy = (bx * c2 - cx * b2) / d;
        return ((a.X + ux, a.Y + uy), Hypot(ux, uy));
    }

    /// <summary>The reference point of a set of rays: the chief ray, the weighted centroid, the
    /// vertex (0, 0) or the middle.</summary>
    public static (double X, double Y) ReferencePoint(IReadOnlyList<SpotPoint> points, EnclosedEnergyReference reference, (double X, double Y)? chief, string who)
    {
        ArgumentNullException.ThrowIfNull(points);
        switch (reference)
        {
            case EnclosedEnergyReference.ChiefRay:
                return chief ?? throw new InvalidOperationException($"{who}: the chief ray did not reach the surface, so there is no chief-ray reference; choose the centroid, the middle or the vertex.");
            case EnclosedEnergyReference.Vertex:
                return (0, 0);
            case EnclosedEnergyReference.Middle:
            {
                var (x, y, _) = SmallestEnclosingCircle(points);
                return (x, y);
            }
            case EnclosedEnergyReference.Centroid:
            {
                double sw = 0, sx = 0, sy = 0;
                foreach (var p in points) { sw += p.Weight; sx += p.Weight * p.X; sy += p.Weight * p.Y; }
                if (!(sw > 0)) throw new InvalidOperationException($"{who}: the rays carry no weight, so there is no centroid.");
                return (sx / sw, sy / sw);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(reference), reference, "Unknown reference.");
        }
    }

    /// <summary>The n × n grid of pupil nodes inside the unit circle (OP-38's nodes, row by row from Py = −1).</summary>
    public static IReadOnlyList<(double Px, double Py)> PupilGrid(int sampling) => PsfEngine.Nodes(sampling).Pupil;

    /// <summary>
    /// The rays of one field (0-based row) at the given wavelength rows on an n × n pupil grid,
    /// read at <paramref name="surface"/> (null for the image), with the reference point. The
    /// chief ray is the selected wavelength's, or the primary wavelength's for several, traced
    /// with apertures ignored. A wavelength of weight zero is left out of a polychromatic set.
    /// </summary>
    public static GeometricEnclosedEnergySpot Trace(SequentialLens lens, OpticalSystemData system, int field, IReadOnlyList<int> wavelengths,
        int sampling, EnclosedEnergyReference reference, int? surface = null, int? maxDegreeOfParallelism = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(wavelengths);
        string who = $"Geometric enclosed energy (field {field + 1})";
        if (sampling < 4) throw new ArgumentException($"{who}: the pupil sampling must be at least 4 (it is {sampling}).", nameof(sampling));
        if (wavelengths.Count == 0) throw new ArgumentException($"{who}: at least one wavelength.", nameof(wavelengths));
        var pupil = PupilGrid(sampling);
        var sample = RaySampler.Trace(lens, system, new RaySampleRequest
        {
            Fields = new[] { field }, Wavelengths = wavelengths, Pupil = pupil, Surface = surface, MaxDegreeOfParallelism = maxDegreeOfParallelism,
        }, cancellation);
        var apodization = PsfEngine.Apodization(lens, system, who);
        bool poly = wavelengths.Count > 1;
        var points = new List<SpotPoint>();
        var waveOf = new List<int>();
        var all = new List<SampledRay>();
        for (int wi = 0; wi < wavelengths.Count; wi++)
        {
            double weight = poly ? system.Wavelengths.Wavelengths[wavelengths[wi]].Weight : 1;
            if (!(weight >= 0) || !double.IsFinite(weight))
                throw new ArgumentException($"{who}: wavelength {wavelengths[wi] + 1}'s weight is {weight:g6}; it must be finite and not negative.");
            if (weight == 0) continue;
            foreach (var ray in sample.Set(0, wi))
            {
                all.Add(ray);
                if (!ray.Passed) continue;
                double a = apodization(Math.Sqrt(ray.Px * ray.Px + ray.Py * ray.Py));
                if (a == 0) continue;
                points.Add(new SpotPoint(ray.Position.X, ray.Position.Y, weight * a * a));
                waveOf.Add(wavelengths[wi]);
            }
        }
        if (all.Count == 0) throw new ArgumentException($"{who}: every wavelength weight is zero, so there is no light.");
        if (points.Count == 0)
            throw new InvalidOperationException($"{who}: no ray reached surface {sample.Surface}: " + string.Join("; ", RaySampler.DescribeFailures(all)) + ".");
        var chiefRay = poly ? sample.PrimaryChiefs[0] : sample.ChiefAt(0, 0);
        (double, double)? chief = chiefRay.AtSurface is null ? null : (chiefRay.Position.X, chiefRay.Position.Y);
        var (rx, ry) = ReferencePoint(points, reference, chief, who);
        return new GeometricEnclosedEnergySpot
        {
            Points = points, WaveOf = waveOf, ReferenceX = rx, ReferenceY = ry, Reference = reference, Chief = chief,
            Field = field, Wavelengths = wavelengths.ToArray(), Surface = sample.Surface, Launched = all.Count,
            Failures = RaySampler.DescribeFailures(all),
        };
    }

    /// <summary>
    /// The geometric enclosed fraction of one field at one size (meters): the criterion form for
    /// other items. <paramref name="lens"/> and <paramref name="system"/> are one configuration's.
    /// </summary>
    public static double Fraction(SequentialLens lens, OpticalSystemData system, int field, IReadOnlyList<int> wavelengths, int sampling,
        EnclosedEnergyType type, EnclosedEnergyReference reference, double size, int? surface = null, CancellationToken cancellation = default) =>
        Trace(lens, system, field, wavelengths, sampling, reference, surface, 1, cancellation).Fraction(type, size);

    /// <summary>
    /// The geometric line and edge spread (help, "Geometric Line/Edge Spread": "The line response
    /// function (or line spread function, LSF) is the cross section of the intensity pattern of the
    /// image of a line object. The edge spread function (ESF) is the cross section of the intensity
    /// pattern of the image of an edge"; "The X- orientation means the line or edge is parallel to
    /// the X-axis"; "The zero coordinate is referenced to the chief ray"). X orientation runs along
    /// y (each ray at y − y_ref), Y along x. The coordinate axis has <paramref name="bins"/> equal
    /// bins over ±<paramref name="maxRadius"/>; the line spread is each bin's weight over the
    /// largest bin's; the edge spread at a bin's centre is the weight of the rays at or below it
    /// over the total (exact, no binning), so it runs from 0 to 1 across all the rays.
    /// </summary>
    public static (double[] Coordinate, double[] Line, double[] Edge) LineEdgeSpread(IReadOnlyList<SpotPoint> points, double rx, double ry,
        bool parallelToX, double maxRadius, int bins)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) throw new ArgumentException("Geometric line/edge spread: no ray reached the surface.", nameof(points));
        if (!(maxRadius > 0) || !double.IsFinite(maxRadius)) throw new ArgumentOutOfRangeException(nameof(maxRadius), maxRadius, "Geometric line/edge spread: the maximum radius must be positive.");
        if (bins < 2) throw new ArgumentOutOfRangeException(nameof(bins), bins, "Geometric line/edge spread: at least 2 bins.");
        double width = 2 * maxRadius / bins;
        var coordinate = new double[bins];
        for (int b = 0; b < bins; b++) coordinate[b] = -maxRadius + (b + 0.5) * width;
        var line = new double[bins];
        var u = new double[points.Count];
        var w = new double[points.Count];
        double total = 0;
        for (int i = 0; i < points.Count; i++)
        {
            u[i] = parallelToX ? points[i].Y - ry : points[i].X - rx;
            w[i] = points[i].Weight;
            total += w[i];
            int b = (int)Math.Floor((u[i] + maxRadius) / width);
            if (b == bins && u[i] <= maxRadius) b = bins - 1;
            if (b >= 0 && b < bins) line[b] += w[i];
        }
        if (!(total > 0)) throw new ArgumentException("Geometric line/edge spread: every ray weighs zero.", nameof(points));
        double max = line.Max();
        if (max > 0) for (int b = 0; b < bins; b++) line[b] /= max;
        // The edge: the weight at or below each coordinate (a one-sided cumulative sum).
        Array.Sort(u, w);
        var cumulative = new double[u.Length];
        double run = 0;
        for (int i = 0; i < u.Length; i++) { run += w[i]; cumulative[i] = run; }
        var edgeValues = new double[bins];
        for (int b = 0; b < bins; b++)
        {
            int lo = 0, hi = u.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (u[mid] <= coordinate[b]) lo = mid + 1; else hi = mid;
            }
            edgeValues[b] = lo == 0 ? 0 : cumulative[lo - 1] / total;
        }
        return (coordinate, line, edgeValues);
    }
}
