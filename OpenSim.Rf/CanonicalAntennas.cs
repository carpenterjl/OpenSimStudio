using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>
/// Textbook antenna generators — the in-app validation pieces (a dipole lands on the
/// classic 73 + j42.5 Ω; a small loop on R = 320π⁴(A/λ²)²; a λ/4 monopole over ground on
/// 36.5 + j21 Ω) and the fastest way to a working simulation without importing a board.
/// The monopole is solved directly against a <see cref="GroundPlane"/> via the solver's
/// image pass (the old "model a full dipole and halve Zin" workaround is gone).
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

    /// <summary>A straight vertical monopole from the origin up to (0, 0, height) —
    /// solve it with a <see cref="GroundPlane"/> at z = 0 so the base end grounds and the
    /// feed lands at the base (<see cref="WireStructure.NearestBasis"/> of the origin).</summary>
    public static IReadOnlyList<WireSegment> Monopole(double height, double wireRadius)
    {
        if (height <= 0 || wireRadius <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Monopole height and radius must be positive.");
        return new[]
        {
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0, 0, height), wireRadius)
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
