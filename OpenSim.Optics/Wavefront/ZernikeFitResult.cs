namespace OpenSim.Optics.Wavefront;

/// <summary>
/// A least-squares Zernike fit. <see cref="Coefficients"/>[j − 1] is term j, in the units of the
/// fitted values.
/// </summary>
/// <param name="Coefficients">The fitted coefficient of each term.</param>
/// <param name="ResidualRms">√(Σ wᵢ rᵢ² / Σ wᵢ) over the points used, rᵢ = fit − value: the plain
/// RMS of the fit error when every weight is 1.</param>
/// <param name="MaxResidual">The largest |rᵢ| over the points used.</param>
/// <param name="Rank">How many singular values of the design matrix are above the rank
/// tolerance. Below <see cref="TermCount"/>, the points cannot tell some combination of terms
/// apart (too few points, or a pupil masked so that two terms agree on it); the coefficients
/// are then the minimum-norm solution, and that combination is not determined by the data.</param>
/// <param name="ConditionNumber">σ_max/σ_min of the weighted design matrix with its columns
/// scaled to unit length; infinity when rank deficient. Near 1 for a full, densely sampled
/// pupil; it grows as vignetting or sparse sampling makes the terms less distinguishable.</param>
/// <param name="SingularValues">The singular values behind <see cref="Rank"/> and
/// <see cref="ConditionNumber"/>, largest first.</param>
/// <param name="PointsUsed">The points that entered the fit: unmasked, with positive weight.</param>
public sealed record ZernikeFitResult(
    IReadOnlyList<double> Coefficients,
    double ResidualRms,
    double MaxResidual,
    int Rank,
    double ConditionNumber,
    IReadOnlyList<double> SingularValues,
    int PointsUsed)
{
    /// <summary>The number of terms fitted.</summary>
    public int TermCount => Coefficients.Count;

    /// <summary>True when every coefficient is determined by the data.</summary>
    public bool IsFullRank => Rank == TermCount;
}
