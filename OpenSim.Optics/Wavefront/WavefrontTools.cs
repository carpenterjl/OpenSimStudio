namespace OpenSim.Optics.Wavefront;

/// <summary>Peak-to-valley and RMS of a sampled wavefront.</summary>
/// <param name="PeakToValley">Largest minus smallest sample.</param>
/// <param name="Rms">√(mean (W − mean)²): the RMS with the mean (piston) taken out, every sample
/// weighted equally.</param>
/// <param name="Mean">The mean sample.</param>
/// <param name="Points">The finite samples counted.</param>
public sealed record WavefrontStatistics(double PeakToValley, double Rms, double Mean, int Points);

/// <summary>Tilt and focus removal and the statistics of sampled wavefronts (OP-35).</summary>
public static class WavefrontTools
{
    /// <summary>The statistics of the finite samples; throws when there are none.</summary>
    public static WavefrontStatistics Statistics(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        double sum = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
        int count = 0;
        foreach (double v in values)
        {
            if (!double.IsFinite(v)) continue;
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
            count++;
        }
        if (count == 0) throw new ArgumentException("The wavefront has no valid sample (every ray failed).", nameof(values));
        double mean = sum / count, squares = 0;
        foreach (double v in values)
            if (double.IsFinite(v)) squares += (v - mean) * (v - mean);
        return new WavefrontStatistics(max - min, Math.Sqrt(squares / count), mean, count);
    }

    /// <summary>
    /// The samples with their least-squares tilt (x and y) and/or focus (2ρ² − 1) taken out. The
    /// fit is of Fringe Zernike terms 1–3 (piston, x, y) or 1–4 (with focus) jointly
    /// (<see cref="ZernikeFit"/>; Fringe terms 2, 3 and 4 are x, y and 2ρ² − 1 on the normalised
    /// pupil), and only the removed terms are subtracted, so piston is left alone. Non-finite
    /// samples are kept as they are and left out of the fit.
    /// </summary>
    /// <param name="removed">The subtracted coefficients (x tilt, y tilt, focus), 0 where not
    /// removed, in the units of the values.</param>
    public static double[] Remove(IReadOnlyList<double> px, IReadOnlyList<double> py, IReadOnlyList<double> values,
        bool tilt, bool focus, out (double TiltX, double TiltY, double Focus) removed)
    {
        ArgumentNullException.ThrowIfNull(px);
        ArgumentNullException.ThrowIfNull(py);
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToArray();
        removed = (0, 0, 0);
        if (!tilt && !focus) return result;
        int terms = focus ? 4 : 3;
        var mask = values.Select(double.IsFinite).ToArray();
        var fit = ZernikeFit.Fit(new ZernikeBasis(ZernikeOrdering.Fringe, terms), px, py, values, mask: mask);
        if (!fit.IsFullRank)
            throw new ArgumentException($"Tilt or focus removal: the {fit.PointsUsed} valid samples cannot tell tilt and focus apart (rank {fit.Rank} of {terms}); sample the pupil more finely.");
        double cx = tilt ? fit.Coefficients[1] : 0, cy = tilt ? fit.Coefficients[2] : 0;
        double cf = focus ? fit.Coefficients[3] : 0;
        for (int i = 0; i < result.Length; i++)
        {
            if (!mask[i]) continue;
            double x = px[i], y = py[i];
            result[i] -= cx * x + cy * y + cf * (2 * (x * x + y * y) - 1);
        }
        removed = (cx, cy, cf);
        return result;
    }
}
