using System.Runtime.CompilerServices;

namespace OpenSim.Optics.Materials;

/// <summary>A glass's place on the glass map: nd, Vd and ΔP_g,F (null where unknown), and
/// whether the numbers are the catalog's printed ones or computed from its dispersion data.</summary>
public sealed record GlassPoint(OpticalMaterial Material, double Nd, double Vd, double? DeltaPgF, bool Computed);

/// <summary>
/// The "distance" between two glasses on the glass map (plan item OP-59), OpticStudio's RGLA
/// distance (OpticStudio 2025 R2 help, "Optimizing Using Model Glasses", equation image saved as
/// <c>plans/sources/optics/op-59-help/images/Optimizing Using Model Glasses.png</c>):
/// d = [W_n(Nd₁ − Nd₂)² + W_a(Vd₁ − Vd₂)² + W_p(ΔP_g,F1 − ΔP_g,F2)²]^½, the weights defaulting to
/// 1.0, 1E-04 and 1E+02 when left at zero (same page). The model-to-real conversion uses the same
/// criterion with the partial-dispersion term omitted ("a least-squares criterion similar to the
/// RGLA definition above (the partial dispersion term is omitted)", same page); its weights are
/// not stated there, and this program uses the RGLA defaults.
/// <para>
/// <b>Where a glass sits.</b> A catalog glass's printed nd, Vd and ΔP_g,F (the NM and ED lines; a
/// model glass carries its own as printed values), as the catalog dialog displays them; where the
/// catalog prints none (a refractiveindex.info material, a Vd printed as 0) they are computed
/// from the dispersion data (<see cref="GlassProperties"/>), and ΔP_g,F stays unknown when the
/// valid range does not reach the g line. Which of the two OpticStudio uses is not stated in the
/// help; printed first is this program's reading.
/// </para>
/// </summary>
public static class GlassDistance
{
    /// <summary>W_n's default, 1.0 ("Optimizing Using Model Glasses").</summary>
    public const double DefaultIndexWeight = 1.0;

    /// <summary>W_a's default, 1E-04 (same page).</summary>
    public const double DefaultAbbeWeight = 1e-4;

    /// <summary>W_p's default, 1E+02 (same page).</summary>
    public const double DefaultPartialDispersionWeight = 1e2;

    private static readonly ConditionalWeakTable<OpticalMaterial, Holder> Cache = new();
    private sealed record Holder(GlassPoint? Point, string Reason);

    /// <summary>The point of <paramref name="material"/>, or false with the reason (no printed
    /// nd and Vd and no data to compute them). Cached per material instance.</summary>
    public static bool TryPoint(OpticalMaterial material, out GlassPoint? point, out string reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        var holder = Cache.GetValue(material, Compute);
        point = holder.Point;
        reason = holder.Reason;
        return point is not null;
    }

    private static Holder Compute(OpticalMaterial m)
    {
        double? nd = m.Nd is { } pn && pn.Value > 1 ? pn.Value : null;
        double? vd = m.Vd is { } pv && pv.Value > 0 ? pv.Value : null;
        double? dp = m.DeltaPgF;
        if (nd is double n && vd is double v && dp is not null)
            return new Holder(new GlassPoint(m, n, v, dp, Computed: false), "");
        if (!GlassProperties.TryCompute(m, out var p, out string why))
        {
            if (nd is double n2 && vd is double v2) return new Holder(new GlassPoint(m, n2, v2, dp, Computed: false), "");
            return new Holder(null, why);
        }
        bool computed = nd is null || vd is null;
        return new Holder(new GlassPoint(m, nd ?? p!.Nd, vd ?? p!.Vd, dp ?? p!.DeltaPgF, computed || dp is null), "");
    }

    /// <summary>The weights with zero replaced by the defaults ("if left at zero, will default").</summary>
    public static (double Wn, double Wa, double Wp) Weights(double wn, double wa, double wp) =>
        (wn == 0 ? DefaultIndexWeight : wn, wa == 0 ? DefaultAbbeWeight : wa, wp == 0 ? DefaultPartialDispersionWeight : wp);

    /// <summary>The distance with the given weights (used as given: apply <see cref="Weights"/>
    /// first for RGLA's zero-means-default rule). A ΔP_g,F term with a positive weight needs both
    /// partial dispersions; null when one is unknown.</summary>
    public static double? Distance(GlassPoint a, GlassPoint b, double wn, double wa, double wp)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        double dn = a.Nd - b.Nd, dv = a.Vd - b.Vd, sum = wn * dn * dn + wa * dv * dv;
        if (wp != 0)
        {
            if (a.DeltaPgF is not double pa || b.DeltaPgF is not double pb) return null;
            double dp = pa - pb;
            sum += wp * dp * dp;
        }
        return Math.Sqrt(sum);
    }
}
