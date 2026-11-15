using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Tolerancing;

/// <summary>
/// The total indicator runout of a rolled element (help page "TRLX, TRLY, TRLR: Tolerance on Roll
/// TIR", OpticStudio 2025 R2): the element turns by α about the center of curvature of its roll
/// surface, and the TIR is read on the other ("read") surface as "the difference in sag along the
/// axis of the roll: e.g. for TRLX: TIR = z(+x) − z(−x)", at the edge of the read surface's
/// untoleranced clear aperture. This program reads that as the axial position of the rolled read
/// surface at the lateral positions ±h (h its clear semi-diameter), measured along the element's
/// original axis (docs/Optics-Conventions.md §36).
/// <para>
/// Everything is in the plane that holds the axis z and the roll direction s, in one length unit,
/// in the element's frame (the roll surface's vertex need not be at the origin). A point (s, z)
/// turns about the center C = (0, z_c) to (s cos α − (z − z_c) sin α, s sin α + (z − z_c) cos α + z_c),
/// the right-handed rotation about the axis s × z … that <see cref="TolerancePerturber"/> applies.
/// </para>
/// </summary>
public static class RollGeometry
{
    /// <summary>The sag of a conic of curvature <paramref name="c"/> and conic constant
    /// <paramref name="k"/> at radius <paramref name="u"/>; NaN where the surface does not reach.</summary>
    public static double ConicSag(double c, double k, double u)
    {
        double q = 1 - (1 + k) * c * c * u * u;
        if (q < 0) return double.NaN;
        return c * u * u / (1 + Math.Sqrt(q));
    }

    /// <summary>The TIR z′(+h) − z′(−h) of the read surface (vertex at <paramref name="readVertexZ"/>,
    /// curvature, conic) after the roll by <paramref name="alphaRadians"/> about (0, <paramref name="centerZ"/>).</summary>
    public static double Tir(double readVertexZ, double readCurvature, double readConic, double centerZ, double h, double alphaRadians)
    {
        double sin = Math.Sin(alphaRadians), cos = Math.Cos(alphaRadians);
        double Z(double target)
        {
            double S(double u) => u * cos - (readVertexZ + ConicSag(readCurvature, readConic, u) - centerZ) * sin - target;
            double w = 0.05 * h + 2 * Math.Abs(sin) * (Math.Abs(readVertexZ - centerZ) + h) + 1e-12;
            double lo = target - w, hi = target + w;
            double flo = S(lo), fhi = S(hi);
            for (int i = 0; i < 40 && (double.IsNaN(flo) || double.IsNaN(fhi) || Math.Sign(flo) == Math.Sign(fhi)); i++)
            {
                w *= 1.5;
                lo = target - w; hi = target + w;
                flo = S(lo); fhi = S(hi);
            }
            if (double.IsNaN(flo) || double.IsNaN(fhi) || Math.Sign(flo) == Math.Sign(fhi))
                throw new InvalidOperationException($"Roll TIR: the rolled read surface does not reach the lateral position {target:g6} (the surface ends before its clear semi-diameter, or the roll is too large).");
            double u = RootFinding.Brent(S, lo, hi);
            return u * sin + (readVertexZ + ConicSag(readCurvature, readConic, u) - centerZ) * cos + centerZ;
        }
        return Z(h) - Z(-h);
    }

    /// <summary>
    /// The roll angle (radians) whose TIR is <paramref name="tir"/>, by Brent's method from a
    /// bracket grown from the slope at zero. Throws, naming the cause, when the TIR does not
    /// change with the roll (the help: "An error message will be displayed if the RollSurf and the
    /// read surface have equal radius, because TIR will always be 0") or the value is too large.
    /// </summary>
    public static double AngleForTir(double readVertexZ, double readCurvature, double readConic, double centerZ, double h, double tir)
    {
        if (!(h > 0)) throw new ArgumentException("Roll TIR: the read surface's clear semi-diameter must be positive.");
        if (tir == 0) return 0;
        double F(double a) => Tir(readVertexZ, readCurvature, readConic, centerZ, h, a) - tir;
        const double probe = 1e-6;
        double slope = (Tir(readVertexZ, readCurvature, readConic, centerZ, h, probe) - Tir(readVertexZ, readCurvature, readConic, centerZ, h, -probe)) / (2 * probe);
        if (!(Math.Abs(slope) > 1e-9 * h))
            throw new InvalidOperationException("Roll TIR: the read surface's TIR does not change with the roll (it is concentric with the roll surface's center of curvature), so no roll gives the asked TIR.");
        double a0 = tir / slope, b = 2 * a0;
        for (int i = 0; i < 30 && Math.Sign(F(b)) == Math.Sign(F(0)); i++) b *= 1.5;
        if (Math.Sign(F(b)) == Math.Sign(F(0)) || Math.Abs(b) > Math.PI / 4)
            throw new InvalidOperationException($"Roll TIR: no roll up to 45° gives a TIR of {tir:g6}; the value is too large for this element.");
        return RootFinding.Brent(F, 0, b);
    }
}
