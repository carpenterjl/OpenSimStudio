using System.Globalization;

namespace OpenSim.Optics.Materials;

/// <summary>
/// A glass's dispersion numbers computed from its own data (never the printed NM values):
/// the indices at the F, d and C lines and, where the valid range reaches them, the g, e, F'
/// and C' lines; the Abbe numbers; the partial dispersion P_g,F and its deviation from the
/// normal line. Plan item OP-14.
/// <list type="bullet">
/// <item>Vd = (nd − 1)/(nF − nC) and Ve = (ne − 1)/(nF' − nC'): the Abbe numbers on the F, d, C
/// and F', e, C' triples (SCHOTT TIE-29, equations 2.1-1 and 2.1-2, as cited in
/// <see cref="SpectralLines"/>), the d-line form as opticalglass computes it
/// (<c>agf_glass.py</c> line 198).</item>
/// <item>P_g,F = (ng − nF)/(nF − nC): the ZemaxGlass user manual (zemaxglass_manual.txt,
/// nomenclature, "P (partial dispersion)").</item>
/// <item>ΔP_g,F = P_g,F − (0.6438 − 0.001682·Vd): the same manual, "dPgF (differential partial
/// dispersion)"; AGFFileReader.jl (<c>src/runtime.jl</c> line 77) uses the same normal line
/// and credits it to SCHOTT TIE-29.</item>
/// </list>
/// The wavelengths are those of <see cref="SpectralLines"/> (TIE-29 Table 2.3-1), at the
/// catalog's reference conditions (<see cref="OpticalMaterial.RelativeIndex"/>).
/// </summary>
public sealed record GlassProperties
{
    /// <summary>The normal line's intercept, 0.6438 (zemaxglass_manual.txt, "dPgF").</summary>
    public const double NormalLineIntercept = 0.6438;

    /// <summary>The normal line's slope per unit of Vd, −0.001682 (zemaxglass_manual.txt, "dPgF").</summary>
    public const double NormalLineSlope = -0.001682;

    public required OpticalMaterial Material { get; init; }
    public required double Nd { get; init; }
    public required double NF { get; init; }
    public required double NC { get; init; }

    /// <summary>Null where the valid range does not reach the line.</summary>
    public double? Ng { get; init; }
    public double? Ne { get; init; }
    public double? NFPrime { get; init; }
    public double? NCPrime { get; init; }

    /// <summary>The principal dispersion nF − nC.</summary>
    public double PrincipalDispersion => NF - NC;

    /// <summary>The Abbe number (nd − 1)/(nF − nC).</summary>
    public double Vd => (Nd - 1) / (NF - NC);

    /// <summary>The Abbe number (ne − 1)/(nF' − nC'), when the range reaches e, F' and C'.</summary>
    public double? Ve => Ne is double e && NFPrime is double f && NCPrime is double c ? (e - 1) / (f - c) : null;

    /// <summary>P_g,F = (ng − nF)/(nF − nC), when the range reaches g.</summary>
    public double? PgF => Ng is double g ? (g - NF) / (NF - NC) : null;

    /// <summary>ΔP_g,F, the deviation of P_g,F from the normal line at this glass's Vd.</summary>
    public double? DeltaPgF => PgF is double p ? p - NormalLinePgF(Vd) : null;

    /// <summary>The normal line: P_g,F = 0.6438 − 0.001682·Vd.</summary>
    public static double NormalLinePgF(double vd) => NormalLineIntercept + NormalLineSlope * vd;

    /// <summary>The numbers of <paramref name="material"/>; throws, naming the glass and the
    /// reason, when they cannot be computed.</summary>
    public static GlassProperties Compute(OpticalMaterial material) =>
        TryCompute(material, out var properties, out string reason) ? properties! : throw new InvalidOperationException(reason);

    /// <summary>
    /// The numbers of <paramref name="material"/>, or false with the reason: no valid range,
    /// a range that does not contain F, d and C, a formula that is not evaluated, an index the
    /// coefficients cannot give, or no dispersion (nF ≤ nC, as in a placeholder entry).
    /// </summary>
    public static bool TryCompute(OpticalMaterial material, out GlassProperties? properties, out string reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        properties = null;
        double d = SpectralLines.LowerD.Micrometers, f = SpectralLines.F.Micrometers, c = SpectralLines.C.Micrometers;
        if (material.ValidRange is not { } range)
        {
            reason = $"{material.Who} states no valid wavelength range.";
            return false;
        }
        if (!material.Covers(d) || !material.Covers(f) || !material.Covers(c))
        {
            reason = $"{material.Who}: its valid range {N(range.Min)}–{N(range.Max)} µm does not contain the F, d and C lines.";
            return false;
        }
        try
        {
            double nd = material.RelativeIndex(d), nF = material.RelativeIndex(f), nC = material.RelativeIndex(c);
            if (!(nF - nC > 0))
            {
                reason = $"{material.Who} has no dispersion (nF − nC = {N(nF - nC)}): a placeholder entry, or data that are not an index.";
                return false;
            }
            properties = new GlassProperties
            {
                Material = material, Nd = nd, NF = nF, NC = nC,
                Ng = Optional(material, SpectralLines.G.Micrometers),
                Ne = Optional(material, SpectralLines.E.Micrometers),
                NFPrime = Optional(material, SpectralLines.FPrime.Micrometers),
                NCPrime = Optional(material, SpectralLines.CPrime.Micrometers),
            };
            reason = "";
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            reason = e.Message.Split(" (Parameter")[0];
            return false;
        }
    }

    private static double? Optional(OpticalMaterial material, double micrometers) =>
        material.Covers(micrometers) ? material.RelativeIndex(micrometers) : null;

    private static string N(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
