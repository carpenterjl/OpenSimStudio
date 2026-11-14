namespace OpenSim.Optics.Sequential.Aspheres;

/// <summary>
/// Forbes's Q polynomials, evaluated only by recurrence (never expanded into monomials).
/// Sources (transcribed in plans/sources/optics/forbes/Forbes-equations-read-2026-11-14.txt):
/// <list type="bullet">
/// <item>[F2007] G. W. Forbes, "Shape specification for axially symmetric optical surfaces",
/// Opt. Express 15, 5218–5226 (2007): Q^con_m(x) = P_m^(0,4)(2x − 1), Eq. (8).</item>
/// <item>[F2010a] G. W. Forbes, "Robust and fast computation for the polynomials of optics",
/// Opt. Express 18, 13851–13862 (2010): the three-term recurrence Eq. (1.6), Clenshaw's sum
/// Eq. (2.2), its derivatives Eq. (2.3), and the Q^con constants Eq. (5.2).</item>
/// <item>[F2010b] G. W. Forbes, "Robust, efficient computational methods for axially symmetric
/// optical aspheres", Opt. Express 18, 19700–19712 (2010): Q^bfs through the auxiliary
/// polynomials P_m, Eqs. (2.5)–(2.7), (3.2)–(3.12), (4.4)–(4.6) and (A.14)–(A.16).</item>
/// </list>
/// </summary>
public static class QPolynomials
{
    // ---- Q^con: P_{n+1} = (a_n + b_n x) P_n − c_n P_{n−1}, P_0 = 1 ([F2010a] Eq. 1.6, 5.2) ----

    /// <summary>[F2010a] Eq. (5.2a): a_n = −(2n + 5)(n² + 5n + 10)/((n + 1)(n + 2)(n + 5)).</summary>
    public static double ConA(int n) => -(2.0 * n + 5) * (n * (double)n + 5.0 * n + 10) / ((n + 1.0) * (n + 2.0) * (n + 5.0));

    /// <summary>[F2010a] Eq. (5.2b): b_n = 2(n + 3)(2n + 5)/((n + 1)(n + 5)).</summary>
    public static double ConB(int n) => 2.0 * (n + 3) * (2.0 * n + 5) / ((n + 1.0) * (n + 5.0));

    /// <summary>[F2010a] Eq. (5.2c): c_n = (n + 3)(n + 4)n/((n + 1)(n + 2)(n + 5)).</summary>
    public static double ConC(int n) => (n + 3.0) * (n + 4.0) * n / ((n + 1.0) * (n + 2.0) * (n + 5.0));

    /// <summary>Q^con_0(x) … Q^con_M(x) by the recurrence.</summary>
    public static double[] ConBasis(int maxOrder, double x)
    {
        var q = new double[maxOrder + 1];
        q[0] = 1;
        if (maxOrder >= 1) q[1] = ConA(0) + ConB(0) * x;
        for (int n = 1; n < maxOrder; n++)
            q[n + 1] = (ConA(n) + ConB(n) * x) * q[n] - ConC(n) * q[n - 1];
        return q;
    }

    /// <summary>
    /// S(x) = Σ s_m Q^con_m(x) and S′(x) by Clenshaw's recurrence and its derivative
    /// ([F2010a] Eqs. 2.2 and 2.3 with j = 1). An empty list gives 0.
    /// </summary>
    public static void ConSum(IReadOnlyList<double> s, double x, out double value, out double derivative)
    {
        int m = s.Count - 1;
        value = derivative = 0;
        if (m < 0) return;
        if (m == 0)
        {
            value = s[0];
            return;
        }
        // α_{n} and α′_{n} for n + 1 and n + 2 as the sweep runs down.
        double a1 = s[m];                                           // α_M
        double a0 = s[m - 1] + (ConA(m - 1) + ConB(m - 1) * x) * a1;  // α_{M−1}
        double d1 = 0;                                              // α′_M
        double d0 = ConB(m - 1) * a1;                               // α′_{M−1} = 1·b_{M−1}·α_M
        for (int n = m - 2; n >= 0; n--)
        {
            double t = ConA(n) + ConB(n) * x;
            double an = s[n] + t * a0 - ConC(n + 1) * a1;
            double dn = ConB(n) * a0 + t * d0 - ConC(n + 1) * d1;
            a1 = a0; a0 = an;
            d1 = d0; d0 = dn;
        }
        value = a0;
        derivative = d0;
    }

    // ---- Q^bfs through the auxiliary P_m: P_{m+1} = (2 − 4x)P_m − P_{m−1}, P0 = 2, P1 = 6 − 8x ----

    /// <summary>
    /// The band of the change-of-basis matrix P_m = f_m Q_m + g_{m−1} Q_{m−1} + h_{m−2} Q_{m−2}
    /// ([F2010b] Eq. 2.5), by [F2010b] Eqs. (A.14)–(A.16) from f0 = 2, f1 = √19/2, g0 = −1/2.
    /// Entries f[0…M], g[0…M−1] (g[M] = 0), h[0…M−2] (h[M−1] = h[M] = 0).
    /// </summary>
    public static (double[] F, double[] G, double[] H) BfsBand(int maxOrder)
    {
        int size = Math.Max(maxOrder, 1) + 1;
        var f = new double[size];
        var g = new double[size];
        var h = new double[size];
        f[0] = 2;
        f[1] = Math.Sqrt(19) / 2;
        g[0] = -0.5;
        for (int m = 2; m <= maxOrder; m++)
        {
            h[m - 2] = -m * (m - 1.0) / (2 * f[m - 2]);
            g[m - 1] = -(1 + g[m - 2] * h[m - 2]) / f[m - 1];
            f[m] = Math.Sqrt(m * (m + 1.0) + 3 - g[m - 1] * g[m - 1] - h[m - 2] * h[m - 2]);
        }
        if (maxOrder == 0) { g[0] = 0; }
        else
        {
            g[maxOrder] = 0;
            h[maxOrder] = 0;
            h[maxOrder - 1] = 0;
        }
        return (f, g, h);
    }

    /// <summary>Q^bfs_0(x) … Q^bfs_M(x) by [F2010b] Eq. (2.7) with the P_m of Eq. (2.6).</summary>
    public static double[] BfsBasis(int maxOrder, double x)
    {
        var (f, g, h) = BfsBand(maxOrder + 1);
        var q = new double[maxOrder + 1];
        double pPrev = 2, p = 6 - 8 * x;      // P0, P1
        q[0] = 1;
        if (maxOrder >= 1) q[1] = (13 - 16 * x) / Math.Sqrt(19);
        for (int m = 1; m < maxOrder; m++)
        {
            double pNext = (2 - 4 * x) * p - pPrev;   // P_{m+1}
            pPrev = p;
            p = pNext;
            q[m + 1] = (p - g[m] * q[m] - h[m - 1] * q[m - 1]) / f[m + 1];
        }
        return q;
    }

    /// <summary>The auxiliary coefficients b from the Q^bfs coefficients a ([F2010b] Eqs. 3.4–3.5).</summary>
    public static double[] BfsToAuxiliary(IReadOnlyList<double> a)
    {
        int m = a.Count - 1;
        var b = new double[a.Count];
        if (m < 0) return b;
        var (f, g, h) = BfsBand(m);
        b[m] = a[m] / f[m];
        if (m >= 1) b[m - 1] = (a[m - 1] - g[m - 1] * b[m]) / f[m - 1];
        for (int n = m - 2; n >= 0; n--)
            b[n] = (a[n] - g[n] * b[n + 1] - h[n] * b[n + 2]) / f[n];
        return b;
    }

    /// <summary>The Q^bfs coefficients a from the auxiliary b ([F2010b] Eqs. 3.2–3.3).</summary>
    public static double[] BfsFromAuxiliary(IReadOnlyList<double> b)
    {
        int m = b.Count - 1;
        var a = new double[b.Count];
        if (m < 0) return a;
        var (f, g, h) = BfsBand(m);
        for (int n = 0; n <= m; n++)
        {
            double v = f[n] * b[n];
            if (n + 1 <= m) v += g[n] * b[n + 1];
            if (n + 2 <= m) v += h[n] * b[n + 2];
            a[n] = v;
        }
        return a;
    }

    /// <summary>
    /// S(x) = Σ b_m P_m(x) and S′(x) from the auxiliary coefficients b by Clenshaw
    /// ([F2010b] Eqs. 3.7–3.9: S = 2(α0 + α1)) and its derivative (Eqs. 3.10–3.12 with j = 1:
    /// S′ = 2(α′0 + α′1)).
    /// </summary>
    public static void BfsSumAuxiliary(IReadOnlyList<double> b, double x, out double value, out double derivative)
    {
        int m = b.Count - 1;
        value = derivative = 0;
        if (m < 0) return;
        double t = 2 - 4 * x;
        // Arrays of length M + 3 keep the index arithmetic of the paper (α_{M+1} = α_{M+2} = 0).
        var alpha = new double[m + 3];
        var dAlpha = new double[m + 3];
        for (int n = m; n >= 0; n--)
            alpha[n] = b[n] + t * alpha[n + 1] - alpha[n + 2];
        // j = 1: α′_M = 0, α′_{M−1} = −4·α_M, α′_n = t·α′_{n+1} − α′_{n+2} − 4·α_{n+1}.
        for (int n = m - 1; n >= 0; n--)
            dAlpha[n] = t * dAlpha[n + 1] - dAlpha[n + 2] - 4 * alpha[n + 1];
        value = 2 * (alpha[0] + alpha[1]);
        derivative = 2 * (dAlpha[0] + dAlpha[1]);
    }

    /// <summary>
    /// Forbes's fit to the auxiliary basis by a discrete cosine transform ([F2010b] Eqs. 4.4–4.6):
    /// the best-fit sphere through the vertex and the edge, c = 2f(ρmax)/(ρmax² + f(ρmax)²)
    /// (Eq. 4.2), then b_m = (−1)^m/N · Σ_j F_j cos[π/N (m + ½)(j + ½)], m = 0…N − 1, with
    /// F_j = cos θ_j·F(cos θ_j), θ_j = π(j + ½)/(2N) and F(u) of Eq. (4.4).
    /// <para>
    /// The paper prints the factor (−1)^m/(2N) in Eqs. (4.5) and (B.7). The midpoint rule on
    /// its Eq. (B.4) — N nodes of spacing π/(2N) on [0, π/2], doubled for the even integrand on
    /// [−π/2, π/2] — gives (−1)^m/N, and only that factor reproduces its worked example,
    /// Eq. (4.7), to the printed digits (checked 2026-11-14); this code uses 1/N.
    /// </para>
    /// </summary>
    /// <param name="sag">The surface's sag f(ρ), meters, with f(0) = 0 and f′(0) = 0.</param>
    /// <param name="rhoMax">Half the clear aperture, meters.</param>
    /// <param name="nodes">N, the number of nodes and of coefficients returned.</param>
    public static double[] BfsFitByCosineTransform(Func<double, double> sag, double rhoMax, int nodes,
        out double bestFitCurvature)
    {
        ArgumentNullException.ThrowIfNull(sag);
        if (!(rhoMax > 0)) throw new ArgumentException($"Q-type fit: ρmax is {rhoMax}; it must be positive.");
        if (nodes < 1) throw new ArgumentException($"Q-type fit: {nodes} nodes; at least one is needed.");
        double fEdge = sag(rhoMax);
        double c = 2 * fEdge / (rhoMax * rhoMax + fEdge * fEdge);
        bestFitCurvature = c;
        var fj = new double[nodes];
        for (int j = 0; j < nodes; j++)
        {
            double u = Math.Cos(Math.PI / (2.0 * nodes) * (j + 0.5));
            double rho = u * rhoMax;
            double arg = 1 - (c * rho) * (c * rho);
            if (!(arg > 0))
                throw new InvalidOperationException($"Q-type fit: the best-fit sphere (curvature {c} 1/m) does not reach ρ = {rho} m.");
            double phi = Math.Sqrt(arg);
            double f = sag(rho);
            if (!double.IsFinite(f))
                throw new InvalidOperationException($"Q-type fit: the sag at ρ = {rho} m is {f}.");
            fj[j] = u * phi / (u * u * (1 - u * u)) * (f - c * rho * rho / (1 + phi));
        }
        var b = new double[nodes];
        for (int m = 0; m < nodes; m++)
        {
            double sum = 0;
            for (int j = 0; j < nodes; j++)
                sum += fj[j] * Math.Cos(Math.PI / nodes * (m + 0.5) * (j + 0.5));
            b[m] = (m % 2 == 0 ? 1.0 : -1.0) / nodes * sum;
        }
        return b;
    }
}
