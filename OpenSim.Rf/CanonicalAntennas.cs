using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>
/// Textbook antenna generators — the in-app validation pieces (a dipole lands on the
/// classic 73 + j42.5 Ω; a small loop on R = 320π⁴(A/λ²)²) and the fastest way to a
/// working simulation without importing a board. A monopole over a ground plane is
/// deliberately NOT generated: model the equivalent full dipole and halve the input
/// impedance — the consumer must state that, never silently approximate.
/// </summary>
public static class CanonicalAntennas
{
    /// <summary>A straight dipole along z, centered at the origin (feed belongs at the
    /// center node — <see cref="WireStructure.NearestBasis"/> of the origin).</summary>
    public static IReadOnlyList<WireSegment> Dipole(double length, double wireRadius)
    {
        if (length <= 0 || wireRadius <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Dipole length and radius must be positive.");
        return new[]
        {
            new WireSegment(new Vector3D(0, 0, -length / 2), new Vector3D(0, 0, length / 2), wireRadius)
        };
    }

    /// <summary>A regular polygon loop in the xy plane, centered at the origin, first
    /// vertex on +x. The polygon AREA (not the circumscribed circle's) is what enters
    /// the small-loop radiation-resistance formula.</summary>
    public static IReadOnlyList<WireSegment> Loop(double loopRadius, double wireRadius, int sides = 16)
    {
        if (loopRadius <= 0 || wireRadius <= 0)
            throw new ArgumentOutOfRangeException(nameof(loopRadius), "Loop and wire radii must be positive.");
        if (sides < 3)
            throw new ArgumentOutOfRangeException(nameof(sides), "A loop needs at least 3 sides.");

        var segments = new WireSegment[sides];
        for (int i = 0; i < sides; i++)
        {
            double angleA = 2 * Math.PI * i / sides;
            double angleB = 2 * Math.PI * (i + 1) / sides;
            segments[i] = new WireSegment(
                new Vector3D(loopRadius * Math.Cos(angleA), loopRadius * Math.Sin(angleA), 0),
                new Vector3D(loopRadius * Math.Cos(angleB), loopRadius * Math.Sin(angleB), 0),
                wireRadius);
        }
        return segments;
    }

    /// <summary>The exact area of the polygon loop <see cref="Loop"/> generates — for
    /// comparing against the small-loop R = 320π⁴(A/λ²)² without a circle approximation.</summary>
    public static double LoopArea(double loopRadius, int sides = 16) =>
        0.5 * sides * loopRadius * loopRadius * Math.Sin(2 * Math.PI / sides);
}
