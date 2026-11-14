using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential;

/// <summary>The system data with new vignetting factors, and one line per field that needs
/// the user's attention.</summary>
public sealed record SetVignettingResult
{
    public required OpticalSystemData System { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>A lens whose vignetting factors were turned into surface apertures, the system with
/// the factors cleared, and what was placed where.</summary>
public sealed record ReplacedVignetting
{
    public required SequentialLens Lens { get; init; }
    public required OpticalSystemData System { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>Fraction of unvignetted rays at fields along +y (the Vignetting Plot).</summary>
public sealed record VignettingCurve
{
    /// <summary>Normalised field Hy of each point, 0 to 1.</summary>
    public required IReadOnlyList<double> Hy { get; init; }

    /// <summary>The fraction at each point, 0 to 1.</summary>
    public required IReadOnlyList<double> Fraction { get; init; }

    /// <summary>Rays in the unit pupil at each field.</summary>
    public int RaysPerField { get; init; }
}

/// <summary>
/// OpticStudio's Set Vignetting, Clear Vignetting, Replace Vignetting With Apertures and the
/// Vignetting Plot's computation (OP-22; docs/Optics-Conventions.md §16). Ansys OpticStudio 2025
/// R2 help on ansyshelp.ansys.com: "Vignetting" (OpticStudio_Help/topics/Vignetting.html),
/// "Replace Vignetting With Apertures" and "Vignetting Plot"; the formulas are
/// <see cref="PupilVignetting"/>'s.
/// </summary>
public static class VignettingTools
{
    /// <summary>Normalised-pupil accuracy of the edge search. The help states about 0.001 %
    /// (1e-5) for OpticStudio's own; this one bisects further.</summary>
    public const double EdgeTolerance = 1e-10;

    /// <summary>
    /// Set Vignetting. The help: the factors are estimated "so that the four marginal rays in
    /// the top, bottom, left, and right edges of the pupil pass within the apertures of each
    /// surface", from the primary wavelength only; the algorithm launches a grid of rays, takes
    /// the centroid of those that pass every surface, then finds the edge of the unvignetted
    /// pupil iteratively. Here, for each field with the factors cleared and ray aiming as the
    /// system says: a 21 × 21 grid (<see cref="BatchTracer.SquareGrid"/>) is traced with the
    /// surface apertures; a ray passes when it reaches the image surface (a miss, total internal
    /// reflection or an aim that fails counts as vignetted, as the Vignetting Plot help says of
    /// rays with errors). From the centroid (or the passing ray nearest it, when the centroid
    /// does not pass), the edge is found along the vertical line through the centre (top and
    /// bottom) and the horizontal line (left and right) by scanning 64 steps to the unit circle
    /// and bisecting the first change to <see cref="EdgeTolerance"/>; the centre moves to the
    /// midpoints and the search repeats, at most four rounds, while the new centre passes. Then
    /// VDX, VDY are the last midpoints and VCX = 1 − (right − left)/2,
    /// VCY = 1 − (top − bottom)/2, so the remapped marginal rays (±1) land on the edges. The
    /// angle is set to zero. A field with no passing ray keeps its factors, with a note.
    /// </summary>
    public static SetVignettingResult SetVignetting(SequentialLens lens, OpticalSystemData system,
        int? maxDegreeOfParallelism = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        var cleared = Clear(system);
        var launcher = new RayLauncher(lens, cleared);
        var trace = TraceWithApertures(lens, cleared, maxDegreeOfParallelism, cancellation);
        var grid = BatchTracer.SquareGrid(21);
        var points = system.Fields.Points;

        var slots = ParallelSlots.Compute(points.Count, maxDegreeOfParallelism, f =>
        {
            var (hx, hy) = cleared.Fields.Normalized(f);
            bool Passes(double px, double py)
            {
                cancellation.ThrowIfCancellationRequested();
                return trace.Trace(launcher.Launch(trace, hx, hy, px, py)).IsOk;
            }
            return Solve(Passes, grid, f + 1, points[f]);
        }, cancellation);

        var notes = new List<string>();
        var updated = new FieldPoint[points.Count];
        for (int f = 0; f < points.Count; f++)
        {
            updated[f] = slots[f].Field;
            if (slots[f].Note is { } note) notes.Add(note);
        }
        return new SetVignettingResult { System = system with { Fields = system.Fields with { Points = updated } }, Notes = notes };
    }

    private static (FieldPoint Field, string? Note) Solve(Func<double, double, bool> passes,
        IReadOnlyList<(double Px, double Py)> grid, int number, FieldPoint field)
    {
        // An unvignetted field: the centre passes and every edge reaches the unit circle.
        if (passes(0, 0)
            && Edge(passes, 0, 0, 0, 1) == 1 && Edge(passes, 0, 0, 0, -1) == 1
            && Edge(passes, 0, 0, 1, 0) == 1 && Edge(passes, 0, 0, -1, 0) == 1)
            return (Cleared(field), null);

        var passing = grid.Where(p => passes(p.Px, p.Py)).ToArray();
        if (passing.Length == 0)
            return (field, $"Field {number}: no ray of a 21 × 21 pupil grid reaches the image surface, so its vignetting factors were left as they were.");
        double mx = passing.Average(p => p.Px), my = passing.Average(p => p.Py);
        double cx, cy;
        if (passes(mx, my)) (cx, cy) = (mx, my);
        else
        {
            var nearest = passing.MinBy(p => (p.Px - mx) * (p.Px - mx) + (p.Py - my) * (p.Py - my));
            (cx, cy) = (nearest.Px, nearest.Py);
        }

        // The edges along the vertical and horizontal lines through the centre; the centre moves
        // to their midpoints (where it still passes) and the search repeats.
        double top = 0, bottom = 0, left = 0, right = 0;
        for (int round = 0; round < 4; round++)
        {
            top = cy + Edge(passes, cx, cy, 0, 1) * Reach(cx, cy, 0, 1);
            bottom = cy - Edge(passes, cx, cy, 0, -1) * Reach(cx, cy, 0, -1);
            right = cx + Edge(passes, cx, cy, 1, 0) * Reach(cx, cy, 1, 0);
            left = cx - Edge(passes, cx, cy, -1, 0) * Reach(cx, cy, -1, 0);
            double nx = (right + left) / 2, ny = (top + bottom) / 2;
            if (nx == cx && ny == cy) break;
            if (!passes(nx, ny)) break;
            (cx, cy) = (nx, ny);
        }
        double vdx = (right + left) / 2, vdy = (top + bottom) / 2;
        double vcx = 1 - (right - left) / 2, vcy = 1 - (top - bottom) / 2;
        if (!(vcx < 1) || !(vcy < 1))
            return (field, $"Field {number}: the unvignetted pupil has no width along x or y, so its vignetting factors were left as they were.");
        string? note = field.TiltDegrees != 0 ? $"Field {number}: its vignetting angle ({field.TiltDegrees}°) was set to zero; the factors are found along x and y." : null;
        return (field with { DecenterX = vdx, DecenterY = vdy, CompressionX = vcx, CompressionY = vcy, TiltDegrees = 0 }, note);
    }

    /// <summary>The distance from (cx, cy) along the unit direction (dx, dy) to the unit circle.</summary>
    private static double Reach(double cx, double cy, double dx, double dy)
    {
        double b = cx * dx + cy * dy, c = cx * cx + cy * cy - 1;
        return -b + Math.Sqrt(Math.Max(0, b * b - c));
    }

    /// <summary>The fraction (0 to 1) of the way from (cx, cy) toward the unit circle along
    /// (dx, dy) at which rays stop passing: 1 when the rim passes; else the first change found by
    /// a 64-step scan, bisected to <see cref="EdgeTolerance"/>.</summary>
    private static double Edge(Func<double, double, bool> passes, double cx, double cy, double dx, double dy)
    {
        double reach = Reach(cx, cy, dx, dy);
        if (!(reach > 0)) return 0;
        bool At(double t) => passes(cx + t * reach * dx, cy + t * reach * dy);
        const int steps = 64;
        double inside = 0;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            if (At(t)) { inside = t; continue; }
            double outside = t;
            while ((outside - inside) * reach > EdgeTolerance)
            {
                double mid = 0.5 * (inside + outside);
                if (At(mid)) inside = mid; else outside = mid;
            }
            return inside;
        }
        return 1;
    }

    /// <summary>Clear Vignetting: every field's factors back to zero.</summary>
    public static OpticalSystemData Clear(OpticalSystemData system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return system with { Fields = system.Fields with { Points = system.Fields.Points.Select(Cleared).ToArray() } };
    }

    private static FieldPoint Cleared(FieldPoint p) =>
        p with { DecenterX = 0, DecenterY = 0, CompressionX = 0, CompressionY = 0, TiltDegrees = 0 };

    /// <summary>
    /// Replace Vignetting With Apertures. The help says only that the tool "turns off
    /// vignetting factors and places surface apertures"; its algorithm is not described, so
    /// this one is the program's own (docs/Optics-Conventions.md §16): for every defined field
    /// at the primary wavelength, <paramref name="rimPoints"/> rays round the rim of the
    /// vignetted pupil (the factors applied) are traced with apertures ignored; every surface
    /// between the object and the image that has no aperture and is not ignored gets a circular
    /// aperture of the largest radial height those rays reach there. Surfaces with their own
    /// aperture keep it. The factors are then cleared. With no vignetted field nothing changes.
    /// </summary>
    public static ReplacedVignetting ReplaceWithApertures(SequentialLens lens, OpticalSystemData system, int rimPoints = 64,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        if (rimPoints < 4) throw new ArgumentOutOfRangeException(nameof(rimPoints), rimPoints, "At least four rim points are needed.");
        if (system.Fields.Points.All(p => VignettingFactors.Of(p).IsZero))
            return new ReplacedVignetting { Lens = lens, System = system, Notes = new[] { "No field has vignetting factors; nothing was replaced." } };

        var launcher = new RayLauncher(lens, system);
        var trace = SequentialTrace.Create(lens, system);
        var options = new TraceOptions { IgnoreApertures = true };
        var heights = new double[lens.Surfaces.Count];
        var notes = new List<string>();
        for (int f = 0; f < system.Fields.Points.Count; f++)
        {
            var (hx, hy) = system.Fields.Normalized(f);
            for (int i = 0; i < rimPoints; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var (s, c) = OpticsConventions.SinCosDegrees(360.0 * i / rimPoints);
                var path = trace.TracePath(launcher.Launch(trace, hx, hy, s, c), options);
                foreach (var hit in path.Hits)
                {
                    double r = Math.Sqrt(hit.Position.X * hit.Position.X + hit.Position.Y * hit.Position.Y);
                    if (r > heights[hit.Surface]) heights[hit.Surface] = r;
                }
                if (!path.Final.IsOk)
                    notes.Add($"Field {f + 1}, rim ray ({s:g4}, {c:g4}): {RayStatuses.Label(path.Status)} at surface {path.FailedAtSurface}; the apertures after it are sized without this ray.");
            }
        }
        var surfaces = lens.Surfaces.ToArray();
        for (int j = 1; j < lens.ImageIndex; j++)
        {
            var s = surfaces[j];
            if (s.Aperture is not null || s.IsIgnored || !(heights[j] > 0)) continue;
            surfaces[j] = s with { Aperture = new CircularAperture(0, heights[j]) };
            notes.Add($"Surface {j}: circular aperture of radius {heights[j]:g8} m.");
        }
        return new ReplacedVignetting { Lens = lens with { Surfaces = surfaces }, System = Clear(system), Notes = notes };
    }

    /// <summary>
    /// The Vignetting Plot's numbers. The help: fractional vignetting is "the percentage of
    /// rays incident upon the entrance pupil which pass all obscurations and apertures in the
    /// system and survive to the image surface, normalized to relative pupil area"; a grid of
    /// (2n + 1) × (2n + 1) rays at each field, primary wavelength, positive y fields; rays that
    /// miss a surface or are totally internally reflected count as vignetted. Here the grid is
    /// (i/n, k/n) for i, k from −n to n, those inside the unit circle kept; fields are Hy = 0 to
    /// 1 in <paramref name="fieldPoints"/> even steps at Hx = 0. With the factors kept, the rays
    /// are launched into the remapped pupil and the fraction is multiplied by the area it keeps,
    /// (1 − VCX)(1 − VCY), this program's reading of "normalized to relative pupil area".
    /// </summary>
    public static VignettingCurve Curve(SequentialLens lens, OpticalSystemData system, int rayDensity, int fieldPoints,
        int? maxDegreeOfParallelism = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        if (rayDensity < 1) throw new ArgumentOutOfRangeException(nameof(rayDensity), rayDensity, "The ray density must be at least 1.");
        if (fieldPoints < 2) throw new ArgumentOutOfRangeException(nameof(fieldPoints), fieldPoints, "At least two field points are needed.");
        var launcher = new RayLauncher(lens, system);
        var trace = TraceWithApertures(lens, system, maxDegreeOfParallelism, cancellation);
        var pupil = new List<(double, double)>();
        for (int i = -rayDensity; i <= rayDensity; i++)
            for (int k = -rayDensity; k <= rayDensity; k++)
            {
                double px = (double)k / rayDensity, py = (double)i / rayDensity;
                if (px * px + py * py <= 1) pupil.Add((px, py));
            }
        var fractions = ParallelSlots.Compute(fieldPoints, maxDegreeOfParallelism, f =>
        {
            double hy = (double)f / (fieldPoints - 1);
            int passed = 0;
            foreach (var (px, py) in pupil)
            {
                cancellation.ThrowIfCancellationRequested();
                if (trace.Trace(launcher.Launch(trace, 0, hy, px, py)).IsOk) passed++;
            }
            return (double)passed / pupil.Count * launcher.VignettingAt(0, hy).AreaFraction;
        }, cancellation);
        return new VignettingCurve
        {
            Hy = Enumerable.Range(0, fieldPoints).Select(f => (double)f / (fieldPoints - 1)).ToArray(),
            Fraction = fractions,
            RaysPerField = pupil.Count,
        };
    }

    private static SequentialTrace TraceWithApertures(SequentialLens lens, OpticalSystemData system,
        int? maxDegreeOfParallelism, CancellationToken cancellation)
    {
        IReadOnlyList<double>? semiDiameters = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semiDiameters = SemiDiameterSizing.Compute(lens, system, maxDegreeOfParallelism: maxDegreeOfParallelism,
                cancellation: cancellation).Clear;
        return SequentialTrace.Create(lens, system, null, semiDiameters);
    }
}
