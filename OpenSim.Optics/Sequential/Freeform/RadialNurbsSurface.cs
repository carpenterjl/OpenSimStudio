using OpenSim.Geometry.Step.Tessellate;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Sequential.Freeform;

/// <summary>
/// OpticStudio's Radial NURBS surface (OP-25, docs/Optics-Conventions.md §55): a rational
/// B-spline curve in the y–z plane from the vertex outward, turned about the z axis (OpticStudio
/// 2025 R2 help, "Radial NURBS": "The first point is always at y = 0 and z = 0 … defined as
/// control point 0", the user's points 1 … N "extend monotonically outward in the +Y direction",
/// each with y, z and a weight w; "a NURBS curve does not actually go through the control points,
/// except for the first and last"; "The y value of the last point defined determines the maximum
/// radial clear aperture"; rays outside the curve are a ray miss; parameter 13 the number of
/// points (at least 4, at most 60), 3n + 11, 3n + 12, 3n + 13 the y, z and w of point n). "This
/// surface does not use any of the parameter columns", read as: no radius and no conic.
/// <para>
/// <b>Reading.</b> The page gives neither the curve's degree nor its knots. This program takes a
/// cubic (degree 3) curve with the clamped uniform knot vector (0, 0, 0, 0, 1/(N − 2), …,
/// (N − 3)/(N − 2), 1, 1, 1, 1) on the N + 1 points, which passes through the first and last
/// points only, as the page says. Weight 0 at the origin is not given by the page; it is 1.
/// Not checked against OpticStudio (§10 item 51). The weights must be positive and the y values
/// strictly increasing, which makes y(t) increasing (a positive-weight rational B-spline's
/// coordinate is monotone when its control values are), so each radius has one point on the
/// curve; the evaluation uses <see cref="Nurbs"/> (Piegl and Tiller's algorithms, OpenSim.Geometry).
/// </para>
/// </summary>
public sealed class RadialNurbsSurface : RadialSagShape
{
    /// <summary>The degree of the curve (this program's reading).</summary>
    public const int Degree = 3;

    /// <summary>"At least 4 points are required, and no more than 60 allowed".</summary>
    public const int MinPoints = 4, MaxPoints = 60;

    private readonly double[] _y, _z, _w;          // user points 1 … N (SI)
    private readonly double[] _hy, _hz, _hw;       // homogeneous control points 0 … N
    private readonly double[] _knots;

    /// <param name="y">y of points 1 … N, meters, strictly increasing from a positive value.</param>
    /// <param name="z">z of points 1 … N, meters.</param>
    /// <param name="w">weights of points 1 … N, positive.</param>
    public RadialNurbsSurface(IReadOnlyList<double> y, IReadOnlyList<double> z, IReadOnlyList<double> w)
    {
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(z);
        ArgumentNullException.ThrowIfNull(w);
        int n = y.Count;
        if (z.Count != n || w.Count != n)
            throw new ArgumentException($"Radial NURBS: {y.Count} y, {z.Count} z and {w.Count} w values; each control point needs all three.");
        if (n < MinPoints || n > MaxPoints)
            throw new ArgumentException($"Radial NURBS: {n} control points; at least {MinPoints} are required and no more than {MaxPoints} allowed.");
        double previous = 0;
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(y[i]) || !double.IsFinite(z[i]) || !double.IsFinite(w[i]))
                throw new ArgumentException($"Radial NURBS: control point {i + 1} ({y[i]}, {z[i]}, w {w[i]}) must be finite.");
            if (!(y[i] > previous))
                throw new ArgumentException($"Radial NURBS: control point {i + 1} has y = {y[i]:R} m, not beyond point {i}'s {previous:R} m; the points must extend monotonically outward in +y.");
            if (!(w[i] > 0))
                throw new ArgumentException($"Radial NURBS: control point {i + 1} has weight {w[i]:R}; the weights must be positive.");
            previous = y[i];
        }
        _y = y.ToArray();
        _z = z.ToArray();
        _w = w.ToArray();
        int count = n + 1;
        _hy = new double[count];
        _hz = new double[count];
        _hw = new double[count];
        _hw[0] = 1;
        for (int i = 0; i < n; i++)
        {
            _hw[i + 1] = _w[i];
            _hy[i + 1] = _w[i] * _y[i];
            _hz[i + 1] = _w[i] * _z[i];
        }
        _knots = new double[count + Degree + 1];
        int interior = count - Degree - 1;          // N − 3 interior knots
        for (int k = 0; k < _knots.Length; k++)
        {
            if (k <= Degree) _knots[k] = 0;
            else if (k >= count) _knots[k] = 1;
            else _knots[k] = (double)(k - Degree) / (interior + 1);
        }
    }

    /// <summary>y of points 1 … N, meters.</summary>
    public IReadOnlyList<double> Y => _y;

    /// <summary>z of points 1 … N, meters.</summary>
    public IReadOnlyList<double> Z => _z;

    /// <summary>Weights of points 1 … N.</summary>
    public IReadOnlyList<double> W => _w;

    /// <summary>The radius of the last point, the edge of the surface.</summary>
    public double MaxRadius => _y[^1];

    public override string TypeName => "Radial NURBS";

    /// <summary>The curve's (y, z) at parameter t and d(y, z)/dt.</summary>
    internal void Curve(double t, out double y, out double z, out double dy, out double dz)
    {
        int count = _hw.Length;
        int span = Nurbs.FindSpan(_knots, Degree, count, t);
        var n = new double[Degree + 1];
        var dn = new double[Degree + 1];
        Nurbs.BasisWithDerivative(_knots, Degree, span, t, n, dn);
        double ay = 0, az = 0, aw = 0, day = 0, daz = 0, daw = 0;
        for (int r = 0; r <= Degree; r++)
        {
            int k = span - Degree + r;
            ay += n[r] * _hy[k];
            az += n[r] * _hz[k];
            aw += n[r] * _hw[k];
            day += dn[r] * _hy[k];
            daz += dn[r] * _hz[k];
            daw += dn[r] * _hw[k];
        }
        // C = A/w, C′ = (A′ − w′C)/w (derived from A = wC).
        y = ay / aw;
        z = az / aw;
        dy = (day - daw * y) / aw;
        dz = (daz - daw * z) / aw;
    }

    public override bool TryRadialSag(double r, SequentialSurface surface, out double z, out double dzdr)
    {
        z = dzdr = double.NaN;
        if (!(r >= 0) || r > MaxRadius) return false;
        if (r == 0)
        {
            // The curve starts at the origin; its slope there from the end tangent.
            Curve(0, out _, out _, out double dy0, out double dz0);
            z = 0;
            dzdr = dz0 / dy0;
            return double.IsFinite(dzdr);
        }
        // Solve y(t) = r on [0, 1]: Newton kept inside a bisection bracket.
        double lo = 0, hi = 1, t = r / MaxRadius;
        double y = 0, zz = 0, dy = 0, dz = 0;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            Curve(t, out y, out zz, out dy, out dz);
            double g = y - r;
            if (g == 0) break;
            if (g < 0) lo = t; else hi = t;
            double next = dy > 0 ? t - g / dy : double.NaN;
            if (!(next > lo && next < hi)) next = 0.5 * (lo + hi);
            if (Math.Abs(next - t) <= 1e-16 || hi - lo <= 1e-16)
            {
                t = next;
                Curve(t, out y, out zz, out dy, out dz);
                break;
            }
            t = next;
        }
        if (!(dy > 0)) return false;
        z = zz;
        dzdr = dz / dy;
        return double.IsFinite(z) && double.IsFinite(dzdr);
    }

    /// <summary>
    /// The vertex curvature d²z/dr² at r = 0 = (y′z″ − z′y″)/y′³ at t = 0. Derived: on the clamped
    /// knot vector the curve's first two derivatives at t = 0 depend on the first three
    /// homogeneous points only. The derivative of a degree-p B-spline is a degree p − 1 B-spline
    /// with control points p(P_{i+1} − P_i)/(u_{i+p+1} − u_{i+1}); applied twice at t = 0, with
    /// u₁ = … = u_p = 0, it gives A′(0) = p(P₁ − P₀)/u_{p+1}, A″(0) = p(p − 1)/u_{p+1}·[(P₂ − P₁)/u_{p+2} − (P₁ − P₀)/u_{p+1}]
    /// applied to the homogeneous
    /// (wy, wz, w), then C = A/w, C′ = (A′ − w′C)/w, C″ = (A″ − 2w′C′ − w″C)/w (from A = wC).
    /// A curve with a slope at the vertex (a cone point) has no paraxial model and is refused.
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        const int p = Degree;
        double u1 = _knots[p + 1], u2 = _knots[p + 2];
        double D1(double[] a) => p * (a[1] - a[0]) / u1;
        double D2(double[] a) => p * (p - 1) / u1 * ((a[2] - a[1]) / u2 - (a[1] - a[0]) / u1);
        double w = _hw[0], w1 = D1(_hw), w2 = D2(_hw);
        double cy = _hy[0] / w, cz = _hz[0] / w;
        double cy1 = (D1(_hy) - w1 * cy) / w, cz1 = (D1(_hz) - w1 * cz) / w;
        double cy2 = (D2(_hy) - 2 * w1 * cy1 - w2 * cy) / w, cz2 = (D2(_hz) - 2 * w1 * cz1 - w2 * cz) / w;
        double slope = cz1 / cy1;
        if (slope != 0)
            throw new ArgumentException($"Radial NURBS: the curve leaves the vertex with slope dz/dr = {slope:R} (control point 1 is not at z = 0), so the surface has a cone point on the axis and the paraxial engine has no model of it. Paraxial data, ray aiming and solves are not available for this lens.");
        double k = (cy1 * cz2 - cz1 * cy2) / (cy1 * cy1 * cy1);
        return new ParaxialShapeEquivalent { Curvature = k };
    }
}
