using System.Numerics;

namespace OpenSim.Rf.Network;

/// <summary>
/// The band around a match where the reflection stays under a limit (−10 dB by default), read
/// from a continuous response — the rational interpolant of an adaptive sweep — rather than by
/// straight lines between solved points: each edge is bracketed on a fine scan outward from
/// the centre, then bisected.
/// </summary>
public static class MatchBand
{
    /// <summary>The band edges [Hz], or null when the centre is not under the limit. An edge
    /// that is not reached inside [<paramref name="fromHz"/>, <paramref name="toHz"/>] is NaN.</summary>
    public static (double Low, double High)? Find(Func<double, Complex> inputImpedance, double referenceOhms,
        double fromHz, double toHz, double centerHz, double limitDb = -10, int scan = 2000)
    {
        if (!(referenceOhms > 0)) throw new ArgumentOutOfRangeException(nameof(referenceOhms));
        if (!(fromHz < toHz) || centerHz < fromHz || centerHz > toHz)
            throw new ArgumentException("The centre must lie in the band.");
        double limit = Math.Pow(10, limitDb / 20);
        double Reflection(double f)
        {
            var z = inputImpedance(f);
            return ((z - referenceOhms) / (z + referenceOhms)).Magnitude;
        }
        if (!(Reflection(centerHz) < limit)) return null;

        double step = (toHz - fromHz) / scan;
        double Edge(int direction)
        {
            double inside = centerHz;
            while (true)
            {
                double next = inside + direction * step;
                if (next < fromHz || next > toHz)
                {
                    double end = direction < 0 ? fromHz : toHz;
                    if (Reflection(end) < limit) return double.NaN;
                    next = end;
                }
                if (Reflection(next) >= limit)
                {
                    double a = inside, b = next;          // under the limit at a, at or over it at b
                    for (int i = 0; i < 200 && Math.Abs(b - a) > 1e-13 * Math.Abs(b); i++)
                    {
                        double middle = 0.5 * (a + b);
                        if (Reflection(middle) < limit) a = middle; else b = middle;
                    }
                    return 0.5 * (a + b);
                }
                inside = next;
            }
        }
        return (Edge(-1), Edge(+1));
    }

    /// <summary>The same on an adaptive sweep's interpolant of the input impedance (entry 0).</summary>
    public static (double Low, double High)? Find(RationalSweepResult sweep, double referenceOhms,
        double centerHz, double limitDb = -10) =>
        Find(f => sweep.At(f)[0], referenceOhms, sweep.FrequenciesHz[0], sweep.FrequenciesHz[^1], centerHz, limitDb);
}
