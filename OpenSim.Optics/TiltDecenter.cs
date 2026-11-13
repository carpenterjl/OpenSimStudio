using OpenSim.Core.Numerics;

namespace OpenSim.Optics;

/// <summary>Which comes first in a coordinate break or a surface tilt/decenter.</summary>
public enum TiltDecenterOrder
{
    /// <summary>Decenter x, decenter y, then tilt about x, the new y, the new z
    /// (OpticStudio's order flag 0).</summary>
    DecenterThenTilt = 0,

    /// <summary>Tilt about z, the new y, the new x, then decenter along the tilted x and y
    /// (OpticStudio's order flag ≠ 0).</summary>
    TiltThenDecenter = 1,
}

/// <summary>
/// A decenter and three tilts, with OpticStudio's meaning (docs/Optics-Conventions.md §4):
/// decenters in meters, tilts in degrees, right-handed about the positive axes, each tilt
/// taken about the axis as the earlier tilts left it.
/// <para>
/// With Rx, Ry, Rz the right-handed rotations, a point p in the new frame sits in the old
/// frame at
/// <list type="bullet">
/// <item>decenter then tilt: (dx, dy, 0) + Rx·Ry·Rz·p</item>
/// <item>tilt then decenter: Rz·Ry·Rx·((dx, dy, 0) + p)</item>
/// </list>
/// so each order with the signs reversed undoes the other (<see cref="Reversed"/>).
/// </para>
/// </summary>
public readonly record struct TiltDecenter(
    double DecenterX, double DecenterY,
    double TiltXDegrees, double TiltYDegrees, double TiltZDegrees,
    TiltDecenterOrder Order = TiltDecenterOrder.DecenterThenTilt)
{
    /// <summary>The break that changes nothing.</summary>
    public static TiltDecenter None => default;

    /// <summary>The transform from the new (tilted, decentered) frame to the old one.</summary>
    public RigidTransform ToTransform()
    {
        var (sx, cx) = OpticsConventions.SinCosDegrees(TiltXDegrees);
        var (sy, cy) = OpticsConventions.SinCosDegrees(TiltYDegrees);
        var (sz, cz) = OpticsConventions.SinCosDegrees(TiltZDegrees);
        var rx = RigidTransform.RotationAboutX(sx, cx);
        var ry = RigidTransform.RotationAboutY(sy, cy);
        var rz = RigidTransform.RotationAboutZ(sz, cz);
        var decenter = RigidTransform.FromTranslation(new Vector3D(DecenterX, DecenterY, 0));

        return Order switch
        {
            TiltDecenterOrder.DecenterThenTilt =>
                RigidTransform.Compose(decenter, RigidTransform.Compose(rx, RigidTransform.Compose(ry, rz))),
            TiltDecenterOrder.TiltThenDecenter =>
                RigidTransform.Compose(RigidTransform.Compose(rz, RigidTransform.Compose(ry, rx)), decenter),
            _ => throw new ArgumentOutOfRangeException(nameof(Order), Order, "Unknown tilt/decenter order.")
        };
    }

    /// <summary>The break that undoes this one: the other order, every value negated.</summary>
    public TiltDecenter Reversed() => new(
        -DecenterX, -DecenterY, -TiltXDegrees, -TiltYDegrees, -TiltZDegrees,
        Order == TiltDecenterOrder.DecenterThenTilt
            ? TiltDecenterOrder.TiltThenDecenter
            : TiltDecenterOrder.DecenterThenTilt);

    /// <summary>
    /// The tilts and decenters, in the asked order, that reproduce a transform. A tilt and
    /// decenter has no shift along z, so the part of the transform it cannot hold comes back
    /// in <paramref name="axialShift"/>: the distance along the old z (decenter then tilt) or
    /// the new z (tilt then decenter) to add as a thickness before the break.
    /// When the y tilt is ±90° the x and z tilts turn about the same line; the z tilt is then
    /// reported as zero.
    /// </summary>
    public static TiltDecenter FromTransform(RigidTransform transform, TiltDecenterOrder order,
        out double axialShift)
    {
        switch (order)
        {
            case TiltDecenterOrder.DecenterThenTilt:
            {
                var (ax, ay, az) = EulerXyz(transform);
                axialShift = transform.Translation.Z;
                return new TiltDecenter(transform.Translation.X, transform.Translation.Y, ax, ay, az, order);
            }
            case TiltDecenterOrder.TiltThenDecenter:
            {
                // Rz·Ry·Rx is the transpose of Rx(−a)·Ry(−b)·Rz(−c).
                var (ax, ay, az) = EulerXyz(transform.Inverse());
                var d = transform.InverseApplyDirection(transform.Translation);
                axialShift = d.Z;
                return new TiltDecenter(d.X, d.Y, -ax, -ay, -az, order);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown tilt/decenter order.");
        }
    }

    /// <summary>Angles (degrees) a, b, c with R = Rx(a)·Ry(b)·Rz(c).</summary>
    private static (double X, double Y, double Z) EulerXyz(RigidTransform t)
    {
        // R13 = sin b, R23 = −sin a·cos b, R33 = cos a·cos b, R11 = cos b·cos c, R12 = −cos b·sin c.
        double r11 = t.Col0.X, r12 = t.Col1.X, r13 = t.Col2.X, r23 = t.Col2.Y, r33 = t.Col2.Z;
        double cosB = Math.Sqrt(r11 * r11 + r12 * r12);
        double b = Math.Atan2(r13, cosB);
        double a, c;
        if (cosB > 1e-12)
        {
            a = Math.Atan2(-r23, r33);
            c = Math.Atan2(-r12, r11);
        }
        else
        {
            // cos b = 0: only a ± c is determined. With c = 0, R22 = cos a and R32 = sin a.
            a = Math.Atan2(t.Col1.Z, t.Col1.Y);
            c = 0;
        }
        const double deg = 180.0 / Math.PI;
        return (a * deg, b * deg, c * deg);
    }
}
