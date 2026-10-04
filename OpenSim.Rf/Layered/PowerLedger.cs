using System.Globalization;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Where the input power of an antenna on a grounded substrate went, in words. The far
/// field gives the radiated power and the surface-wave poles give what the slab guides
/// away; on a lossy substrate the rest is heat in the dielectric, and it was shown as an
/// unexplained shortfall of "P_rad + P_sw" with no efficiency.
/// </summary>
public static class PowerLedger
{
    /// <summary>A lossless ledger further than this from 100 % is the model's own error
    /// and is said to be.</summary>
    public const double LosslessTolerance = 0.02;

    /// <param name="inputWatts">Power accepted at the feed.</param>
    /// <param name="radiatedWatts">Space-wave power from the far-field integral.</param>
    /// <param name="surfaceWaveWatts">Power in the extracted surface-wave modes.</param>
    /// <param name="lossTangent">Largest loss tangent of the stackup; 0 = lossless.</param>
    /// <param name="directivity">Peak directivity (linear), for the gain.</param>
    public static string Describe(double inputWatts, double radiatedWatts, double surfaceWaveWatts,
        double lossTangent, double directivity)
    {
        string P(double fraction) => fraction.ToString("P1", CultureInfo.InvariantCulture);
        string G(double value) => value.ToString("g3", CultureInfo.InvariantCulture);

        double efficiency = radiatedWatts / inputWatts;
        double accounted = (radiatedWatts + surfaceWaveWatts) / inputWatts;
        string text = $"surface wave P_sw = {G(surfaceWaveWatts)} W ({P(surfaceWaveWatts / inputWatts)} of input); " +
                      $"radiation efficiency P_rad/P_in = {P(efficiency)}, gain = " +
                      $"{G(10 * Math.Log10(Math.Max(efficiency * directivity, 1e-300)))} dBi";

        if (lossTangent > 0)
        {
            double remainder = 1 - accounted;
            text += remainder >= 0
                ? $"; the remaining {P(remainder)} is dielectric loss (tan δ = {G(lossTangent)}), by " +
                  "difference — it is not integrated, and the surface-wave share on a lossy slab " +
                  "is itself approximate"
                : $"; P_rad + P_sw = {P(accounted)} of input, MORE than was put in, on a lossy " +
                  "substrate — the excess is the model's own error and the dielectric loss cannot " +
                  "be read off this ledger";
        }
        else if (Math.Abs(accounted - 1) > LosslessTolerance)
        {
            text += $"; P_rad + P_sw = {P(accounted)} of input on a LOSSLESS substrate, where it " +
                    $"should be 100 %: the {P(Math.Abs(accounted - 1))} {(accounted > 1 ? "excess" : "shortfall")} " +
                    "is the model's own error (a known open item for probe-fed patches), so the " +
                    "efficiency and gain above carry that much uncertainty";
        }
        else
        {
            text += $"; P_rad + P_sw = {P(accounted)} of input (lossless: the ledger closes)";
        }
        return text;
    }
}
