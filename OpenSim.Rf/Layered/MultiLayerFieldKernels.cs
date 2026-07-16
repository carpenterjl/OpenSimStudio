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
/// and the BVP-oracle gate on the TOTAL kernel confirms the result. Valid for observation at or
/// above the source height z ≥ z_s (the map region above the metal); dh/dz = +1 for both images.</para>
///
/// <para>W̃ / ∂zW̃ (the A_z coupling) get NO image, exactly as the single-slab field path — their
/// spectral decay is already 1/k_ρ² × exponentials and the tail machinery carries them.</para>
/// </summary>
internal static class MultiLayerFieldKernels
{
    /// <summary>The G̃_A and K̃_Φ image lists for a source at interface <paramref name="m"/>
    /// observed at height <paramref name="z"/> (z ≥ source height). Two images each: the primary
    /// at |z − z_s| and the PEC-ground image at z + z_s.</summary>
    public static (MultiLayerImages.Image[] Ga, MultiLayerImages.Image[] Phi) FieldImages(
        LayeredStackup stackup, int m, double z)
    {
        double zs = stackup.InterfaceHeights()[m];
        double hLow = Math.Abs(z - zs);
        double hHigh = z + zs;
        // The Coulomb medium above the source: the cover for a buried patch (c₀ = 1/ε_cover),
        // air for a coplanar-top source (c₀ = 1). The remaining 2/(ε+1)-type correction rides
        // the remainder — it decays because its images sit deeper than |z − z_s|.
        Complex epsAbove = m == stackup.Layers.Count - 1
            ? Complex.One : stackup.Layers[m + 1].ComplexPermittivity;
        Complex c0 = 1 / epsAbove;
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
        return (ga, phi);
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
