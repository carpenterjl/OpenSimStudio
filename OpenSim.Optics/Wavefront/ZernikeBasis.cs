namespace OpenSim.Optics.Wavefront;

/// <summary>
/// The first <see cref="TermCount"/> Zernike terms of one ordering, ready to evaluate at points
/// of the normalised pupil (x, y) = (ρ cos θ, ρ sin θ), θ counter-clockwise from +x (OpticStudio's
/// convention in all three of its Zernike tables). Term j is index j − 1 in every span.
/// <para>
/// A term is Z = N · R_n^|m|(ρ) · cos mθ (m &gt; 0), sin |m|θ (m &lt; 0) or 1 (m = 0), with N
/// from <see cref="ZernikeIndex.Normalization"/>. It is evaluated as N·Scale·p_k(ρ²)·Re or Im of
/// (x + iy)^|m|: ρ^|m| cos |m|θ = Re (x + iy)^|m|. That form has no division by ρ, so the value
/// and the gradient are exact at the pupil centre, and the x and y derivatives come from
/// d(x + iy)^m/dx = m(x + iy)^(m−1) and d/dy = i·m(x + iy)^(m−1).
/// </para>
/// <para>
/// The polynomials are defined everywhere; they are orthogonal only on the unit disk (or the
/// annulus ε ≤ ρ ≤ 1), and only in the continuous sense — never on a set of samples, which is
/// why <see cref="ZernikeFit"/> solves least squares instead of projecting. Immutable and safe to
/// share between threads.
/// </para>
/// </summary>
public sealed class ZernikeBasis
{
    private readonly ZernikeTerm[] _terms;
    private readonly double[] _factor;               // N · Scale(k) per term
    private readonly ZernikeRadialRecurrence?[] _radial; // by |m|
    private readonly int[] _count;                   // how many p_k each |m| needs
    private readonly int[] _offset;                  // where |m|'s p_k start in the scratch
    private readonly int _scratchLength;

    /// <param name="ordering">The term order and normalisation.</param>
    /// <param name="termCount">How many terms, from term 1. Fringe has at most 37.</param>
    /// <param name="obscuration">The annular obscuration ratio ε = R_inner/R_outer, 0 ≤ ε &lt; 1;
    /// only for <see cref="ZernikeOrdering.Annular"/>. Fringe and Standard are circular.</param>
    public ZernikeBasis(ZernikeOrdering ordering, int termCount, double obscuration = 0)
    {
        if (termCount < 1) throw new ArgumentOutOfRangeException(nameof(termCount), termCount, "At least one term.");
        if (ordering == ZernikeOrdering.Fringe && termCount > ZernikeIndex.FringeTermCount)
            throw new ArgumentOutOfRangeException(nameof(termCount), termCount, $"The Fringe set has {ZernikeIndex.FringeTermCount} terms.");
        if (!(obscuration >= 0 && obscuration < 1))
            throw new ArgumentOutOfRangeException(nameof(obscuration), obscuration, "The obscuration ratio must be in [0, 1).");
        if (obscuration != 0 && ordering != ZernikeOrdering.Annular)
            throw new ArgumentException($"{ordering} Zernike terms are for a circular pupil; use the Annular ordering for an obscuration.", nameof(obscuration));

        Ordering = ordering;
        TermCount = termCount;
        Obscuration = obscuration;
        _terms = new ZernikeTerm[termCount];
        int maxM = 0;
        for (int j = 0; j < termCount; j++)
        {
            _terms[j] = ZernikeIndex.FromIndex(ordering, j + 1);
            maxM = Math.Max(maxM, _terms[j].AbsM);
        }
        MaxAzimuthalOrder = maxM;

        _count = new int[maxM + 1];
        foreach (var term in _terms)
        {
            int k = (term.N - term.AbsM) / 2;
            _count[term.AbsM] = Math.Max(_count[term.AbsM], k + 1);
        }
        _radial = new ZernikeRadialRecurrence?[maxM + 1];
        _offset = new int[maxM + 1];
        int offset = 0;
        for (int m = 0; m <= maxM; m++)
        {
            _offset[m] = offset;
            if (_count[m] == 0) continue;
            _radial[m] = CreateRadial(m, _count[m] - 1);
            offset += _count[m];
        }
        _scratchLength = offset;

        _factor = new double[termCount];
        for (int j = 0; j < termCount; j++)
        {
            var term = _terms[j];
            int k = (term.N - term.AbsM) / 2;
            _factor[j] = ZernikeIndex.Normalization(ordering, term) * _radial[term.AbsM]!.Scale(k);
        }
    }

    public ZernikeOrdering Ordering { get; }

    public int TermCount { get; }

    /// <summary>ε, the inner radius over the outer; 0 for a circular pupil.</summary>
    public double Obscuration { get; }

    /// <summary>The largest |m| among the terms.</summary>
    public int MaxAzimuthalOrder { get; }

    /// <summary>Term <paramref name="j"/> (1-based) as (n, m).</summary>
    public ZernikeTerm Term(int j)
    {
        CheckTerm(j);
        return _terms[j - 1];
    }

    /// <summary>The factor N in front of R_n^m for term <paramref name="j"/> (1-based).</summary>
    public double Normalization(int j) => ZernikeIndex.Normalization(Ordering, Term(j));

    /// <summary>True when (x, y) lies on the pupil the terms are orthogonal on, ε ≤ ρ ≤ 1, to a
    /// relative 1e-12 at either edge.</summary>
    public bool Contains(double x, double y)
    {
        double rho = Math.Sqrt(x * x + y * y);
        const double slack = 1e-12;
        return rho <= 1 + slack && rho >= Obscuration * (1 - slack);
    }

    /// <summary>
    /// The radial polynomial R_n^m(ρ) of this basis's pupil: the circle polynomial (R(1) = 1)
    /// when ε = 0, Mahajan's annular polynomial otherwise. Any valid (n, m), not only the
    /// basis's own terms.
    /// </summary>
    public double RadialPolynomial(int n, int m, double rho)
    {
        var term = new ZernikeTerm(n, m);
        int k = (n - term.AbsM) / 2;
        var radial = term.AbsM < _radial.Length && _radial[term.AbsM] is { } own && own.MaxK >= k
            ? own
            : CreateRadial(term.AbsM, k);
        Span<double> p = k < 128 ? stackalloc double[k + 1] : new double[k + 1];
        Span<double> dp = k < 128 ? stackalloc double[k + 1] : new double[k + 1];
        radial.Evaluate(rho * rho, k + 1, p, dp);
        return Math.Pow(rho, term.AbsM) * radial.Scale(k) * p[k];
    }

    /// <summary>Term <paramref name="j"/> (1-based) at (x, y).</summary>
    public double Evaluate(int j, double x, double y) => EvaluateWithGradient(j, x, y).Value;

    /// <summary>Term <paramref name="j"/> (1-based) in polar pupil coordinates.</summary>
    public double EvaluatePolar(int j, double rho, double theta) =>
        Evaluate(j, rho * Math.Cos(theta), rho * Math.Sin(theta));

    /// <summary>Term <paramref name="j"/> (1-based) at (x, y), with ∂Z/∂x and ∂Z/∂y.</summary>
    public (double Value, double DX, double DY) EvaluateWithGradient(int j, double x, double y)
    {
        CheckTerm(j);
        var term = _terms[j - 1];
        int m = term.AbsM, k = (term.N - m) / 2;
        Span<double> p = k < 128 ? stackalloc double[k + 1] : new double[k + 1];
        Span<double> dp = k < 128 ? stackalloc double[k + 1] : new double[k + 1];
        _radial[m]!.Evaluate(x * x + y * y, k + 1, p, dp);
        var (re, im) = Power(x, y, m);
        var (reLower, imLower) = m > 0 ? Power(x, y, m - 1) : (0.0, 0.0);
        return Combine(term, _factor[j - 1], p[k], dp[k], x, y, re, im, reLower, imLower);
    }

    /// <summary>Every term at (x, y): <paramref name="values"/>[j − 1] is term j.</summary>
    public void EvaluateAll(double x, double y, Span<double> values) =>
        EvaluateAllCore(x, y, values, Span<double>.Empty, Span<double>.Empty, false);

    /// <summary>Every term at (x, y) with its x and y derivatives.</summary>
    public void EvaluateAll(double x, double y, Span<double> values, Span<double> dX, Span<double> dY)
    {
        if (dX.Length < TermCount || dY.Length < TermCount)
            throw new ArgumentException($"The derivative spans need {TermCount} elements.");
        EvaluateAllCore(x, y, values, dX, dY, true);
    }

    /// <summary>Σ c_j Z_j(x, y): the wavefront a set of coefficients describes. Element 0 of
    /// <paramref name="coefficients"/> is term 1; it may be shorter than the basis.</summary>
    public double Sum(ReadOnlySpan<double> coefficients, double x, double y)
    {
        if (coefficients.Length > TermCount)
            throw new ArgumentException($"At most {TermCount} coefficients.", nameof(coefficients));
        Span<double> values = TermCount <= 512 ? stackalloc double[TermCount] : new double[TermCount];
        EvaluateAll(x, y, values);
        double sum = 0;
        for (int j = 0; j < coefficients.Length; j++) sum += coefficients[j] * values[j];
        return sum;
    }

    private void EvaluateAllCore(double x, double y, Span<double> values, Span<double> dX, Span<double> dY, bool gradient)
    {
        if (values.Length < TermCount) throw new ArgumentException($"The value span needs {TermCount} elements.", nameof(values));
        double u = x * x + y * y;
        var p = new double[_scratchLength];
        var dp = new double[_scratchLength];
        for (int m = 0; m <= MaxAzimuthalOrder; m++)
        {
            if (_count[m] == 0) continue;
            _radial[m]!.Evaluate(u, _count[m], p.AsSpan(_offset[m], _count[m]), dp.AsSpan(_offset[m], _count[m]));
        }
        // (x + iy)^m for m = 0 … MaxAzimuthalOrder by repeated multiplication.
        var re = new double[MaxAzimuthalOrder + 1];
        var im = new double[MaxAzimuthalOrder + 1];
        re[0] = 1;
        for (int m = 1; m <= MaxAzimuthalOrder; m++)
        {
            re[m] = re[m - 1] * x - im[m - 1] * y;
            im[m] = re[m - 1] * y + im[m - 1] * x;
        }
        for (int j = 0; j < TermCount; j++)
        {
            var term = _terms[j];
            int m = term.AbsM, index = _offset[m] + (term.N - m) / 2;
            var result = Combine(term, _factor[j], p[index], dp[index], x, y, re[m], im[m],
                m > 0 ? re[m - 1] : 0, m > 0 ? im[m - 1] : 0);
            values[j] = result.Value;
            if (gradient)
            {
                dX[j] = result.DX;
                dY[j] = result.DY;
            }
        }
    }

    private static (double Value, double DX, double DY) Combine(
        ZernikeTerm term, double factor, double p, double dpdu, double x, double y,
        double re, double im, double reLower, double imLower)
    {
        int m = term.AbsM;
        // The angular part T = Re or Im of z^m (1 for m = 0) and its derivatives.
        double t, tx, ty;
        if (term.M >= 0)
        {
            t = re;
            tx = m * reLower;
            ty = -m * imLower;
        }
        else
        {
            t = im;
            tx = m * imLower;
            ty = m * reLower;
        }
        double value = factor * p * t;
        double dx = factor * (2 * x * dpdu * t + p * tx);
        double dy = factor * (2 * y * dpdu * t + p * ty);
        return (value, dx, dy);
    }

    private static (double Re, double Im) Power(double x, double y, int m)
    {
        double re = 1, im = 0;
        for (int i = 0; i < m; i++) (re, im) = (re * x - im * y, re * y + im * x);
        return (re, im);
    }

    private ZernikeRadialRecurrence CreateRadial(int m, int maxK) =>
        Obscuration == 0
            ? ZernikeRadialRecurrence.Circle(m, maxK)
            : ZernikeRadialRecurrence.Annulus(m, maxK, Obscuration);

    private void CheckTerm(int j)
    {
        if (j < 1 || j > TermCount)
            throw new ArgumentOutOfRangeException(nameof(j), j, $"Terms run from 1 to {TermCount}.");
    }
}
