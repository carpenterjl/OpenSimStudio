namespace OpenSim.Optics.Wavefront;

/// <summary>
/// The three Zernike sets OpticStudio fits. They differ in the order of the terms, in the
/// factor in front of each term, and (for the annular set) in the pupil they are orthogonal on.
/// In all three the angle is measured counter-clockwise from the +x axis of the normalised
/// pupil, and the radius is the normalised pupil radius (docs/Optics-Conventions.md §7).
/// </summary>
public enum ZernikeOrdering
{
    /// <summary>
    /// The Fringe, or University of Arizona, set: Z = R_n^m(ρ)·cos mθ or sin mθ, every term
    /// equal to 1 in magnitude at the pupil edge and not normalised. The order is Table III of
    /// Wyant and Creath (1992) shifted by one (their term 0 is Fringe term 1), which is also the
    /// table in OpticStudio's "Zernike Fringe Coefficients" help. OpticStudio stops at 37 terms;
    /// later terms follow the same rule (<see cref="ZernikeIndex.FromFringe"/>).
    /// </summary>
    Fringe = 0,

    /// <summary>
    /// The Standard set in Noll's (1976) order and normalisation: each term has unit RMS over
    /// the unit disk, so a coefficient is that term's RMS contribution. OpticStudio's "Zernike
    /// Standard Coefficients".
    /// </summary>
    Standard = 1,

    /// <summary>
    /// Mahajan's (1981, 1994) annular polynomials in Noll's order: orthonormal over the annulus
    /// ε ≤ ρ ≤ 1, with unit RMS over the annulus. Equal to <see cref="Standard"/> when ε = 0.
    /// OpticStudio's "Zernike Annular Coefficients".
    /// </summary>
    Annular = 2,
}
