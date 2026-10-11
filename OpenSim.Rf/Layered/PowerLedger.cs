using System.Globalization;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Where the input power of an antenna on a grounded substrate went, in words. The far
/// field gives the radiated power and the surface-wave poles give what the slab guides
/// away; on a lossy substrate the rest is heat in the dielectric, and it was shown as an
/// unexplained shortfall of "P_rad + P_sw" with no efficiency. On a lossy ground (FU-37) the
/// surface wave decays into the ground, so its power is part of the ground's heat rather than a
/// term of its own.
/// </summary>
public static class PowerLedger
{
    /// <summary>A lossless ledger further than this from 100 % is the model's own error
    /// and is said to be.</summary>
    public const double LosslessTolerance = 0.02;

    /// <param name="inputWatts">Power accepted at the feed.</param>
    /// <param name="radiatedWatts">Space-wave power from the far-field integral.</param>
    /// <param name="surfaceWaveWatts">Power in the extracted surface-wave modes (ignored on a
    /// lossy ground, which absorbs them).</param>
    /// <param name="lossTangent">Largest loss tangent of the stackup; 0 = lossless.</param>
    /// <param name="directivity">Peak directivity (linear), for the gain.</param>
    /// <param name="conductorWatts">Heat in the sheet metal, integrated from the solved
    /// current; 0 for perfect metal. It is counted with the radiated and surface-wave power
    /// before anything is put down to the dielectric or to the model's error.</param>
    /// <param name="lossyGround">The ground plane is an impedance boundary.</param>
    /// <param name="groundWatts">Heat in that ground, integrated over the plane
    /// (<see cref="GroundPlaneLoss"/>); null when it was not integrated, and the ground's share
    /// is then told by difference.</param>
    public static string Describe(double inputWatts, double radiatedWatts, double surfaceWaveWatts,
        double lossTangent, double directivity, double conductorWatts = 0,
        bool lossyGround = false, double? groundWatts = null)
    {
        string P(double fraction) => fraction.ToString("P1", CultureInfo.InvariantCulture);
        string G(double value) => value.ToString("g3", CultureInfo.InvariantCulture);

        double efficiency = radiatedWatts / inputWatts;
        double guided = lossyGround ? 0 : surfaceWaveWatts;
        double ground = groundWatts ?? 0;
        double accounted = (radiatedWatts + guided + conductorWatts + ground) / inputWatts;
        string text = lossyGround
            ? "the surface wave decays into the lossy ground"
            : $"surface wave P_sw = {G(surfaceWaveWatts)} W ({P(surfaceWaveWatts / inputWatts)} of input)";
        text += $"; radiation efficiency P_rad/P_in = {P(efficiency)}, gain = " +
                $"{G(10 * Math.Log10(Math.Max(efficiency * directivity, 1e-300)))} dBi";
        if (conductorWatts > 0)
            text += $"; conductor loss {P(conductorWatts / inputWatts)} of input (integrated over the sheet" +
                    (lossyGround ? ")" : "; the ground plane is taken as perfect)");
        if (lossyGround && groundWatts is not null)
            text += $"; ground-plane loss {P(ground / inputWatts)} of input (integrated over the plane, " +
                    "the surface wave it absorbs included)";

        string counted = lossyGround
            ? groundWatts is null ? "P_rad" : "P_rad + P_ground"
            : "P_rad + P_sw";
        if (conductorWatts > 0) counted += " + heat";
        bool byDifference = lossTangent > 0 || (lossyGround && groundWatts is null);
        if (byDifference)
        {
            string what = lossTangent > 0 && lossyGround && groundWatts is null
                ? $"ground-plane and dielectric loss (tan δ = {G(lossTangent)})"
                : lossTangent > 0 ? $"dielectric loss (tan δ = {G(lossTangent)})"
                : "ground-plane loss, the surface wave it absorbs included";
            double remainder = 1 - accounted;
            text += remainder >= 0
                ? $"; the remaining {P(remainder)} is {what}, by difference — it is not integrated" +
                  (lossyGround ? "" : ", and the surface-wave share on a lossy slab is itself approximate")
                : $"; {counted} = {P(accounted)} of input, MORE than was put in — the excess is the " +
                  "model's own error and the loss cannot be read off this ledger";
        }
        else if (Math.Abs(accounted - 1) > LosslessTolerance)
        {
            text += $"; {counted} = {P(accounted)} of input, where it should be 100 %: the " +
                    $"{P(Math.Abs(accounted - 1))} {(accounted > 1 ? "excess" : "shortfall")} " +
                    "is the model's own error (the solve's or the far-field quadrature's), so the " +
                    "efficiency and gain above carry that much uncertainty";
        }
        else
        {
            text += $"; {counted} = {P(accounted)} of input (" +
                    (lossyGround ? "every watt radiated or absorbed" : "lossless") + ": the ledger closes)";
        }
        return text;
    }
}
