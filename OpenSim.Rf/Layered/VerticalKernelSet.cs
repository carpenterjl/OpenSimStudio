using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The single grounded slab's implementation of <see cref="VerticalKernels"/> — the Stage E
/// closed forms. Its five quasi-static images and its analytic pole residues are exactly the
/// pieces <see cref="VerticalSpatialKernels"/> derives; this class is the medium-facing half of
/// the seam, and carries no composition logic of its own (that lives once on the base, so both
/// media compose identically).
///
/// <para>Vertical↔vertical interactions occur only at tube scale, where a ρ table cannot
/// amortize — the base evaluates DIRECTLY per (ρ, z, z′), and the vertical↔surface tables are
/// built on top by the probe assembly. Poles are found once per set and shared with the
/// boundary-kernel machinery.</para>
/// </summary>
internal sealed class VerticalKernelSet : VerticalKernels
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
    public override double FrequencyHz { get; }
    public override double K0 { get; }
    public override double TotalThicknessMeters => Substrate.ThicknessMeters;
    public override double MaxRelativePermittivity => Substrate.RelativePermittivity;
    public override IReadOnlyList<SurfaceWavePole> Poles => _poles;

    public override VerticalKernelImage[] Images(double z, double zPrime) =>
        VerticalSpatialKernels.Images(Substrate, z, zPrime);

    /// <summary>The five-image set as source-segment transforms: reflect at z = 0 (the z + z′
    /// image), reflect at z = d (the critical 2d − z − z′ image, nearly singular near the
    /// junction and regime-dispatched for free), and shift by ∓2d (the 2d ∓ |z − z′| pair —
    /// which of the two each shift produces depends on the sign of z − z′, and they carry the
    /// SAME coefficients, so the set is order-independent). The heights are constant across the
    /// slab, so both arguments are ignored.</summary>
    public override VerticalMomentTracks MomentTracks(double z, double zPrime)
    {
        double d = Substrate.ThicknessMeters;
        var epsC = SpectralKernels.ComplexPermittivity(Substrate);
        var eta = (epsC - 1) / (epsC + 1);
        return new VerticalMomentTracks(1 / epsC, new[]
        {
            new VerticalMomentTrack(zp => -zp, 1, -1 / epsC),
            new VerticalMomentTrack(zp => 2 * d - zp, -eta, eta / epsC),
            new VerticalMomentTrack(zp => zp - 2 * d, -eta, -eta / epsC),
            new VerticalMomentTrack(zp => zp + 2 * d, -eta, -eta / epsC)
        });
    }

    public override (Complex GAzz, Complex GAxz, Complex KPhi) Remainder(
        double rho, double z, double zPrime, int refinement) =>
        SommerfeldIntegrator.VerticalRemainder(Substrate, K0, _poles, rho, z, zPrime, refinement);

    public override (Complex GAzz, Complex GAxz, Complex KPhi) PoleResidues(
        Complex poleKRho, bool isTm, double z, double zPrime) =>
        VerticalSpatialKernels.PoleResidues(Substrate, K0, poleKRho, z, zPrime);
}
