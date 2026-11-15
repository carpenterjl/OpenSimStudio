namespace OpenSim.Optics.FiberCoupling;

/// <summary>
/// A single-mode fiber's Gaussian mode as the Single Mode Coupling analysis defines it (OP-50;
/// docs/Optics-Conventions.md §68): its numerical aperture on x and y, "n times the sin of the
/// half angle to the 1 over e squared intensity point" (OpticStudio 2025 R2 help, "Single Mode
/// Coupling", saved under plans/sources/optics/op-50-help/), and the direction its axis points.
/// <para>
/// <b>The mode in direction space (derived).</b> At its waist the mode is the field
/// exp(−x²/ω0x² − y²/ω0y²). Its plane-wave spectrum, written against the direction cosines
/// (α, β) of each plane wave in a medium of index n (spatial frequency n·α/λ), is
/// π·ω0x·ω0y·exp(−π²·n²·(ω0x²·α² + ω0y²·β²)/λ²), a Gaussian whose intensity falls to 1/e² at
/// n·α = λ/(π·ω0x). That is the help's NA, so ω0 = λ/(π·NA) with λ the wavelength in the
/// system's air, and the spectrum is exp(−n²·(α²/NAx² + β²/NAy²)) up to the constant. Its
/// power, ∫|F|² dα dβ over the whole plane, is π·NAx·NAy/(2n²).
/// </para>
/// <para>
/// <b>Tilt.</b> A fiber whose axis has direction cosines (αt, βt) is the same mode with the
/// linear phase exp(−2πi·n·(αt·x + βt·y)/λ) across its face (the help's "Tilt of the fiber is
/// modeled as adding a linear phase shift to the fiber mode", "Decenters and Tilts"); in
/// direction space that shifts the Gaussian to centre (αt, βt) and leaves its power unchanged.
/// </para>
/// </summary>
public readonly record struct GaussianFiberMode(double NaX, double NaY, double AxisAlpha, double AxisBeta)
{
    /// <summary>The spectrum's amplitude at direction cosines (α, β) in a medium of
    /// <paramref name="index"/>: exp(−n²·((α − αt)²/NAx² + (β − βt)²/NAy²)).</summary>
    public double Spectrum(double alpha, double beta, double index)
    {
        double a = (alpha - AxisAlpha) / NaX, b = (beta - AxisBeta) / NaY;
        return Math.Exp(-index * index * (a * a + b * b));
    }

    /// <summary>∫|F|² dα dβ over the whole plane, π·NAx·NAy/(2n²).</summary>
    public double SpectralPower(double index) => Math.PI * NaX * NaY / (2 * index * index);

    /// <summary>The waist radius λ/(π·NA) on x, meters, for <paramref name="airWavelength"/>.</summary>
    public double WaistX(double airWavelength) => airWavelength / (Math.PI * NaX);

    /// <summary>The waist radius on y.</summary>
    public double WaistY(double airWavelength) => airWavelength / (Math.PI * NaY);

    /// <summary>
    /// The unit axis of a fiber tilted "first around X, then around Y" (help, "Single Mode
    /// Coupling"): R = Rx(θx)·Ry(θy) with each tilt about the axis as the earlier tilt left it
    /// (docs §4), applied to the nominal axis sense·ẑ. Its third column gives
    /// (sin θy, −sin θx·cos θy, cos θx·cos θy), so a tilt about X turns the fiber toward −y.
    /// </summary>
    public static (double X, double Y, double Z) TiltedAxis(double tiltXDegrees, double tiltYDegrees, double sense)
    {
        var (sx, cx) = OpticsConventions.SinCosDegrees(tiltXDegrees);
        var (sy, cy) = OpticsConventions.SinCosDegrees(tiltYDegrees);
        return (sense * sy, -sense * sx * cy, sense * cx * cy);
    }

    /// <summary>Checks the numerical apertures: positive and below the medium index.</summary>
    public static void Check(double naX, double naY, double index, string who)
    {
        if (!(naX > 0) || !(naY > 0) || !double.IsFinite(naX) || !double.IsFinite(naY))
            throw new ArgumentException($"{who}: the numerical apertures must be positive (they are {naX} on x and {naY} on y).");
        if (naX >= index || naY >= index)
            throw new ArgumentException($"{who}: a numerical aperture of {Math.Max(naX, naY)} is not below the medium's index {index}; n·sin θ cannot reach it.");
    }
}
