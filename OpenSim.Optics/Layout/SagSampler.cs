using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Layout;

/// <summary>
/// Where a surface is sampled for drawing. The cross-section and the lathe use the same radii,
/// so a profile in the 2-D drawing lies exactly on the 3-D body's meridian.
/// </summary>
public static class SagSampler
{
    /// <summary>
    /// Ring radii from the vertex outward, the vertex itself excluded: <paramref name="clearSamples"/>
    /// equal steps out to the clear semi-diameter, then one more ring at
    /// <paramref name="outer"/> when it lies beyond the clear edge (the outer rim of the flat).
    /// </summary>
    public static double[] Radii(double clear, double outer, int clearSamples)
    {
        if (clearSamples < 1) throw new ArgumentOutOfRangeException(nameof(clearSamples), clearSamples, "At least one sample is needed.");
        if (!(clear > 0)) throw new ArgumentOutOfRangeException(nameof(clear), clear, "The clear semi-diameter must be positive.");
        bool flat = outer > clear;
        var radii = new double[clearSamples + (flat ? 1 : 0)];
        for (int k = 1; k <= clearSamples; k++)
            radii[k - 1] = k == clearSamples ? clear : clear * k / clearSamples;
        if (flat) radii[clearSamples] = outer;
        return radii;
    }

    /// <summary>
    /// The surface's profile in its local frame along the line through the vertex at
    /// <paramref name="azimuth"/> (radians from local x; π/2 is the local y, the YZ cross-section),
    /// from −<paramref name="outer"/> to +<paramref name="outer"/>. Inside the clear aperture the
    /// points follow the sag; beyond it they lie on the flat.
    /// </summary>
    public static IReadOnlyList<Vector3D> Meridian(LayoutSurface surface, double outer, int clearSamples,
        double azimuth = Math.PI / 2)
    {
        var radii = Radii(surface.ClearSemiDiameter, outer, clearSamples);
        var (s, c) = azimuth == Math.PI / 2 ? (1.0, 0.0) : Math.SinCos(azimuth);
        var points = new List<Vector3D>(2 * radii.Length + 1);
        for (int k = radii.Length - 1; k >= 0; k--)
            points.Add(surface.LocalPoint(-radii[k] * c, -radii[k] * s));
        points.Add(surface.LocalPoint(0, 0));
        foreach (double r in radii)
            points.Add(surface.LocalPoint(r * c, r * s));
        return points;
    }
}
