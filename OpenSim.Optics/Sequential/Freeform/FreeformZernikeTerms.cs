using OpenSim.Optics.Sequential.Anamorphic;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Sequential.Freeform;

/// <summary>
/// The Zernike sag terms Σ Aᵢ·Zᵢ(ρ, φ) of OpticStudio's Zernike Fringe Sag, Zernike Standard Sag,
/// Zernike Annular Standard Sag and Grid Sag surfaces (OP-25, docs/Optics-Conventions.md §55).
/// <para>
/// From the help pages of those surfaces (OpticStudio 2025 R2, saved under
/// plans/sources/optics/op-25-help/): the coefficients Aᵢ are lengths (meters here), "r is the
/// radial ray coordinate in lens units, ρ is the normalized radial ray coordinate, and φ is the
/// angular ray coordinate"; the polynomials are those of the "Zernike Fringe Coefficients",
/// "Zernike Standard Coefficients" and "Zernike Annular Coefficients" tables (<see cref="ZernikeBasis"/>
/// with the matching <see cref="ZernikeOrdering"/>, φ counter-clockwise from +x); "The Zernike
/// terms may be decentered from the conic and aspherical terms using parameters 9 and 10"
/// (Fringe, Standard, Grid Sag), read as ρ = |(x − x_d, y − y_d)|/R_n. With the extrapolate flag 0
/// "the Zernike terms are ignored outside of the normalization radius"; for the annular form
/// also "Within the obscuration radius, the Zernike terms are always ignored, no matter how the
/// 'Extrapolate' flag is set".
/// </para>
/// Immutable and safe to share between threads (<see cref="ZernikeBasis"/> is).
/// </summary>
internal sealed class FreeformZernikeTerms
{
    private readonly double[] _a;
    private readonly ZernikeBasis? _basis;

    /// <param name="form">The surface type, for messages.</param>
    /// <param name="ordering">Fringe, Standard or Annular.</param>
    /// <param name="coefficients">A1 … AN, meters.</param>
    /// <param name="normalizationRadius">Meters; positive when N &gt; 0.</param>
    /// <param name="extrapolate">The extrapolate flag (true for 1). The Grid Sag has no flag
    /// (its parameter 0 is the interpolation method); its terms are passed with true, as its sag
    /// equation sums them without a bound (the reading in §55).</param>
    /// <param name="maximum">The most terms the form holds.</param>
    /// <param name="decenterX">The Zernike decenter in x, meters (parameter 9).</param>
    /// <param name="decenterY">The Zernike decenter in y, meters (parameter 10).</param>
    /// <param name="obscuration">ε, annular only, 0 ≤ ε &lt; 1.</param>
    public FreeformZernikeTerms(string form, ZernikeOrdering ordering, IReadOnlyList<double> coefficients,
        double normalizationRadius, bool extrapolate, int maximum, double decenterX = 0, double decenterY = 0,
        double obscuration = 0)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        if (coefficients.Count > maximum)
            throw new ArgumentException($"{form}: {coefficients.Count} Zernike terms given; the surface holds at most {maximum}.");
        if (!double.IsFinite(normalizationRadius) || normalizationRadius < 0)
            throw new ArgumentException($"{form}: the normalization radius is {normalizationRadius}; it must be a length of zero or more.");
        if (!double.IsFinite(decenterX) || !double.IsFinite(decenterY))
            throw new ArgumentException($"{form}: the Zernike decenter ({decenterX}, {decenterY}) must be finite.");
        if (!(obscuration >= 0 && obscuration < 1))
            throw new ArgumentException($"{form}: the obscuration is {obscuration}; it must be at least 0 and less than 1.");
        _a = new double[coefficients.Count];
        for (int i = 0; i < _a.Length; i++)
        {
            if (!double.IsFinite(coefficients[i]))
                throw new ArgumentException($"{form}: Zernike coefficient {i + 1} is {coefficients[i]}; every coefficient must be finite.");
            _a[i] = coefficients[i];
        }
        if (_a.Length > 0 && !(normalizationRadius > 0))
            throw new ArgumentException($"{form}: {_a.Length} Zernike terms are used but the normalization radius is {normalizationRadius}; it must be a positive length (the coordinates are divided by it).");
        Form = form;
        Ordering = ordering;
        NormalizationRadius = normalizationRadius;
        Extrapolate = extrapolate;
        DecenterX = decenterX;
        DecenterY = decenterY;
        Obscuration = obscuration;
        if (_a.Length > 0) _basis = new ZernikeBasis(ordering, _a.Length, ordering == ZernikeOrdering.Annular ? obscuration : 0);
    }

    public string Form { get; }
    public ZernikeOrdering Ordering { get; }

    /// <summary>A1 … AN, meters.</summary>
    public IReadOnlyList<double> Coefficients => _a;

    /// <summary>Meters.</summary>
    public double NormalizationRadius { get; }
    public bool Extrapolate { get; }

    /// <summary>Meters.</summary>
    public double DecenterX { get; }

    /// <summary>Meters.</summary>
    public double DecenterY { get; }

    /// <summary>ε (annular form only).</summary>
    public double Obscuration { get; }

    /// <summary>Adds the terms and their x and y slopes at (x, y), meters; false when a value is
    /// not finite.</summary>
    public bool Add(double x, double y, ref double z, ref double dzdx, ref double dzdy)
    {
        if (_basis is null) return true;
        double r = NormalizationRadius, u = (x - DecenterX) / r, v = (y - DecenterY) / r;
        double rho2 = u * u + v * v;
        if (!Extrapolate && rho2 > 1) return true;
        if (Obscuration > 0 && rho2 < Obscuration * Obscuration) return true;
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
    /// The terms' value, slopes and second derivatives at the vertex, for the paraxial model
    /// (<see cref="AnamorphicParaxial"/>). Derived as in <c>ZernikeSagTerms.AtVertex</c> (OP-24):
    /// near ρ = 0 only |m| ≤ 2 reach second order; the circle polynomials' p_k(0) and p_k′(0)
    /// come from the radial recurrence. Throws when the terms are decentered and any coefficient
    /// is non-zero (the vertex is then not ρ = 0 and this expansion does not apply; the
    /// rotationally symmetric paraxial engine has no model of a decentered term anyway). The
    /// annular form with ε &gt; 0 adds nothing at the vertex, which lies in its obscuration where
    /// the terms are ignored.
    /// </summary>
    public VertexTaylor AtVertex()
    {
        var t = new VertexTaylor();
        if (_basis is null || _a.All(a => a == 0)) return t;
        if (DecenterX != 0 || DecenterY != 0)
            throw new ArgumentException($"{Form}: the Zernike terms are decentered by ({DecenterX:R}, {DecenterY:R}) m, so their expansion at the vertex is not the centred one and the rotationally symmetric paraxial engine has no model of the surface. Paraxial data, ray aiming and solves are not available for this lens.");
        if (Ordering == ZernikeOrdering.Annular && Obscuration > 0) return t;
        var ordering = Ordering == ZernikeOrdering.Annular ? ZernikeOrdering.Standard : Ordering; // ε = 0: equal to Standard
        double r = NormalizationRadius;
        Span<double> p = stackalloc double[32];
        Span<double> dp = stackalloc double[32];
        for (int j = 1; j <= _a.Length; j++)
        {
            double a = _a[j - 1];
            if (a == 0) continue;
            var term = ZernikeIndex.FromIndex(ordering, j);
            int m = term.AbsM, k = (term.N - m) / 2;
            if (m > 2) continue;
            var radial = ZernikeRadialRecurrence.Circle(m, k);
            radial.Evaluate(0, k + 1, p, dp);
            double f = a * ZernikeIndex.Normalization(ordering, term) * radial.Scale(k);
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
