using System.Numerics;

namespace OpenSim.Optics.NonSequential.Volume;

/// <summary>
/// Lorenz–Mie scattering by a homogeneous sphere of real relative index <see cref="RelativeIndex"/>
/// and size parameter <see cref="SizeParameter"/> x = 2π·n_host·r/λ (plan item OP-81;
/// docs/Optics-Conventions.md §77).
/// <list type="bullet">
/// <item><b>Efficiencies</b> (Wikipedia, "Mie scattering", section "Mie scattering efficiencies"):
/// Q_sca = (2/x²) Σ (2n + 1)(|a_n|² + |b_n|²), Q_ext = (2/x²) Σ (2n + 1) Re(a_n + b_n); for a real
/// index they are equal. Asymmetry g = (4/(x² Q_sca)) Σ [n(n + 2)/(n + 1) Re(a_n a*_{n+1} + b_n b*_{n+1})
/// + (2n + 1)/(n(n + 1)) Re(a_n b*_n)], as miepython 3.3.1 (MIT) sums it.</item>
/// <item><b>Coefficients</b> a_n, b_n from the logarithmic derivative D_n(mx) = ψ_n′/ψ_n by
/// downward recurrence D_{n−1} = n/z − 1/(D_n + n/z) started well above max(N, |mx|), the
/// Riccati–Bessel ψ_n(x) by Miller's downward recurrence ψ_{n−1} = (2n + 1)/x·ψ_n − ψ_{n+1}
/// normalised to ψ₀ = sin x or ψ₁ = sin x/x − cos x, and χ_n(x) upward from χ₀ = cos x,
/// χ₁ = cos x/x + sin x, ξ_n = ψ_n + iχ_n; a_n = ((D_n/m + n/x)ψ_n − ψ_{n−1})/((D_n/m + n/x)ξ_n − ξ_{n−1}),
/// b_n the same with m·D_n. This is the scheme of miepython 3.3.1's <c>_an_bn_py</c> (read as
/// documentation; it follows Wiscombe's MIEV0 notes and Bohren and Huffman's BHMIE, neither of
/// which was available), and its series stops at Wiscombe's N = ⌊x + 4.05·x^(1/3) + 2⌋, for which
/// miepython states "the error when the series is summed is around 1e-6".</item>
/// <item><b>Amplitudes</b> S₁ = Σ (2n + 1)/(n(n + 1))(a_n π_n + b_n τ_n), S₂ = Σ … (a_n τ_n + b_n π_n),
/// π_n, τ_n by miepython's recurrence; the phase function for unpolarized light is
/// p(μ) = (|S₁|² + |S₂|²)/(2π x² Q_sca), so that ∫ p dΩ = 1 (derived from
/// C_sca = (1/k²)∫ (|S₁|² + |S₂|²)/2 dΩ and Q_sca = C_sca/(πr²)).</item>
/// </list>
/// Absorbing (complex-index) particles are not built.
/// </summary>
public sealed class NscMie
{
    private readonly Complex[] _a, _b;

    public double RelativeIndex { get; }
    public double SizeParameter { get; }

    /// <summary>Terms summed (Wiscombe's N).</summary>
    public int Terms => _a.Length;

    public double Qext { get; }
    public double Qsca { get; }
    public double Asymmetry { get; }

    public NscMie(double relativeIndex, double sizeParameter)
    {
        if (!(relativeIndex > 0) || !double.IsFinite(relativeIndex))
            throw new ArgumentException($"Mie scattering: the relative index must be positive and finite; it is {relativeIndex}.");
        if (!(sizeParameter > 0) || !double.IsFinite(sizeParameter))
            throw new ArgumentException($"Mie scattering: the size parameter 2πnr/λ must be positive and finite; it is {sizeParameter}.");
        if (sizeParameter > 1e5)
            throw new ArgumentException($"Mie scattering: the size parameter {sizeParameter:g6} is above 100000 (over 100000 series terms); use a geometric model for so large a particle.");
        RelativeIndex = relativeIndex;
        SizeParameter = sizeParameter;
        (_a, _b) = Coefficients(relativeIndex, sizeParameter);
        double x2 = sizeParameter * sizeParameter, ext = 0, sca = 0, asy = 0;
        int n = _a.Length;
        for (int k = 0; k < n; k++)
        {
            double order = k + 1, c = 2 * order + 1;
            ext += c * (_a[k].Real + _b[k].Real);
            sca += c * (_a[k].Real * _a[k].Real + _a[k].Imaginary * _a[k].Imaginary + _b[k].Real * _b[k].Real + _b[k].Imaginary * _b[k].Imaginary);
            if (k + 1 < n)
            {
                asy += order * (order + 2) / (order + 1) * (_a[k] * Complex.Conjugate(_a[k + 1]) + _b[k] * Complex.Conjugate(_b[k + 1])).Real;
                asy += c / (order * (order + 1)) * (_a[k] * Complex.Conjugate(_b[k])).Real;
            }
        }
        Qext = 2 * ext / x2;
        Qsca = 2 * sca / x2;
        Asymmetry = Qsca > 0 ? 4 * asy / (x2 * Qsca) : 0;
        if (!double.IsFinite(Qsca) || !double.IsFinite(Asymmetry))
            throw new InvalidOperationException($"Mie scattering (m = {relativeIndex:g6}, x = {sizeParameter:g6}): the series did not give finite efficiencies.");
    }

    /// <summary>Wiscombe's number of terms.</summary>
    public static int WiscombeTerms(double x) => (int)(x + 4.05 * Math.Pow(x, 0.33333) + 2.0);

    private static (Complex[] A, Complex[] B) Coefficients(double m, double x)
    {
        int nTerms = WiscombeTerms(x);
        double mx = m * x;
        // D_n(mx) downward from far above both N and |mx|, where D is insensitive to its start.
        int nStart = (int)Math.Max(nTerms, mx) + 16 + (int)(1.5 * Math.Sqrt(mx));
        var d = new double[nTerms + 1];
        double dn = 0;
        for (int n = nStart; n >= 1; n--)
        {
            dn = n / mx - 1 / (dn + n / mx);
            if (n - 1 <= nTerms) d[n - 1] = dn;   // d[n−1] = D_{n−1}
        }
        // Here d[k] = D_k; the loop's last step produced D_0.
        var psi = PsiDownward(x, nTerms);
        var a = new Complex[nTerms];
        var b = new Complex[nTerms];
        double chiPrev = Math.Cos(x), chi = Math.Cos(x) / x + Math.Sin(x);
        for (int n = 1; n <= nTerms; n++)
        {
            double nx = n / x;
            var xiPrev = new Complex(psi[n - 1], chiPrev);
            var xi = new Complex(psi[n], chi);
            double ta = d[n] / m + nx, tb = d[n] * m + nx;
            a[n - 1] = (ta * psi[n] - psi[n - 1]) / (ta * xi - xiPrev);
            b[n - 1] = (tb * psi[n] - psi[n - 1]) / (tb * xi - xiPrev);
            double next = (2 * n + 1) / x * chi - chiPrev;
            chiPrev = chi;
            chi = next;
        }
        return (a, b);
    }

    private static double[] PsiDownward(double x, int nStop)
    {
        int nStart = (int)Math.Max(nStop, x) + 25 + (int)(1.5 * Math.Sqrt(x));
        var psi = new double[nStart + 2];
        psi[nStart] = 1e-50;
        for (int n = nStart; n >= 1; n--)
        {
            psi[n - 1] = (2 * n + 1) / x * psi[n] - psi[n + 1];
            if (Math.Abs(psi[n - 1]) > 1e200)
                for (int k = n - 1; k <= nStart + 1; k++) psi[k] /= 1e200;
        }
        double psi0 = Math.Sin(x), psi1 = Math.Sin(x) / x - Math.Cos(x);
        double scale = Math.Abs(psi1) > Math.Abs(psi0) ? psi1 / psi[1] : psi0 / psi[0];
        var r = new double[nStop + 1];
        for (int k = 0; k <= nStop; k++) r[k] = psi[k] * scale;
        return r;
    }

    /// <summary>The amplitudes S₁(μ), S₂(μ) (Bohren and Huffman's normalisation) at μ = cos θ.</summary>
    public (Complex S1, Complex S2) Amplitudes(double mu)
    {
        int n = _a.Length;
        Complex s1 = 0, s2 = 0;
        double piPrev = 0, pi = 1;
        for (int k = 1; k <= n; k++)
        {
            double tau = k * mu * pi - (k + 1) * piPrev;
            double scale = (2.0 * k + 1) / (k * (k + 1.0));
            s1 += scale * (_a[k - 1] * pi + _b[k - 1] * tau);
            s2 += scale * (_a[k - 1] * tau + _b[k - 1] * pi);
            double next = ((2 * k + 1) * mu * pi - (k + 1) * piPrev) / k;
            piPrev = pi;
            pi = next;
        }
        return (s1, s2);
    }

    /// <summary>The phase function for unpolarized light, per steradian, ∫ p dΩ = 1.</summary>
    public double Phase(double mu)
    {
        var (s1, s2) = Amplitudes(mu);
        double i = s1.Real * s1.Real + s1.Imaginary * s1.Imaginary + s2.Real * s2.Real + s2.Imaginary * s2.Imaginary;
        return i / (2 * Math.PI * SizeParameter * SizeParameter * Qsca);
    }
}
