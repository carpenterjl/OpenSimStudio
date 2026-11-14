using OpenSim.Core.Geometry2D;

namespace OpenSim.Core.Plotting;

/// <summary>One sample of a cross-section: distance from the start along the line, the point,
/// and the interpolated value there (NaN off the grid).</summary>
public readonly record struct CrossSectionSample(double Distance, double X, double Y, double Value);

/// <summary>A profile of a <see cref="GridField"/> along a straight line, by bilinear interpolation.</summary>
public static class CrossSection
{
    /// <summary><paramref name="count"/> (at least 2) evenly spaced samples from
    /// <paramref name="start"/> to <paramref name="end"/>, both included.</summary>
    public static IReadOnlyList<CrossSectionSample> Sample(GridField field, Point2 start, Point2 end, int count)
    {
        count = Math.Max(2, count);
        var samples = new CrossSectionSample[count];
        var span = end - start;
        double length = span.Length;
        for (int i = 0; i < count; i++)
        {
            double t = i == count - 1 ? 1 : (double)i / (count - 1);
            var p = start + span * t;
            samples[i] = new CrossSectionSample(length * t, p.X, p.Y, field.Interpolate(p.X, p.Y));
        }
        return samples;
    }

    /// <summary>The sample count that puts two samples in every grid spacing along the line (the
    /// profile then shows every kink of the bilinear surface), clamped to [2, <paramref name="cap"/>].</summary>
    public static int DefaultCount(GridField field, Point2 start, Point2 end, int cap = 4000)
    {
        double spacing = Math.Min(field.DeltaX, field.DeltaY);
        double n = 2 * (end - start).Length / spacing + 1;
        return (int)Math.Clamp(Math.Ceiling(double.IsFinite(n) ? n : 2), 2, cap);
    }
}
