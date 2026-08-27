using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>One quasi-static image of the vertical-current kernels: the coefficient pair applied
/// to g_dynamic(√(ρ² + <see cref="Height"/>²)). G̃_A^xz carries no image (its spectral weight
/// decays a full order faster than its siblings do — measured, and gated), so there is no third
/// coefficient.</summary>
internal readonly record struct VerticalKernelImage(
    double Height, Complex CoefficientGAzz, Complex CoefficientKPhi);

/// <summary>One image track as the TUBE assembly sees it: not a height, but a map taking the
/// source segment's endpoints to the image segment's, so the image contribution is an ordinary
/// thin-wire pair moment against a transformed source (the layered-image doctrine — the kernel
/// argument moves, the currents do not). The height view of the same image is
/// <see cref="VerticalKernelImage"/>; keeping both is deliberate, because a spatial kernel wants
/// a distance and a moment wants a segment.</summary>
internal readonly record struct VerticalMomentTrack(
    Func<double, double> SourceHeightMap, Complex CoefficientA, Complex CoefficientPhi);

/// <summary>The closed-form image tracks of one element pair: the PRIMARY (which the wire
/// machinery integrates through its own SELF/NEAR/FAR dispatch, so it needs a coefficient rather
/// than a map) plus the transformed images.</summary>
/// <param name="PrimaryCoefficientPhi">K_Φ's primary coefficient. G_A^zz's is 1 in EVERY medium
/// — exactly for one slab, and measured ε-independently for a stackup — so it is not carried:
/// the assembly adds the primary vector moment with no coefficient at all.</param>
/// <param name="Images">The image tracks, primary excluded.</param>
internal readonly record struct VerticalMomentTracks(
    Complex PrimaryCoefficientPhi, IReadOnlyList<VerticalMomentTrack> Images);

/// <summary>
/// The seam every probe consumes its medium through: the SPATIAL vertical-current kernels
///
///   G_A^zz(ρ; z, z′), G_A^xz(ρ; z, z′), K_Φ(ρ; z, z′)
///     = Σ images coeff·e^{−jk₀R_h}/(4πR_h) + Σ poles Res·(−j/4)k_p H₀⁽²⁾(k_pρ)
///       + direct Sommerfeld remainder,
///
/// for one (frequency, medium) pair. Two media implement it — the single grounded slab
/// (<see cref="VerticalKernelSet"/>, Stage E's closed forms) and an N-layer grounded stackup
/// (<see cref="MultiLayerVerticalKernelSet"/>, Stage C1's TLGF) — and the probe assembly is
/// written ONCE against this class.
///
/// <para>That is not tidiness for its own sake. It is what makes the N = 1 identity gate a
/// statement about the KERNELS rather than about two hand-aligned assemblies: the same
/// quadrature, the same moment bookkeeping and the same matrix consume both, so a disagreement
/// can only come from the medium. The composed <see cref="Evaluate"/> and
/// <see cref="EvaluateSmoothG"/> live here for the same reason — the composition rule (remainder,
/// then images, then poles, in that association) is a property of the decomposition, not of the
/// medium.</para>
///
/// <para>Both media are read at HEIGHTS, never at interfaces-by-index, and both answer at any
/// z, z′ in [0, total thickness]. A source exactly ON a material discontinuity is a typed failure
/// in the multi-layer implementation (−2µ₀/ε is two-valued there); a probe never asks, because
/// every internal interface is forced to be a tube NODE and quadrature points are strictly inside
/// an element.</para>
/// </summary>
internal abstract class VerticalKernels
{
    /// <summary>Frequency of this set, Hz.</summary>
    public abstract double FrequencyHz { get; }

    /// <summary>Free-space wavenumber.</summary>
    public abstract double K0 { get; }

    /// <summary>Ground-to-top dielectric height — the upper bound on both kernel heights.</summary>
    public abstract double TotalThicknessMeters { get; }

    /// <summary>The largest εr in the medium: what sets the in-dielectric wavelength, and so the
    /// near/far crossover of any table built on this set.</summary>
    public abstract double MaxRelativePermittivity { get; }

    /// <summary>The surface-wave poles, found once per set (their locations are
    /// source-independent).</summary>
    public abstract IReadOnlyList<SurfaceWavePole> Poles { get; }

    /// <summary>The quasi-static images extracted from the Sommerfeld integrand and added back in
    /// closed form. Subtracting an image never changes the answer — it only accelerates
    /// convergence and lifts the ρ = 0 singularity — so the two media legitimately extract
    /// different NUMBERS of images and still agree.</summary>
    public abstract VerticalKernelImage[] Images(double z, double zPrime);

    /// <summary>The same images as SEGMENT transforms, for the tube's moment assembly. The two
    /// heights identify which materials the pair sits in; pass element MIDPOINTS, which are
    /// unambiguous because no element straddles an interface.</summary>
    public abstract VerticalMomentTracks MomentTracks(double z, double zPrime);

    /// <summary>The direct Sommerfeld remainder — the kernel minus its images and pole terms.</summary>
    public abstract (Complex GAzz, Complex GAxz, Complex KPhi) Remainder(
        double rho, double z, double zPrime, int refinement);

    /// <summary>The residues of the three kernels at one surface-wave pole, per (z, z′). The
    /// pole is passed as (location, polarization) rather than as a
    /// <see cref="SurfaceWavePole"/>, because the surface-wave POWER integral evaluates the
    /// residues at the pole's REAL part — the launched mode — while the record carries the full
    /// complex location a lossy stack puts it at.</summary>
    public abstract (Complex GAzz, Complex GAxz, Complex KPhi) PoleResidues(
        Complex poleKRho, bool isTm, double z, double zPrime);

    /// <summary>All three spatial kernels at lateral distance ρ (positive — tube self terms pass
    /// the reduced ρ_eff = √(ρ² + a²)) and heights z, z′ in
    /// [0, <see cref="TotalThicknessMeters"/>].</summary>
    public (Complex GAzz, Complex GAxz, Complex KPhi) Evaluate(
        double rho, double z, double zPrime, int refinement = 1)
    {
        var (rZz, rXz, rPhi) = Remainder(rho, z, zPrime, refinement);

        Complex gAzz = rZz, gAxz = rXz, kPhi = rPhi;
        foreach (var image in Images(z, zPrime))
        {
            var g = FreeSpaceG(Math.Sqrt(rho * rho + image.Height * image.Height));
            gAzz += RfConstants.Mu0 * image.CoefficientGAzz * g;
            kPhi += image.CoefficientKPhi * g / RfConstants.Eps0;
        }
        foreach (var pole in Poles)
        {
            var factor = new Complex(0, -0.25) * pole.KRho * Bessel.H02(pole.KRho * rho);
            var (resZz, resXz, resPhi) = PoleResidues(pole.KRho, pole.IsTm, z, zPrime);
            gAzz += resZz * factor;
            gAxz += resXz * factor;
            kPhi += resPhi * factor;
        }
        return (gAzz, gAxz, kPhi);
    }

    /// <summary>The SMOOTH parts (pole terms + Sommerfeld remainder) scaled to the raw
    /// e^{−jkR}/R kernel scale the moment machinery integrates against — ×4π/µ₀ for the A-type
    /// kernels, ×4πε₀ for K_Φ (the <c>LayeredKernelSplit</c> precedent). The closed-form image
    /// terms are handled by the geometric moment tracks instead.</summary>
    public (Complex GzzSmooth, Complex GxzSmooth, Complex PhiSmooth) EvaluateSmoothG(
        double rho, double z, double zPrime, int refinement = 1)
    {
        var (rZz, rXz, rPhi) = Remainder(rho, z, zPrime, refinement);
        Complex gzz = rZz, gxz = rXz, kPhi = rPhi;
        foreach (var pole in Poles)
        {
            var factor = new Complex(0, -0.25) * pole.KRho * Bessel.H02(pole.KRho * rho);
            var (resZz, resXz, resPhi) = PoleResidues(pole.KRho, pole.IsTm, z, zPrime);
            gzz += resZz * factor;
            gxz += resXz * factor;
            kPhi += resPhi * factor;
        }
        double scaleA = 4 * Math.PI / RfConstants.Mu0;
        double scalePhi = 4 * Math.PI * RfConstants.Eps0;
        return (scaleA * gzz, scaleA * gxz, scalePhi * kPhi);
    }

    private Complex FreeSpaceG(double r)
    {
        var (sin, cos) = Math.SinCos(K0 * r);
        double scale = 1 / (4 * Math.PI * r);
        return new Complex(scale * cos, -scale * sin);
    }
}
