using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Stage C1 (spatial) — the full SPATIAL vertical-current kernels for one (frequency, N-layer
/// stackup) pair, the multi-layer sibling of <see cref="VerticalKernelSet"/>:
///
///   G_A^zz(ρ; z, z′), G_A^xz(ρ; z, z′), K_Φ(ρ; z, z′)
///     = Σ images coeff·e^{−jk₀R_h}/(4πR_h) + Σ poles Res·(−j/4)k_p H₀⁽²⁾(k_pρ)
///       + direct Sommerfeld remainder.
///
/// <para>Same composition, same scaling conventions and the same two read-outs as the single-slab
/// set, so a probe assembly can consume either through one seam — and at N = 1 the two agree,
/// which is the gate. Vertical↔vertical interactions happen at TUBE scale, where a ρ table cannot
/// amortize, so this evaluates DIRECTLY per (ρ, z, z′); it is the tube's own quadrature that
/// amortizes.</para>
///
/// <para>Poles are found once per set from the stackup's own dispersion — their locations are
/// source-independent, and splitting the stack to place the source (which every kernel evaluation
/// does internally) leaves the dispersion unchanged, gated by the split-invariance tests. The
/// per-(z, z′) RESIDUES are computed per evaluation by
/// <see cref="TransmissionLineGreens.PoleVerticalResidues"/>.</para>
/// </summary>
internal sealed class MultiLayerVerticalKernelSet
{
    private readonly SurfaceWavePole[] _poles;

    public MultiLayerVerticalKernelSet(LayeredStackup stackup, double frequencyHz)
    {
        if (frequencyHz <= 0) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        Stackup = stackup;
        FrequencyHz = frequencyHz;
        K0 = 2 * Math.PI * frequencyHz / RfConstants.SpeedOfLight;
        _poles = SurfaceWavePoles.Find(stackup, K0).ToArray();
    }

    public LayeredStackup Stackup { get; }
    public double FrequencyHz { get; }
    public double K0 { get; }

    internal IReadOnlyList<SurfaceWavePole> Poles => _poles;

    /// <summary>All three spatial kernels at lateral distance ρ (positive — tube self terms pass
    /// the reduced ρ_eff = √(ρ² + a²)) and heights z, z′ in [0, total thickness].</summary>
    public (Complex GAzz, Complex GAxz, Complex KPhi) Evaluate(
        double rho, double z, double zPrime, int refinement = 1)
    {
        var (gAzz, gAxz, kPhi) = SommerfeldIntegrator.VerticalRemainderMultiLayer(
            Stackup, K0, _poles, rho, z, zPrime, refinement);

        foreach (var image in MultiLayerVerticalImages.Images(Stackup, z, zPrime))
        {
            var g = FreeSpaceG(Math.Sqrt(rho * rho + image.Height * image.Height));
            gAzz += RfConstants.Mu0 * image.CoefficientGAzz * g;
            kPhi += image.CoefficientKPhi * g / RfConstants.Eps0;
        }
        var (poleZz, poleXz, polePhi) = PoleTerms(rho, z, zPrime);
        return (gAzz + poleZz, gAxz + poleXz, kPhi + polePhi);
    }

    /// <summary>The SMOOTH parts (pole terms + Sommerfeld remainder) scaled to the raw
    /// e^{−jkR}/R kernel scale the moment machinery integrates against — ×4π/µ₀ for the A-type
    /// kernels, ×4πε₀ for K_Φ. The closed-form image terms are handled by the geometric moment
    /// tracks instead (the <c>LayeredKernelSplit</c> precedent, mirroring
    /// <see cref="VerticalKernelSet.EvaluateSmoothG"/> exactly).</summary>
    public (Complex GzzSmooth, Complex GxzSmooth, Complex PhiSmooth) EvaluateSmoothG(
        double rho, double z, double zPrime, int refinement = 1)
    {
        var (gzz, gxz, kPhi) = SommerfeldIntegrator.VerticalRemainderMultiLayer(
            Stackup, K0, _poles, rho, z, zPrime, refinement);
        var (poleZz, poleXz, polePhi) = PoleTerms(rho, z, zPrime);
        double scaleA = 4 * Math.PI / RfConstants.Mu0;
        double scalePhi = 4 * Math.PI * RfConstants.Eps0;
        return (scaleA * (gzz + poleZz), scaleA * (gxz + poleXz), scalePhi * (kPhi + polePhi));
    }

    private (Complex Zz, Complex Xz, Complex Phi) PoleTerms(double rho, double z, double zPrime)
    {
        Complex zz = Complex.Zero, xz = Complex.Zero, phi = Complex.Zero;
        foreach (var pole in _poles)
        {
            var factor = new Complex(0, -0.25) * pole.KRho * Bessel.H02(pole.KRho * rho);
            var (resZz, resXz, resPhi) = TransmissionLineGreens.PoleVerticalResidues(
                Stackup, K0, pole.KRho, pole.IsTm, z, zPrime);
            zz += resZz * factor;
            xz += resXz * factor;
            phi += resPhi * factor;
        }
        return (zz, xz, phi);
    }

    private Complex FreeSpaceG(double r)
    {
        var (sin, cos) = Math.SinCos(K0 * r);
        double scale = 1 / (4 * Math.PI * r);
        return new Complex(scale * cos, -scale * sin);
    }
}
