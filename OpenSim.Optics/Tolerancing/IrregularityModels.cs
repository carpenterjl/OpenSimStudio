namespace OpenSim.Optics.Tolerancing;

/// <summary>
/// The irregularity models of the tolerance operands (docs/Optics-Conventions.md §36), read from
/// the OpticStudio 2025 R2 help pages named on each member (saved under
/// plans/sources/optics/op-63-help/, equations under images/).
/// </summary>
public static class IrregularityModels
{
    /// <summary>
    /// The peak-to-valley, over the unit disk, of the TIRR shape f = ρ⁴/2 + ρ′y²/2 after removing
    /// its least-squares fit by piston, tilt and power (1, x, y, ρ²): 17/32. Derived here, not
    /// copied: with u = ρ² (uniform on [0, 1] under the disk's area weight) and ρ′y² = u(1 + cos 2φ′)/2,
    /// f = u²/2 + u/4 + (u/4)·cos 2φ′. The u/4 term is power; the best line through u² is u − 1/6
    /// (slope cov(u², u)/var(u) = (1/12)/(1/12) = 1, intercept 1/3 − 1/2), so the residual is
    /// (u² − u + 1/6)/2 + (u/4)·cos 2φ′, with no tilt. Its maximum is 1/3 (u = 1, cos = 1) and its
    /// minimum −19/96 (u = 3/4, cos = −1), so the peak-to-valley is 1/3 + 19/96 = 51/96 = 17/32.
    /// The raw shape runs from 0 to 1 (peak-to-valley 1). The test suite checks this on a grid.
    /// </summary>
    public const double TirrPowerRemovedPeakToValley = 17.0 / 32.0;

    /// <summary>
    /// The Irregular surface's spherical and astigmatic terms Zs and Za (equal) for a TIRR of
    /// <paramref name="fringes"/> double-pass fringes at <paramref name="testWavelength"/> (both
    /// terms in the wavelength's unit). OpticStudio's model ("TIRR: Tolerance on Surface
    /// Irregularity", the equation ΔZ = (λt/2)(W/2·ρ⁴ + W/2·ρ′y²)) gives Zs = Za = λt·W/4: the raw
    /// sag change runs from 0 to λt·W/2, one double-pass fringe per W. The default
    /// <see cref="IrregularityModel.PowerRemoved"/> scales the same shape so that its
    /// peak-to-valley after removing piston, tilt and power is λt·W/2 — the irregularity an
    /// interferometer or test plate reads once power is adjusted out ("Per ISO-10110, power and
    /// tilt should be removed", help page "ISOB") — which is larger by 32/17.
    /// </summary>
    public static double TirrTerm(double fringes, double testWavelength, IrregularityModel model)
    {
        double opticStudio = testWavelength * fringes / 4;
        return model == IrregularityModel.OpticStudio ? opticStudio : opticStudio / TirrPowerRemovedPeakToValley;
    }

    /// <summary>
    /// TEXI's coefficient on each Fringe Zernike term: c = (λ/2)·f/√n, f the double-pass fringes,
    /// n = Int2 − Int3 + 1 the number of terms (help page "TEXI: Tolerance on Surface Irregularity
    /// Using the Fringe Zernike Model", the equation for c).
    /// </summary>
    public static double TexiCoefficient(double fringes, double testWavelength, int terms)
    {
        if (terms < 1) throw new ArgumentOutOfRangeException(nameof(terms), terms, "TEXI: there must be at least one Zernike term.");
        return testWavelength / 2 * fringes / Math.Sqrt(terms);
    }

    /// <summary>
    /// TEZI's coefficient on each Standard Zernike term for the sensitivity analysis: all equal,
    /// with "the square root of the sum of the squares of the coefficients" equal to the RMS
    /// (help page "TEZI: Tolerance on Surface Irregularity Using the Standard Zernike Model"), so
    /// c = rms/√n. The Standard (Noll) terms are orthonormal over the unit disk, so this is also
    /// the RMS of the surface change without piston.
    /// </summary>
    public static double TeziCoefficient(double rms, int terms)
    {
        if (terms < 1) throw new ArgumentOutOfRangeException(nameof(terms), terms, "TEZI: there must be at least one Zernike term.");
        return rms / Math.Sqrt(terms);
    }

    /// <summary>
    /// ISOA's Zernike Standard coefficients for a peak-to-valley power <paramref name="max"/>
    /// (help page "ISOA": A4 = ±Max/(2√3), and the piston A1 = √3·A4 that keeps the vertex sag
    /// zero, so z = 2√3·A4·ρ²). ISOA is defined; its composite add-on is not built.
    /// </summary>
    public static (double A1, double A4) IsoaCoefficients(double max)
    {
        double a4 = max / (2 * Math.Sqrt(3));
        return (Math.Sqrt(3) * a4, a4);
    }
}
