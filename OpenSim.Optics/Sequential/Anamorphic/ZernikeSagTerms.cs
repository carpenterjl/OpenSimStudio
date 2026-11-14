using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Sequential.Anamorphic;

/// <summary>
/// The optional Zernike Standard sag terms Σ Aᵢ·Zᵢ(ρ, φ) that OpticStudio's Toroidal and Biconic
/// Zernike surfaces add to their base sag (OpticStudio 2025 R2 help, "Toroidal" and "Biconic
/// Zernike": "the Zi terms are the Zernike standard terms as described in 'Zernike Standard
/// Sag'"). From "Zernike Standard Sag": the coefficients Aᵢ are lengths (meters here), the
/// coordinates are normalised by the normalization radius, the polynomials are those of the
/// "Zernike Standard Coefficients" table (Noll's order and normalisation, <see cref="ZernikeBasis"/>
/// with <see cref="ZernikeOrdering.Standard"/>, φ counter-clockwise from +x), and with the
/// extrapolate flag 0 "the Zernike terms are ignored outside of the normalization radius", with 1
/// "considered no matter where the ray lands". Immutable and safe to share between threads.
/// </summary>
internal sealed class ZernikeSagTerms
{
    private readonly double[] _a;
    private readonly ZernikeBasis? _basis;

    /// <param name="form">The surface type, for messages.</param>
    /// <param name="coefficients">A1 … AN, meters; N at most <paramref name="maximum"/>.</param>
    /// <param name="normalizationRadius">Meters; must be positive when N &gt; 0.</param>
    /// <param name="extrapolate">OpticStudio's extrapolate flag (parameter 0): true for 1.</param>
    /// <param name="maximum">The most terms the form holds.</param>
    public ZernikeSagTerms(string form, IReadOnlyList<double> coefficients, double normalizationRadius, bool extrapolate, int maximum)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        if (coefficients.Count > maximum)
            throw new ArgumentException($"{form}: {coefficients.Count} Zernike terms given; the surface holds at most {maximum}.");
        if (!double.IsFinite(normalizationRadius) || normalizationRadius < 0)
            throw new ArgumentException($"{form}: the normalization radius is {normalizationRadius}; it must be a length of zero or more.");
        _a = new double[coefficients.Count];
        for (int i = 0; i < _a.Length; i++)
        {
            if (!double.IsFinite(coefficients[i]))
                throw new ArgumentException($"{form}: Zernike coefficient {i + 1} is {coefficients[i]}; every coefficient must be finite.");
            _a[i] = coefficients[i];
        }
        if (_a.Length > 0 && !(normalizationRadius > 0))
            throw new ArgumentException($"{form}: {_a.Length} Zernike terms are used but the normalization radius is {normalizationRadius}; it must be a positive length (the coordinates are divided by it).");
        NormalizationRadius = normalizationRadius;
        Extrapolate = extrapolate;
        if (_a.Length > 0) _basis = new ZernikeBasis(ZernikeOrdering.Standard, _a.Length);
    }

    /// <summary>A1 … AN, meters.</summary>
    public IReadOnlyList<double> Coefficients => _a;

    /// <summary>Meters.</summary>
    public double NormalizationRadius { get; }

    public bool Extrapolate { get; }

    /// <summary>Adds the terms and their x and y slopes at (x, y), meters; false when a value is
    /// not finite.</summary>
    public bool Add(double x, double y, ref double z, ref double dzdx, ref double dzdy)
    {
        if (_basis is null) return true;
        double r = NormalizationRadius, u = x / r, v = y / r;
        if (!Extrapolate && u * u + v * v > 1) return true;
        int n = _a.Length;
        Span<double> values = n <= 256 ? stackalloc double[n] : new double[n];
        Span<double> dx = n <= 256 ? stackalloc double[n] : new double[n];
        Span<double> dy = n <= 256 ? stackalloc double[n] : new double[n];
        _basis.EvaluateAll(u, v, values, dx, dy);
        double s = 0, sx = 0, sy = 0;
        for (int j = 0; j < n; j++)
        {
            s += _a[j] * values[j];
            sx += _a[j] * dx[j];
            sy += _a[j] * dy[j];
        }
        z += s;
        dzdx += sx / r;
        dzdy += sy / r;
        return double.IsFinite(z) && double.IsFinite(dzdx) && double.IsFinite(dzdy);
    }

    /// <summary>
    /// The terms' value, slopes and second derivatives at the vertex (x = y = 0), meters and
    /// inverse meters, for the paraxial checks (<see cref="AnamorphicParaxial"/>).
    /// <para>
    /// Derived: a term is F·p_k(ρ²)·Re or Im (x + iy)^m with F the Noll normalisation times the
    /// recurrence's scale (<see cref="ZernikeBasis"/>), so near the origin only m ≤ 2 contribute
    /// to second order. m = 0: F·p(0) + F·p′(0)·(x² + y²) → value F·p(0), ∂²/∂x² = ∂²/∂y² = 2F·p′(0).
    /// m = 1: F·p(0)·x (cos) or F·p(0)·y (sin) → that slope. m = 2: F·p(0)·(x² − y²) (cos) →
    /// ∂²/∂x² = 2F·p(0), ∂²/∂y² = −2F·p(0); F·p(0)·2xy (sin) → ∂²/∂x∂y = 2F·p(0). p(0) and
    /// p′(0) come from the radial recurrence at u = 0, the same polynomials the trace evaluates.
    /// Coordinates are x/R_n, so slopes scale by 1/R_n and second derivatives by 1/R_n². The
    /// vertex is always inside the normalization radius, so the extrapolate flag does not enter.
    /// </para>
    /// </summary>
    public VertexTaylor AtVertex()
    {
        var t = new VertexTaylor();
        if (_basis is null) return t;
        double r = NormalizationRadius;
        // k = (n − m)/2 ≤ n/2, and term 231 has n = 20, so 32 entries are plenty.
        Span<double> p = stackalloc double[32];
        Span<double> dp = stackalloc double[32];
        for (int j = 1; j <= _a.Length; j++)
        {
            double a = _a[j - 1];
            if (a == 0) continue;
            var term = ZernikeIndex.FromNoll(j);
            int m = term.AbsM, k = (term.N - m) / 2;
            if (m > 2) continue;
            var radial = ZernikeRadialRecurrence.Circle(m, k);
            radial.Evaluate(0, k + 1, p, dp);
            double f = a * ZernikeIndex.Normalization(ZernikeOrdering.Standard, term) * radial.Scale(k);
            switch (m)
            {
                case 0:
                    t = t with { Value = t.Value + f * p[k], Hxx = t.Hxx + 2 * f * dp[k] / (r * r), Hyy = t.Hyy + 2 * f * dp[k] / (r * r) };
                    break;
                case 1 when term.IsSine:
                    t = t with { Gy = t.Gy + f * p[k] / r };
                    break;
                case 1:
                    t = t with { Gx = t.Gx + f * p[k] / r };
                    break;
                case 2 when term.IsSine:
                    t = t with { Hxy = t.Hxy + 2 * f * p[k] / (r * r) };
                    break;
                default:
                    t = t with { Hxx = t.Hxx + 2 * f * p[k] / (r * r), Hyy = t.Hyy - 2 * f * p[k] / (r * r) };
                    break;
            }
        }
        return t;
    }
}

/// <summary>A sag's second-order expansion at the vertex: z(0, 0), the slopes and the second
/// derivatives (meters, dimensionless, inverse meters).</summary>
internal readonly record struct VertexTaylor
{
    public double Value { get; init; }
    public double Gx { get; init; }
    public double Gy { get; init; }
    public double Hxx { get; init; }
    public double Hyy { get; init; }
    public double Hxy { get; init; }

    public static VertexTaylor operator +(VertexTaylor a, VertexTaylor b) => new()
    {
        Value = a.Value + b.Value, Gx = a.Gx + b.Gx, Gy = a.Gy + b.Gy,
        Hxx = a.Hxx + b.Hxx, Hyy = a.Hyy + b.Hyy, Hxy = a.Hxy + b.Hxy,
    };
}

/// <summary>
/// The first-order model of the anamorphic surfaces of OP-24 (docs/Optics-Conventions.md §23).
/// The paraxial engine is rotationally symmetric and traces the y–z meridian
/// (<see cref="SurfaceShape.ParaxialEquivalent"/>), as the Paraxial XY, ABCD and Periodic
/// surfaces already report: the surface is given its y–z vertex curvature ∂²z/∂y² at the
/// vertex, and its x–z curvature is not passed on. A surface whose vertex is not a stationary
/// point on the axis, or whose second-order part is twisted, has no such model: a vertex off
/// z = 0 (a Zernike piston or defocus term) shifts the surface along the axis, a slope at the
/// vertex (a tilt term, a linear x or y term, a tilted or decentered Irregular surface) tilts it,
/// and ∂²z/∂x∂y ≠ 0 turns the astigmatism out of the x and y axes so a y-meridian ray leaves its
/// plane. Those are refused with an <see cref="ArgumentException"/> naming the cause, as the
/// paraxial engine refuses a tilted or decentered lens (§15), rather than given a curvature
/// that would be wrong.
/// </summary>
internal static class AnamorphicParaxial
{
    /// <summary>The y–z curvature <paramref name="baseCurvature"/> + ∂²z/∂y² of
    /// <paramref name="t"/>, the expansion of everything the surface adds to its base curvature
    /// (base curvature itself when that adds nothing, so −0 stays −0); throws as above.</summary>
    public static ParaxialShapeEquivalent YCurvature(string form, double baseCurvature, VertexTaylor t, string cause)
    {
        if (t.Value != 0)
            throw new ArgumentException($"{form}: the sag at the vertex is {t.Value:R} m, not zero ({cause}), so the surface is shifted along the axis and the rotationally symmetric paraxial engine has no model of it. Paraxial data, ray aiming and solves are not available for this lens.");
        if (t.Gx != 0 || t.Gy != 0)
            throw new ArgumentException($"{form}: the surface has the slope ({t.Gx:R}, {t.Gy:R}) at the vertex ({cause}), so it is tilted there and the rotationally symmetric paraxial engine has no model of it. Paraxial data, ray aiming and solves are not available for this lens.");
        if (t.Hxy != 0)
            throw new ArgumentException($"{form}: ∂²z/∂x∂y at the vertex is {t.Hxy:R} 1/m ({cause}), so the surface's astigmatism is not along x and y and a y-meridian paraxial ray leaves its plane. Paraxial data, ray aiming and solves are not available for this lens.");
        return new ParaxialShapeEquivalent { Curvature = Aspheres.ParaxialCurvature.Add(baseCurvature, t.Hyy) };
    }
}
