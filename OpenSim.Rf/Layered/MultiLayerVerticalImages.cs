using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Stage C1 (spatial) — the quasi-static images the multi-layer VERTICAL-current Sommerfeld
/// remainder subtracts, the N-layer analog of <see cref="VerticalSpatialKernels.Images"/>.
///
/// <para><b>The set is deliberately minimal, and that costs nothing in correctness.</b> Image
/// subtraction only ACCELERATES convergence and lifts the ρ = 0 singularity — it never changes
/// the answer, because the integrand subtracts <c>Σ c·e^{−jk_z0 h}/(jk_z0)</c> and the spatial
/// kernel adds back the identical <c>Σ c·g(√(ρ²+h²))</c>, the Sommerfeld transform of the very
/// same images (the S9b doctrine, <see cref="MultiLayerFieldKernels.FieldImages"/>). So only the
/// two terms carrying singular content are extracted — the primary at |z − z′| and the PEC-ground
/// image at z + z′ — and the remainder carries the whole reflection ladder, which decays
/// exponentially because every remaining image height is strictly larger.</para>
///
/// <para><b>The coefficients, and how they were fixed.</b> They were MEASURED off the assembled
/// spectral kernels (<see cref="TransmissionLineGreens.EvaluateVertical"/>) in the k_ρ → ∞ limit,
/// not assumed, and the measurement says something simple:</para>
/// <list type="bullet">
///   <item><b>G̃_A^zz images with coefficient 1, ε-independently</b> — measured 1.0000 for every
///     permittivity pair tried, in the same layer AND straddling one interface, and a vertical
///     dipole images POSITIVELY over PEC (+1, not −1 — the sign that separates it from the
///     charge kernel).</item>
///   <item><b>K̃_Φ images with c₀ = 2/(ε(z) + ε(z′))</b> — the classical two-medium transmission
///     factor, measured exact both in one layer (where it collapses to the familiar 1/ε) and
///     across one interface, in both directions and for contrasts up to 9.8:1.</item>
///   <item><b>G̃_A^xz gets no image at all</b> — measured to fall a full 1/k_ρ faster than its
///     siblings (its extracted coefficient decays as 1/k_ρ, i.e. the kernel itself as
///     e^{−k_ρ h}/k_ρ²), so the tail machinery carries it. Same call as the W̃ precedent.</item>
/// </list>
///
/// <para><b>Where the rule is exact, and where it is only an accelerator — stated, because the
/// difference is visible in the measurement.</b> The primary is exact whenever the two heights
/// are separated by AT MOST ONE interface, which is the only regime where it has to be: |z − z′|
/// → 0 forces exactly that, and that is the limit in which the primary carries the singularity.
/// Across two or more interfaces the true coefficients drift (measured: 0.578 rather than 1 for
/// G̃_A^zz across a 4.4 / 2.2 / 9.8 ladder), but there |z − z′| exceeds the whole intervening
/// layer, so the mismatch rides an e^{−k_ρ|z−z′|} remainder and is exponentially damped — the
/// convergence gates measure exactly that. The ground image is exact where IT has to be: z + z′ →
/// 0 forces both heights into the bottom layer, where ε(z) = ε(z′) and the classical ∓1/ε₁ pair
/// is the grounded-half-space answer.</para>
/// </summary>
internal static class MultiLayerVerticalImages
{
    /// <summary>One quasi-static image: coefficient × g_dynamic(√(ρ² + Height²)). Same shape as
    /// <see cref="VerticalSpatialKernels.KernelImage"/>, so the two remainder integrands read
    /// alike.</summary>
    public readonly record struct KernelImage(double Height, Complex CoefficientGAzz, Complex CoefficientKPhi);

    /// <summary>The two-image set for a vertical source at z′ observed at z, both in
    /// [0, total thickness]. At N = 1 this is images 0 and 1 of
    /// <see cref="VerticalSpatialKernels.Images"/> exactly — the identity that gates it.</summary>
    public static KernelImage[] Images(LayeredStackup stackup, double z, double zPrime)
    {
        // The region rule is the KERNEL'S own (MultiLayerFieldKernels.RegionPermittivity, which
        // mirrors TransmissionLineGreens.Profile): region 0 at or above the stack top, else the
        // first layer whose top is ≥ z. Reading the image from the same side the kernel is read
        // from is what makes the subtraction cancel rather than merely resemble — at the top
        // interface the kernel is the air-side limit, and so is this.
        var epsZ = MultiLayerFieldKernels.RegionPermittivity(stackup, z);
        var epsSource = MultiLayerFieldKernels.RegionPermittivity(stackup, zPrime);
        var c0 = 2 / (epsZ + epsSource);
        double dz = Math.Abs(z - zPrime);
        return new[]
        {
            new KernelImage(dz, Complex.One, c0),
            new KernelImage(z + zPrime, Complex.One, -c0),
        };
    }
}
