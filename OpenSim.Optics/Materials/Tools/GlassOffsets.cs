using System.Globalization;

namespace OpenSim.Optics.Materials;

/// <summary>
/// An index and Abbe-number offset held on an <see cref="OpticalMaterial"/>: every index the
/// glass gives is mapped to <see cref="Shift"/> + <see cref="Scale"/>·n. Made only by
/// <see cref="GlassOffsets.Apply"/>, which chooses the scale and shift.
/// </summary>
/// <param name="DeltaNd">The requested change of nd.</param>
/// <param name="DeltaVd">The requested change of Vd.</param>
/// <param name="Scale">(nF − nC) after / (nF − nC) before.</param>
/// <param name="Shift">nd after − Scale·nd before.</param>
public sealed record IndexOffset(double DeltaNd, double DeltaVd, double Scale, double Shift)
{
    /// <summary>The offset index for a catalog index <paramref name="n"/>.</summary>
    public double Apply(double n) => Shift + Scale * n;
}

/// <summary>
/// Index and Abbe offsets on a glass (OpticStudio's glass offset; plan item OP-14). The rule
/// is this program's, not OpticStudio's, whose formula has not been read at a source: the
/// offset glass is the affine map n′(λ) = s + k·n(λ) that gives nd′ = nd + ΔNd and
/// Vd′ = Vd + ΔVd exactly, i.e. k = (nd′ − 1)/(Vd′·(nF − nC)) and s = nd′ − k·nd. An affine map
/// keeps every relative partial dispersion (n(λ₁) − n(λ₂))/(nF − nC), P_g,F among them, so only
/// the two requested numbers move. The map is applied by <see cref="OpticalMaterial.RelativeIndex"/>,
/// so the thermal model (OP-13) sees the offset index as its catalog index.
/// </summary>
public static class GlassOffsets
{
    /// <summary>
    /// <paramref name="material"/> with nd moved by <paramref name="deltaNd"/> and Vd by
    /// <paramref name="deltaVd"/>, measured from the glass's own computed nd and Vd (any offset it
    /// already carries is replaced, not added to). The printed nd and Vd become the offset
    /// values. Throws when the glass's nd and Vd cannot be computed, or when the result would
    /// have nd′ ≤ 1 or Vd′ ≤ 0.
    /// </summary>
    public static OpticalMaterial Apply(OpticalMaterial material, double deltaNd, double deltaVd)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!double.IsFinite(deltaNd) || !double.IsFinite(deltaVd))
            throw new ArgumentException($"{material.Who}: the index and Abbe offsets must be finite numbers.");
        var bare = material with { Offset = null };
        var p = GlassProperties.Compute(bare);
        double nd = p.Nd + deltaNd, vd = p.Vd + deltaVd;
        if (!(nd > 1)) throw new ArgumentOutOfRangeException(nameof(deltaNd), deltaNd, $"{material.Who}: nd {N(p.Nd)} + {N(deltaNd)} is not above 1.");
        if (!(vd > 0)) throw new ArgumentOutOfRangeException(nameof(deltaVd), deltaVd, $"{material.Who}: Vd {N(p.Vd)} + {N(deltaVd)} is not positive.");
        double scale = (nd - 1) / vd / p.PrincipalDispersion;
        double shift = nd - scale * p.Nd;
        return bare with
        {
            Offset = new IndexOffset(deltaNd, deltaVd, scale, shift),
            Nd = new PrintedNumber(nd, nd.ToString("R", CultureInfo.InvariantCulture)),
            Vd = new PrintedNumber(vd, vd.ToString("R", CultureInfo.InvariantCulture)),
        };
    }

    private static string N(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
