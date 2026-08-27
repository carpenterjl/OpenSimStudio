using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Stage S9b — the per-observation-height ingredients of the MULTI-LAYER / covered field
/// kernels, the multi-layer analog of <see cref="LayeredFieldKernels"/>. It wraps the TLGF
/// per-z field evaluator (<see cref="TransmissionLineGreens.EvaluateField"/> / <see
/// cref="TransmissionLineGreens.PoleFieldResidues"/>) and supplies the quasi-static images the
/// Sommerfeld remainder subtracts.
///
/// <para><b>The image set is deliberately minimal — and that is exactly correct.</b> The image
/// subtraction only ACCELERATES convergence and lifts the ρ = 0 singularity; it never changes
/// the answer, because the integrand subtracts <c>Σ c·e^{−jk_z0 h}/(jk_z0)</c> and the spatial
/// kernel adds back the identical <c>Σ c·g(√(ρ²+h²))</c> — the Sommerfeld transform of the very
/// same images. So we subtract only the two terms that carry the near-metal singular content —
/// the ε-independent G̃_A pair (grounded half-space: +1 at |z−z_s|, −1 at z+z_s) and the K̃_Φ
/// primary + PEC-ground image (c₀ = 1/ε_above at |z−z_s|, −c₀ at z+z_s) — and let the Sommerfeld
/// remainder carry everything else (the top-interface reflection ladder). For a field map the
/// probe stands a positive height off the metal, so every remaining image height exceeds |z−z_s|
/// and the remainder DECAYS EXPONENTIALLY in k_ρ — no per-z image-ladder derivation is needed,
/// and the BVP-oracle gate on the TOTAL kernel confirms the result.</para>
///
/// <para>Observation is valid on EITHER side of the source plane (above a buried patch, or in
/// the substrate below it) — only the ground plane itself is excluded, where the fields vanish.
/// That generality costs exactly two things, both carried here: the primary image's Coulomb
/// coefficient follows the permittivity at the OBSERVATION point rather than always the medium
/// above the source, and its height FALLS with z below the source, so d(height)/dz travels with
/// the image list instead of being assumed +1 at the point of use.</para>
///
/// <para>W̃ / ∂zW̃ (the A_z coupling) get NO image, exactly as the single-slab field path — their
/// spectral decay is already 1/k_ρ² × exponentials and the tail machinery carries them.</para>
/// </summary>
internal static class MultiLayerFieldKernels
{
    /// <summary>The G̃_A and K̃_Φ image lists for a source at interface <paramref name="m"/>
    /// observed at height <paramref name="z"/>, on EITHER side of the source plane. Two images
    /// each: the primary at |z − z_s| and the PEC-ground image at z + z_s. The returned
    /// <c>DhDz</c> gives d(height)/dz per image — the primary's height FALLS as z rises below
    /// the source (−1) and rises above it (+1), while the ground image's z + z_s always rises;
    /// the ∂z kernels' image subtraction carries that sign, so it must travel with the list
    /// rather than being assumed at the point of use.</summary>
    public static (MultiLayerImages.Image[] Ga, MultiLayerImages.Image[] Phi, double[] DhDz) FieldImages(
        LayeredStackup stackup, int m, double z)
    {
        double zs = stackup.InterfaceHeights()[m];
        double hLow = Math.Abs(z - zs);
        double hHigh = z + zs;
        // The Coulomb medium the primary image is written in. ABOVE the source this is the
        // medium just above it — the shipped rule, kept EXACTLY so every map that has ever been
        // produced stays bit for bit (the image split is a convergence device, never a change of
        // answer, so re-choosing it above the source would move shipped numbers to no measured
        // benefit). BELOW the source the shipped constant is not merely a different choice but
        // the wrong side of the sheet, so there it follows the observation point's own layer.
        // The remaining 2/(ε+1)-type correction rides the remainder either way — it decays
        // because its images sit deeper than |z − z_s|.
        Complex c0 = 1 / (z >= zs
            ? (m == stackup.Layers.Count - 1
                ? Complex.One : stackup.Layers[m + 1].ComplexPermittivity)
            : RegionPermittivity(stackup, z));
        var ga = new[]
        {
            new MultiLayerImages.Image(hLow, Complex.One),
            new MultiLayerImages.Image(hHigh, -Complex.One),
        };
        var phi = new[]
        {
            new MultiLayerImages.Image(hLow, c0),
            new MultiLayerImages.Image(hHigh, -c0),
        };
        return (ga, phi, new[] { z >= zs ? 1.0 : -1.0, 1.0 });
    }

    /// <summary>The permittivity of the region containing <paramref name="z"/>, following
    /// <see cref="TransmissionLineGreens.EvaluateField"/>'s own rule EXACTLY — region 0 (air)
    /// at or above the stack top, else the first layer whose top is ≥ z. Used only BELOW the
    /// source (see <see cref="FieldImages"/>): matching the kernel's own layer rule is what
    /// makes the image cancel the observation-point Coulomb singularity there. Shared with
    /// <see cref="MultiLayerVerticalImages"/>, whose primary-image coefficient is written in
    /// exactly these terms — one implementation of the region rule, never two.</summary>
    internal static Complex RegionPermittivity(LayeredStackup stackup, double z)
    {
        int n = stackup.Layers.Count;
        var heights = stackup.InterfaceHeights();
        if (z >= heights[n - 1]) return Complex.One;
        int i = 0;
        while (i < n - 1 && z > heights[i]) i++;
        return stackup.Layers[i].ComplexPermittivity;
    }

    /// <summary>The six field kernels at (k_ρ, z) — delegates to the TLGF per-z evaluator.</summary>
    public static (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)
        EvaluateAll(LayeredStackup stackup, double k0, Complex kRho, Complex kz0, int m, double z)
        => TransmissionLineGreens.EvaluateField(stackup, k0, kRho, kz0, m, z);

    /// <summary>The per-z residues of the six kernels at a pole — delegates to the TLGF
    /// null-vector residue evaluator profiled at z.</summary>
    public static (Complex A, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)
        PoleResidues(LayeredStackup stackup, double k0, Complex poleKRho, bool isTm, int m, double z)
        => TransmissionLineGreens.PoleFieldResidues(stackup, k0, poleKRho, isTm, m, z);
}
