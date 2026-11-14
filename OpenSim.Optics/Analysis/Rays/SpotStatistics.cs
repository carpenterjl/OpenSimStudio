using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis.Rays;

/// <summary>The point a spot diagram's radii are measured from (OpticStudio's "Refer To").</summary>
public enum SpotReference
{
    /// <summary>The real chief ray at the primary wavelength (OpticStudio's default).</summary>
    ChiefRay = 0,
    /// <summary>The weighted mean position of the rays traced.</summary>
    Centroid = 1,
    /// <summary>The point that makes the largest ray errors equal in ±x and in ±y.</summary>
    Middle = 2,
    /// <summary>The surface's local origin (0, 0).</summary>
    Vertex = 3,
}

/// <summary>One ray of a spot: its position and its weight (the wavelength's weight).</summary>
public readonly record struct SpotPoint(double X, double Y, double Weight);

/// <summary>The radii of one spot about its reference point, in the units of the points.</summary>
public sealed record SpotStatistics
{
    public required int Count { get; init; }
    public required double ReferenceX { get; init; }
    public required double ReferenceY { get; init; }
    public required double CentroidX { get; init; }
    public required double CentroidY { get; init; }

    /// <summary>√(Σ wᵢ rᵢ² / Σ wᵢ), rᵢ the distance of ray i from the reference.</summary>
    public required double Rms { get; init; }
    /// <summary>√(Σ wᵢ (xᵢ − x_ref)² / Σ wᵢ).</summary>
    public required double RmsX { get; init; }
    /// <summary>√(Σ wᵢ (yᵢ − y_ref)² / Σ wᵢ).</summary>
    public required double RmsY { get; init; }
    /// <summary>The largest rᵢ: the radius of the circle about the reference that holds every ray.</summary>
    public required double Geo { get; init; }

    /// <summary>
    /// The definitions of OpticStudio's spot diagram (Ansys OpticStudio 2025 R2 help, "Standard
    /// Spot Diagram", ansyshelp.ansys.com): the GEO radius is "the distance from the reference
    /// point ... to the ray which is farthest away from the reference point"; the RMS radius is
    /// the root-mean-square radial size, "The distance between each ray and the reference point
    /// is squared, and averaged over all the rays, and then the square root is taken", and it
    /// "considers the wavelength weighting"; "The centroid is defined by the average position of
    /// the rays traced. The middle is defined so that the maximum ray errors are equal in the
    /// plus and minus x and y directions. The vertex is defined by the local coordinates 0,0".
    /// Here the average and the centroid are weighted by <see cref="SpotPoint.Weight"/> (equal
    /// weights give the plain mean). <paramref name="chief"/> is the chief ray's position, used
    /// for <see cref="SpotReference.ChiefRay"/>. Throws when there are no points.
    /// </summary>
    public static SpotStatistics Compute(IReadOnlyList<SpotPoint> points, SpotReference reference, (double X, double Y) chief)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) throw new ArgumentException("A spot needs at least one ray.", nameof(points));
        double sw = 0, sx = 0, sy = 0;
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var p in points)
        {
            if (!(p.Weight > 0) || !double.IsFinite(p.Weight))
                throw new ArgumentException($"A spot ray's weight must be positive and finite (it is {p.Weight}).", nameof(points));
            sw += p.Weight; sx += p.Weight * p.X; sy += p.Weight * p.Y;
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        double cx = sx / sw, cy = sy / sw;
        var (rx, ry) = reference switch
        {
            SpotReference.ChiefRay => chief,
            SpotReference.Centroid => (cx, cy),
            SpotReference.Middle => (0.5 * (minX + maxX), 0.5 * (minY + maxY)),
            SpotReference.Vertex => (0.0, 0.0),
            _ => throw new ArgumentOutOfRangeException(nameof(reference), reference, "Unknown spot reference."),
        };
        double sxx = 0, syy = 0, geo = 0;
        foreach (var p in points)
        {
            double dx = p.X - rx, dy = p.Y - ry;
            sxx += p.Weight * dx * dx;
            syy += p.Weight * dy * dy;
            geo = Math.Max(geo, Math.Sqrt(dx * dx + dy * dy));
        }
        return new SpotStatistics
        {
            Count = points.Count, ReferenceX = rx, ReferenceY = ry, CentroidX = cx, CentroidY = cy,
            Rms = Math.Sqrt((sxx + syy) / sw), RmsX = Math.Sqrt(sxx / sw), RmsY = Math.Sqrt(syy / sw), Geo = geo,
        };
    }
}

/// <summary>A real working F-number, or why it could not be found.</summary>
public readonly record struct WorkingFNumberResult(double Value, string? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// OpticStudio's working F/# and Airy disk radius (Ansys OpticStudio 2025 R2 help,
/// ansyshelp.ansys.com; copies in <c>plans/sources/optics/op-34-help/</c>):
/// <list type="bullet">
/// <item>"Working F/#": W = 1/(2 n sin θ), "where θ is the real marginal ray angle in image space
/// and n is the index of refraction of image space. The marginal ray is traced at the specified
/// conjugates. Working F/# ignores surface apertures but considers vignetting factors"; "For off
/// axis field points or non-axial systems, working F/# is determined by the average of the
/// square of the numerical aperture between the axis ray and four marginal rays, at the top,
/// bottom, left, and right side of the vignetted pupil"; and it is capped at 10,000.</item>
/// <item>"Standard Spot Diagram": the Airy disk radius "is given by 1.22 times the wavelength
/// (primary wavelength is used if polychromatic) times the F/# of the beam".</item>
/// </list>
/// Here θ is the angle between the field's chief ray (the "axis ray") and each of the marginal
/// rays (0, ±1) and (±1, 0) at the image surface, NA = n sin θ with n the image space's index
/// (relative to the system's air), and W = 1/(2√(mean NA²)). The help's fallback when a marginal
/// ray cannot be traced (a smaller pupil, scaled up) is not built: the result says which ray
/// failed instead.
/// </summary>
public static class RealWorkingFNumber
{
    /// <summary>The help's cap: "OpticStudio will automatically "cap" the F/# at 10,000".</summary>
    public const double Cap = 10000;

    /// <summary>The help's factor of the Airy radius, 1.22 (Standard Spot Diagram).</summary>
    public const double AiryFactor = 1.22;

    /// <summary>The working F/# at normalised field (hx, hy) for <paramref name="trace"/>'s wavelength.</summary>
    public static WorkingFNumberResult Compute(SequentialTrace trace, RayLauncher launcher, double hx, double hy)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(launcher);
        var options = new TraceOptions { IgnoreApertures = true };
        var chief = trace.Trace(launcher.Launch(trace, hx, hy, 0, 0), options);
        if (!chief.IsOk)
            return new(double.NaN, $"the chief ray failed ({RayStatuses.Label(chief.Status).ToLowerInvariant()} at surface {chief.FailedAtSurface})");
        double n = trace.Indices[trace.Lens.ImageIndex];
        double sum = 0;
        foreach (var (px, py, name) in new[] { (0.0, 1.0, "top"), (0.0, -1.0, "bottom"), (-1.0, 0.0, "left"), (1.0, 0.0, "right") })
        {
            var marginal = trace.Trace(launcher.Launch(trace, hx, hy, px, py), options);
            if (!marginal.IsOk)
                return new(double.NaN, $"the {name} marginal ray failed ({RayStatuses.Label(marginal.Status).ToLowerInvariant()} at surface {marginal.FailedAtSurface})");
            double sin = Vector3D.Cross(chief.Direction, marginal.Direction).Length;
            double na = n * sin;
            sum += na * na;
        }
        double meanNa = Math.Sqrt(sum / 4);
        double w = meanNa > 0 ? 1 / (2 * meanNa) : double.PositiveInfinity;
        return new(Math.Min(w, Cap), null);
    }

    /// <summary>1.22 λ W, in the units of <paramref name="wavelength"/>.</summary>
    public static double AiryRadius(double wavelength, double workingFNumber) => AiryFactor * wavelength * workingFNumber;
}
