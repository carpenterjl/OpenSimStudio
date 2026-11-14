using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Wavefront;

/// <summary>
/// The radial polynomials of one azimuthal frequency m, R_n^m(ρ) for n = m, m + 2, …, by a
/// three-term recurrence in u = ρ². Every R_n^m is ρ^m times a polynomial of degree
/// k = (n − m)/2 in u, and those polynomials are orthogonal on the pupil with weight u^m:
/// ∫ R_n^m R_n'^m ρ dρ = ½ ∫ u^m Q_k Q_k' du. So
/// <code>
/// R_n^m(ρ) = ρ^m · Scale[k] · p_k(u),   p_{k+1} = (A_k u + B_k) p_k − C_k p_{k−1},   p_0 = P0.
/// </code>
/// The recurrence (and its derivative, from differentiating it) replaces the factorial sum,
/// whose alternating terms lose every digit by n ≈ 40.
/// <list type="bullet">
/// <item><b>Circle.</b> R_n^m(ρ) = ρ^m P_k^(0,m)(2u − 1), the Jacobi polynomial with α = 0,
/// β = m, so R_n^m(1) = 1. Coefficients from DLMF 18.9.1–18.9.2; p_1 is written out because the
/// k = 0 coefficients are 0/0 when m = 0.</item>
/// <item><b>Annulus</b> ε ≤ ρ ≤ 1. The polynomials orthonormal for u^m du on [ε², 1], their
/// recurrence found by the discretised Stieltjes procedure on a Gauss–Legendre rule that
/// integrates every product exactly (Gautschi, "Orthogonal Polynomials: Computation and
/// Approximation", 2004, §2.2.3). Scaled, as in Mahajan (JOSA 71, 75, 1981; Applied Optics 33,
/// 8125, 1994, Table 1), to ∫_ε^1 (R_n^m)² ρ dρ = (1 − ε²)/(2(n + 1)) with a positive leading
/// coefficient; at ε = 0 these are the circle polynomials.</item>
/// </list>
/// </summary>
internal sealed class ZernikeRadialRecurrence
{
    private readonly double[] _a, _b, _c, _scale;
    private readonly double _p0;

    private ZernikeRadialRecurrence(int m, double[] a, double[] b, double[] c, double[] scale, double p0)
    {
        M = m;
        _a = a;
        _b = b;
        _c = c;
        _scale = scale;
        _p0 = p0;
    }

    /// <summary>|m|.</summary>
    public int M { get; }

    /// <summary>The largest k = (n − m)/2 this recurrence reaches.</summary>
    public int MaxK => _scale.Length - 1;

    /// <summary>The factor between ρ^m·p_k(u) and R_{m+2k}^m.</summary>
    public double Scale(int k) => _scale[k];

    /// <summary>The circle polynomials (Fringe and Standard), R_n^m(1) = 1.</summary>
    public static ZernikeRadialRecurrence Circle(int m, int maxK)
    {
        Validate(m, maxK);
        var a = new double[maxK];
        var b = new double[maxK];
        var c = new double[maxK];
        var scale = new double[maxK + 1];
        Array.Fill(scale, 1.0);
        if (maxK > 0)
        {
            // P_1^(α,β)(x) = (α + 1) + (α + β + 2)(x − 1)/2 with α = 0, β = m, x = 2u − 1.
            a[0] = m + 2;
            b[0] = -(m + 1);
        }
        for (int k = 1; k < maxK; k++)
        {
            // DLMF 18.9.2 with α = 0, β = m; s = 2k + α + β is at least 2 here.
            double s = 2 * k + m;
            double denominator = 2.0 * (k + 1) * (k + m + 1);
            double capitalA = (s + 1) * (s + 2) / denominator;
            double capitalB = -(double)m * m * (s + 1) / (denominator * s);
            double capitalC = (double)k * (k + m) * (s + 2) / ((double)(k + 1) * (k + m + 1) * s);
            // (A x + B) with x = 2u − 1.
            a[k] = 2 * capitalA;
            b[k] = capitalB - capitalA;
            c[k] = capitalC;
        }
        return new ZernikeRadialRecurrence(m, a, b, c, scale, 1.0);
    }

    /// <summary>Mahajan's annular polynomials for obscuration ratio ε (0 ≤ ε &lt; 1).</summary>
    public static ZernikeRadialRecurrence Annulus(int m, int maxK, double epsilon)
    {
        Validate(m, maxK);
        if (!(epsilon >= 0 && epsilon < 1))
            throw new ArgumentOutOfRangeException(nameof(epsilon), epsilon, "The obscuration ratio must be in [0, 1).");
        double lower = epsilon * epsilon;
        // The largest product integrated is u^m · p_maxK², degree 2·maxK + m in u; an N-point
        // Gauss rule is exact to degree 2N − 1.
        int points = maxK + m / 2 + 2;
        var (u, w) = GaussLegendre.Rule(points, lower, 1.0);
        for (int i = 0; i < points; i++) w[i] *= Math.Pow(u[i], m);

        double mu0 = 0;
        foreach (double weight in w) mu0 += weight;
        double p0 = 1 / Math.Sqrt(mu0);
        var current = new double[points];
        var previous = new double[points];
        Array.Fill(current, p0);

        var a = new double[maxK];
        var b = new double[maxK];
        var c = new double[maxK];
        double offDiagonal = 0;
        for (int k = 0; k < maxK; k++)
        {
            double alpha = 0;
            for (int i = 0; i < points; i++) alpha += w[i] * u[i] * current[i] * current[i];
            var next = new double[points];
            double norm = 0;
            for (int i = 0; i < points; i++)
            {
                next[i] = (u[i] - alpha) * current[i] - offDiagonal * previous[i];
                norm += w[i] * next[i] * next[i];
            }
            double nextOffDiagonal = Math.Sqrt(norm);
            for (int i = 0; i < points; i++) next[i] /= nextOffDiagonal;
            // b_{k+1} q_{k+1} = (u − α_k) q_k − b_k q_{k−1}.
            a[k] = 1 / nextOffDiagonal;
            b[k] = -alpha / nextOffDiagonal;
            c[k] = offDiagonal / nextOffDiagonal;
            previous = current;
            current = next;
            offDiagonal = nextOffDiagonal;
        }

        // ½ ∫ u^m (s q_k)² du = s²/2 must equal (1 − ε²)/(2(n + 1)).
        var scale = new double[maxK + 1];
        for (int k = 0; k <= maxK; k++) scale[k] = Math.Sqrt((1 - lower) / (m + 2 * k + 1));
        return new ZernikeRadialRecurrence(m, a, b, c, scale, p0);
    }

    /// <summary>
    /// p_k(u) and dp_k/du for k = 0 … <paramref name="count"/> − 1, so that
    /// R_{m+2k}^m(ρ) = ρ^m·Scale(k)·p[k].
    /// </summary>
    public void Evaluate(double u, int count, Span<double> p, Span<double> dp)
    {
        p[0] = _p0;
        dp[0] = 0;
        if (count == 1) return;
        p[1] = (_a[0] * u + _b[0]) * p[0];
        dp[1] = _a[0] * p[0];
        for (int k = 1; k < count - 1; k++)
        {
            double factor = _a[k] * u + _b[k];
            p[k + 1] = factor * p[k] - _c[k] * p[k - 1];
            dp[k + 1] = _a[k] * p[k] + factor * dp[k] - _c[k] * dp[k - 1];
        }
    }

    private static void Validate(int m, int maxK)
    {
        if (m < 0) throw new ArgumentOutOfRangeException(nameof(m), m, "|m| must be 0 or more.");
        if (maxK < 0) throw new ArgumentOutOfRangeException(nameof(maxK), maxK, "The largest k must be 0 or more.");
    }
}
