using System.Numerics;

namespace OpenSim.Solvers.Magnetics;

/// <summary>Closed forms for a round wire carrying its own sinusoidal current.</summary>
public static class RoundWire
{
    /// <summary>R_ac/R_dc of a round wire of <paramref name="diameterMeters"/> carrying its own
    /// current: the exact Bessel solution Re[(z/2)·J₀(z)/J₁(z)] with z = (1 − j)·a/δ, from
    /// J(r) ∝ J₀(κr), κ² = −jωµ₀σ.</summary>
    public static double SkinResistanceFactor(double diameterMeters, double conductivitySiemensPerMeter, double frequencyHz)
    {
        if (!(diameterMeters > 0)) throw new ArgumentOutOfRangeException(nameof(diameterMeters));
        if (!(conductivitySiemensPerMeter > 0)) throw new ArgumentOutOfRangeException(nameof(conductivitySiemensPerMeter));
        if (!(frequencyHz > 0)) return 1;
        double delta = 1 / Math.Sqrt(Math.PI * frequencyHz * MagneticConstants.Mu0 * conductivitySiemensPerMeter);
        double ratio = diameterMeters / 2 / delta;
        if (ratio < 1e-4) return 1;
        if (ratio > 12)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz),
                $"The wire is {ratio:g3} skin depths in radius; the power series loses its digits past 12.");
        var z = new Complex(1, -1) * ratio;
        return (0.5 * z * BesselJ0(z) / BesselJ1(z)).Real;
    }

    private static Complex BesselJ0(Complex z)
    {
        var q = -z * z / 4;
        Complex term = 1, sum = 1;
        for (int m = 1; m <= 80; m++)
        {
            term *= q / (m * (double)m);
            sum += term;
            if (term.Magnitude < 1e-18 * sum.Magnitude) break;
        }
        return sum;
    }

    private static Complex BesselJ1(Complex z)
    {
        var q = -z * z / 4;
        Complex term = 1, sum = 1;
        for (int m = 1; m <= 80; m++)
        {
            term *= q / (m * (double)(m + 1));
            sum += term;
            if (term.Magnitude < 1e-18 * sum.Magnitude) break;
        }
        return z / 2 * sum;
    }
}
