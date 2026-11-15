using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>
/// First-party Bessel evaluations, first written for the Sommerfeld machinery: J₀/Y₀ on the real
/// axis (the transform kernel) and H₀⁽²⁾ for complex argument (the surface-wave pole
/// term, whose k_p moves off the real axis for lossy substrates). Strategy is series
/// below |x| = 14 and the DLMF 10.17 Hankel asymptotics above — the crossover keeps
/// BOTH sides at ~1e-12 absolute (series cancellation costs ~e^{x} in the last digits;
/// the asymptotic optimal-truncation floor is ~e^{−2x}), two orders past any gate
/// here. H₀⁽²⁾(z) comes from K₀ via H₀⁽²⁾(z) = (2j/π)·K₀(jz), valid on Im(z) ≤ 0 —
/// exactly the half-plane where physical pole wavenumbers live; K₀'s ascending series
/// is all-positive (no cancellation) and its |z| ≥ 12 asymptotic is a plain e^{−z}
/// envelope, so the same crossover reasoning applies.
/// </summary>
public static class Bessel
{
    private const double EulerGamma = 0.57721566490153286060651209008240243;
    private const double SeriesLimit = 14.0;

    public static double J0(double x)
    {
        x = Math.Abs(x);
        return x < SeriesLimit ? J0Series(x) : J0Asymptotic(x);
    }

    public static double Y0(double x)
    {
        if (x <= 0) throw new ArgumentOutOfRangeException(nameof(x), "Y0 needs x > 0.");
        return x < SeriesLimit ? Y0Series(x) : Y0Asymptotic(x);
    }

    // The two branches stay separately callable (internal) so the tests can assert
    // their AGREEMENT across the crossover — series and asymptotics share no algebra,
    // which makes that agreement a genuine oracle.

    internal static double J0Series(double x)
    {
        // Σ (−x²/4)^k / (k!)² — alternating; the crossover bounds the cancellation.
        double q = -0.25 * x * x;
        double term = 1, sum = 1;
        for (int k = 1; k < 60; k++)
        {
            term *= q / (k * (double)k);
            sum += term;
            if (Math.Abs(term) < 1e-18 * Math.Abs(sum) + 1e-300) break;
        }
        return sum;
    }

    internal static double J0Asymptotic(double x)
    {
        var (p, q) = HankelPq(x);
        double omega = x - Math.PI / 4;
        var (sin, cos) = Math.SinCos(omega);
        return Math.Sqrt(2 / (Math.PI * x)) * (p * cos - q * sin);
    }

    internal static double Y0Series(double x)
    {
        // Y0 = (2/π)[(ln(x/2) + γ) J0 + Σ_{k≥1} (−1)^{k+1} H_k (x²/4)^k/(k!)²].
        double q = 0.25 * x * x;
        double term = 1, sum = 0, harmonic = 0;
        for (int k = 1; k < 60; k++)
        {
            term *= q / (k * (double)k);
            harmonic += 1.0 / k;
            double addend = (k % 2 == 1 ? term : -term) * harmonic;
            sum += addend;
            if (Math.Abs(addend) < 1e-18 * (Math.Abs(sum) + 1) + 1e-300) break;
        }
        return (2 / Math.PI) * ((Math.Log(x / 2) + EulerGamma) * J0Series(x) + sum);
    }

    internal static double Y0Asymptotic(double x)
    {
        var (p, q) = HankelPq(x);
        double omega = x - Math.PI / 4;
        var (sin, cos) = Math.SinCos(omega);
        return Math.Sqrt(2 / (Math.PI * x)) * (p * sin + q * cos);
    }

    /// <summary>The DLMF 10.17 asymptotic sums P (even a_k) and Q (odd a_k) for ν = 0,
    /// a_0 = 1, a_k = −a_{k−1}(2k−1)²/(8k), truncated at the smallest term.</summary>
    private static (double P, double Q) HankelPq(double x)
    {
        double p = 1, q = 0;
        double a = 1, invX = 1 / x, power = 1;
        double previousMagnitude = double.MaxValue;
        for (int k = 1; k <= 40; k++)
        {
            a *= -((2 * k - 1.0) * (2 * k - 1.0)) / (8.0 * k);
            power *= invX;
            double term = a * power;
            double magnitude = Math.Abs(term);
            if (magnitude >= previousMagnitude) break; // divergent tail begins
            previousMagnitude = magnitude;
            // P = Σ (−1)^m a_{2m}/x^{2m}, Q = Σ (−1)^m a_{2m+1}/x^{2m+1} (DLMF 10.17.3):
            // for a_k index k, the (−1)^m prefactor is +,−,+,… per PAIR of k values.
            if (k % 2 == 0) p += (k % 4 == 0 ? term : -term);
            else q += ((k - 1) % 4 == 0 ? term : -term);
        }
        return (p, q);
    }

    // ------------------------------------------------------------------ real J₁
    private const double J1SeriesLimit = 2.0;
    private const double J1AsymptoticLimit = 25.0;

    /// <summary>J₁(x) on the real axis, accurate to about 1e-15 absolute everywhere. Three
    /// ranges, each used only where it loses no digits: the ascending series below |x| = 2 (no
    /// cancellation there), backward recurrence between (Miller's algorithm, normalised by
    /// J₀ + 2·ΣJ₂ₖ = 1, DLMF 10.12.4; stable because Jₙ is the minimal solution going down), and
    /// the DLMF 10.17 Hankel asymptotics from |x| = 25, where the smallest term is below
    /// e⁻⁵⁰. J₁ is odd.</summary>
    public static double J1(double x)
    {
        double ax = Math.Abs(x);
        double value = ax < J1SeriesLimit ? J1Series(ax)
            : ax < J1AsymptoticLimit ? J1Recurrence(ax)
            : J1Asymptotic(ax);
        return x < 0 ? -value : value;
    }

    internal static double J1Series(double x)
    {
        // (x/2) Σ (−x²/4)^k / (k!(k+1)!)
        double q = -0.25 * x * x;
        double term = 1, sum = 1;
        for (int k = 1; k < 80; k++)
        {
            term *= q / (k * (k + 1.0));
            sum += term;
            if (Math.Abs(term) < 1e-18 * Math.Abs(sum) + 1e-300) break;
        }
        return 0.5 * x * sum;
    }

    internal static double J1Recurrence(double x)
    {
        // Start well above the turning point n ≈ x, where J_n is negligible, and recur down:
        // J_{k−1} = (2k/x) J_k − J_{k+1}.
        int n = 2 * (int)Math.Ceiling(0.5 * x) + 40;            // even
        double above = 0, current = 1;                          // J_{n+1}, J_n, unnormalised
        double sum = 2 * current;
        for (int k = n; k >= 1; k--)
        {
            double below = 2 * k / x * current - above;         // J_{k−1}
            above = current;
            current = below;
            if (k - 1 >= 2 && (k - 1) % 2 == 0) sum += 2 * current;
        }
        sum += current;                                         // J_0
        return above / sum;                                     // J_1
    }

    internal static double J1Asymptotic(double x)
    {
        // DLMF 10.17.3 with ν = 1: a_k = a_{k−1}(4 − (2k−1)²)/(8k), ω = x − 3π/4.
        double p = 1, q = 0;
        double a = 1, invX = 1 / x, power = 1;
        double previousMagnitude = double.MaxValue;
        for (int k = 1; k <= 60; k++)
        {
            a *= (4.0 - (2 * k - 1.0) * (2 * k - 1.0)) / (8.0 * k);
            power *= invX;
            double term = a * power;
            double magnitude = Math.Abs(term);
            if (magnitude >= previousMagnitude) break;
            previousMagnitude = magnitude;
            if (k % 2 == 0) p += k % 4 == 0 ? term : -term;
            else q += (k - 1) % 4 == 0 ? term : -term;
            if (magnitude < 1e-18) break;
        }
        double omega = x - 3 * Math.PI / 4;
        var (sin, cos) = Math.SinCos(omega);
        return Math.Sqrt(2 / (Math.PI * x)) * (p * cos - q * sin);
    }

    /// <summary>J₀(z) and J₁(z) for complex z with Re(z) ≥ 0 — the transform kernels on a
    /// Sommerfeld path lifted off the real axis (FU-7's probe near field). Same strategy as the
    /// real J₀: the ascending series below |z| = 14 (J₁ = (z/2)·Σ(−z²/4)ᵏ/(k!(k+1)!)), the
    /// DLMF 10.17 Hankel asymptotics above, with a_k(ν) = a_{k−1}(4ν² − (2k−1)²)/(8k) and
    /// ω = z − νπ/2 − π/4, truncated at the smallest term. On the real axis J₀ reproduces
    /// <see cref="J0(double)"/>; J₁ is gated against −dJ₀/dz and the series/asymptotic crossover.</summary>
    public static (Complex J0, Complex J1) J01(Complex z) =>
        z.Magnitude < SeriesLimit ? J01Series(z) : J01Asymptotic(z);

    internal static (Complex J0, Complex J1) J01Series(Complex z)
    {
        var q = -0.25 * z * z;
        Complex t0 = 1, s0 = 1, t1 = 1, s1 = 1;
        for (int k = 1; k < 80; k++)
        {
            t0 *= q / (k * (double)k);
            t1 *= q / (k * (k + 1.0));
            s0 += t0;
            s1 += t1;
            if (t0.Magnitude < 1e-18 * s0.Magnitude && t1.Magnitude < 1e-18 * s1.Magnitude) break;
        }
        return (s0, 0.5 * z * s1);
    }

    internal static (Complex J0, Complex J1) J01Asymptotic(Complex z)
    {
        var scale = Complex.Sqrt(2 / (Math.PI * z));
        var (p0, q0) = HankelPq(z, 0);
        var (p1, q1) = HankelPq(z, 1);
        var w0 = z - Math.PI / 4;
        var w1 = z - 3 * Math.PI / 4;
        return (scale * (p0 * Complex.Cos(w0) - q0 * Complex.Sin(w0)),
                scale * (p1 * Complex.Cos(w1) - q1 * Complex.Sin(w1)));
    }

    /// <summary>The DLMF 10.17 P and Q sums for order ν at complex z, truncated at the
    /// smallest term.</summary>
    private static (Complex P, Complex Q) HankelPq(Complex z, int nu)
    {
        Complex p = 1, q = 0, power = 1;
        var invZ = 1 / z;
        double a = 1, previous = double.MaxValue;
        for (int k = 1; k <= 40; k++)
        {
            a *= (4.0 * nu * nu - (2 * k - 1.0) * (2 * k - 1.0)) / (8.0 * k);
            power *= invZ;
            var term = a * power;
            double magnitude = term.Magnitude;
            if (magnitude >= previous) break;
            previous = magnitude;
            if (k % 2 == 0) p += k % 4 == 0 ? term : -term;
            else q += (k - 1) % 4 == 0 ? term : -term;
        }
        return (p, q);
    }

    /// <summary>Modified Bessel K₀ for complex z with Re(z) ≥ 0 (principal branch).</summary>
    public static Complex K0(Complex z)
    {
        if (z == Complex.Zero) throw new ArgumentOutOfRangeException(nameof(z));
        if (z.Magnitude < 12)
        {
            // Ascending series: K0 = −(ln(z/2) + γ) I0(z) + Σ_{k≥1} H_k (z²/4)^k/(k!)².
            var q = 0.25 * z * z;
            Complex term = 1, i0 = 1, sum = 0;
            double harmonic = 0;
            for (int k = 1; k < 80; k++)
            {
                term *= q / (k * (double)k);
                harmonic += 1.0 / k;
                i0 += term;
                sum += harmonic * term;
                if (term.Magnitude < 1e-18 * (sum.Magnitude + i0.Magnitude) + 1e-300) break;
            }
            return -(Complex.Log(z / 2) + EulerGamma) * i0 + sum;
        }
        // DLMF 10.40.2: K0(z) ~ √(π/2z) e^{−z} Σ a_k/z^k (same signed a_k as above).
        Complex s = 1, power = 1;
        double a = 1;
        double previousMagnitude = double.MaxValue;
        for (int k = 1; k <= 40; k++)
        {
            a *= -((2 * k - 1.0) * (2 * k - 1.0)) / (8.0 * k);
            power /= z;
            var term = a * power;
            if (term.Magnitude >= previousMagnitude) break;
            previousMagnitude = term.Magnitude;
            s += term;
        }
        return Complex.Sqrt(Math.PI / 2 / z) * Complex.Exp(-z) * s;
    }

    /// <summary>Hankel H₀⁽²⁾(z) = (2j/π) K₀(jz), valid for Im(z) ≤ 0 — the physical
    /// (outward, decaying-with-loss) surface-wave wavenumber half-plane.</summary>
    public static Complex H02(Complex z)
    {
        if (z.Imaginary > 1e-12 * z.Magnitude)
            throw new ArgumentOutOfRangeException(nameof(z),
                "H0(2) is evaluated via K0(jz), valid only for Im(z) ≤ 0 (physical poles).");
        return new Complex(0, 2 / Math.PI) * K0(Complex.ImaginaryOne * z);
    }

    // OP-47: integer orders, for the thin sinusoidal phase grating's J_m(a)² efficiencies.

    /// <summary>
    /// J_n(x) for integer n and real x (OP-47). J_{−n} = (−1)ⁿJ_n (DLMF 10.4.1) and
    /// J_n(−x) = (−1)ⁿJ_n(x) (derived: the ascending series of J_n is (x/2)ⁿ times a series in x²).
    /// n = 1 is <see cref="J1"/>; every other order comes from Miller's backward recurrence
    /// J_{k−1} = (2k/x)J_k − J_{k+1} (DLMF 10.6.1) from an order well above both n and |x|,
    /// normalised by 1 = J₀ + 2J₂ + 2J₄ + … (DLMF 10.12.4) — the procedure DLMF 10.74(iv) gives for
    /// J_n, which is the minimal solution of the recurrence, so the backward direction is stable at
    /// every order — rescaling the unnormalised values when they grow past 10²⁰⁰. The absolute
    /// error is a few ulps of 1 (the normalising sum's terms are bounded by 1).
    /// </summary>
    public static double Jn(int n, double x)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x), x, "J_n needs a finite argument.");
        if (n == int.MinValue) throw new ArgumentOutOfRangeException(nameof(n), n, "The order is out of range.");
        double sign = 1;
        if (n < 0)
        {
            n = -n;
            if ((n & 1) == 1) sign = -sign;
        }
        if (x < 0)
        {
            x = -x;
            if ((n & 1) == 1) sign = -sign;
        }
        if (n == 1) return sign * J1(x);
        if (x == 0) return n == 0 ? 1 : 0;
        int start = 2 * ((Math.Max(n, (int)Math.Ceiling(x)) + 30 + (int)Math.Sqrt(40.0 * Math.Max(n, x))) / 2);   // even
        double above = 0, here = 1e-300, sum = 0, wanted = 0;
        for (int k = start; k >= 1; k--)
        {
            double below = 2 * k / x * here - above;   // J_{k−1}
            above = here;
            here = below;
            int order = k - 1;
            if (order == n) wanted = here;
            if (order >= 2 && (order & 1) == 0) sum += 2 * here;
            if (Math.Abs(here) > 1e200)
            {
                here *= 1e-200; above *= 1e-200; sum *= 1e-200; wanted *= 1e-200;
            }
        }
        sum += here;   // J_0
        return sign * wanted / sum;
    }
}
