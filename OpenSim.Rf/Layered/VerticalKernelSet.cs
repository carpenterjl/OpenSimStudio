using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The full SPATIAL vertical-current kernels for one (frequency, stackup) pair:
///
///   G_A^zz(ρ; z, z′), G_A^xz(ρ; z, z′), K_Φ(ρ; z, z′)
///     = Σ images coeff·e^{−jk₀R_h}/(4πR_h) + Σ poles Res·(−j/4)k_p H₀⁽²⁾(k_pρ)
///       + direct Sommerfeld remainder,
///
/// with G_A^xz the scalar radial potential whose ∇_ρ is the horizontal vector
/// potential of a vertical current (the spectral kernel is normalized per −jk_x, so
/// its transform is the gradient's scalar). Vertical↔vertical interactions occur only
/// at tube scale, where a ρ table cannot amortize — this set evaluates DIRECTLY per
/// (ρ, z, z′) (the plan's coupling-class split; the vertical↔surface tables are built
/// on top by the probe assembly). Poles are found once per set, shared with the
/// boundary-kernel machinery.
/// </summary>
internal sealed class VerticalKernelSet
{
    private readonly SurfaceWavePole[] _poles;

    public VerticalKernelSet(SubstrateStackup substrate, double frequencyHz)
    {
        Substrate = substrate;
        FrequencyHz = frequencyHz;
        K0 = 2 * Math.PI * frequencyHz / RfConstants.SpeedOfLight;
        _poles = SurfaceWavePoles.Find(substrate, K0).ToArray();
    }

    public SubstrateStackup Substrate { get; }
    public double FrequencyHz { get; }
    public double K0 { get; }

    internal IReadOnlyList<SurfaceWavePole> Poles => _poles;

    /// <summary>All three spatial kernels at lateral distance ρ (positive — tube self
    /// terms pass the reduced ρ_eff = √(ρ² + a²)) and heights z, z′ ∈ [0, d].</summary>
    public (Complex GAzz, Complex GAxz, Complex KPhi) Evaluate(
        double rho, double z, double zPrime, int refinement = 1)
    {
        var (rZz, rXz, rPhi) = SommerfeldIntegrator.VerticalRemainder(
            Substrate, K0, _poles, rho, z, zPrime, refinement);

        Complex gAzz = rZz, gAxz = rXz, kPhi = rPhi;
        foreach (var image in VerticalSpatialKernels.Images(Substrate, z, zPrime))
        {
            var g = FreeSpaceG(Math.Sqrt(rho * rho + image.Height * image.Height));
            gAzz += RfConstants.Mu0 * image.CoefficientGAzz * g;
            kPhi += image.CoefficientKPhi * g / RfConstants.Eps0;
        }
        foreach (var pole in _poles)
        {
            var factor = new Complex(0, -0.25) * pole.KRho * Bessel.H02(pole.KRho * rho);
            var (resZz, resXz, resPhi) = VerticalSpatialKernels.PoleResidues(
                Substrate, K0, pole.KRho, z, zPrime);
            gAzz += resZz * factor;
            gAxz += resXz * factor;
            kPhi += resPhi * factor;
        }
        return (gAzz, gAxz, kPhi);
    }

    /// <summary>The SMOOTH parts (pole terms + Sommerfeld remainder) scaled to the raw
    /// e^{−jkR}/R kernel scale the moment machinery integrates against — ×4π/µ₀ for the
    /// A-type kernels, ×4πε₀ for K_Φ (the <c>LayeredKernelSplit</c> precedent). The
    /// closed-form image terms are handled by the geometric moment tracks instead.</summary>
    public (Complex GzzSmooth, Complex GxzSmooth, Complex PhiSmooth) EvaluateSmoothG(
        double rho, double z, double zPrime, int refinement = 1)
    {
        var (rZz, rXz, rPhi) = SommerfeldIntegrator.VerticalRemainder(
            Substrate, K0, _poles, rho, z, zPrime, refinement);
        Complex gzz = rZz, gxz = rXz, kPhi = rPhi;
        foreach (var pole in _poles)
        {
            var factor = new Complex(0, -0.25) * pole.KRho * Bessel.H02(pole.KRho * rho);
            var (resZz, resXz, resPhi) = VerticalSpatialKernels.PoleResidues(
                Substrate, K0, pole.KRho, z, zPrime);
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
