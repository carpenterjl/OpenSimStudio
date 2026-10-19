namespace OpenSim.Tests.Si;

/// <summary>
/// Closed forms the line tests share: the complete elliptic integral by the AGM and the exact
/// zero-thickness symmetric stripline — Cohn (1954), and the same expression as Rautio's stripline
/// standard (IEEE T-MTT 42(11), 1994, p. 2047) writes it — with η₀/4 = µ₀c/4, not the
/// often-quoted 30π, which that paper notes is 0.07 % high.
/// </summary>
internal static class ExactLines
{
    /// <summary>η₀/4 [Ω] with η₀ = µ₀·c, µ₀ = 4π·10⁻⁷ H/m (the value OpenSim.Rf uses).</summary>
    public const double QuarterEta0 = 4e-7 * Math.PI * 299792458.0 / 4;

    /// <summary>Complete elliptic integral of the first kind by the AGM, K = π / (2·AGM(1, k′)),
    /// taken as a function of the COMPLEMENTARY modulus k′ = √(1 − k²) — for a wide strip
    /// tanh(πw/2b) rounds to exactly 1 and forming k′ from it would lose everything.</summary>
    public static double EllipticKOfComplement(double complementaryModulus)
    {
        double a = 1, b = complementaryModulus;
        for (int i = 0; i < 60 && Math.Abs(a - b) > 1e-16 * a; i++)
            (a, b) = (0.5 * (a + b), Math.Sqrt(a * b));
        return Math.PI / (2 * a);
    }

    /// <summary>Z₀ of a zero-thickness strip of width <paramref name="w"/> centred between planes
    /// <paramref name="b"/> apart: Z₀·√εr = (η₀/4)·K(k′)/K(k), k = tanh(πw/2b), k′ = sech(πw/2b).</summary>
    public static double StriplineZ0(double w, double b, double epsR)
    {
        double x = Math.PI * w / (2 * b);
        // K(k′) has complementary modulus k = tanh, K(k) has k′ = sech.
        return QuarterEta0 / Math.Sqrt(epsR) * EllipticKOfComplement(Math.Tanh(x)) / EllipticKOfComplement(1 / Math.Cosh(x));
    }
}
