using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// First-party Bessel evaluations for the Sommerfeld machinery: J₀/Y₀ on the real
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
internal static class Bessel
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
}
