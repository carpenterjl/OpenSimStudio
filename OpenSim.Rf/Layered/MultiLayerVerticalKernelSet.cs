using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Stage C1 (spatial) — the N-layer grounded stackup's implementation of
/// <see cref="VerticalKernels"/>, the multi-layer sibling of <see cref="VerticalKernelSet"/>.
///
/// <para>Same composition, same scaling conventions and the same two read-outs as the single-slab
/// set — because both are the BASE class's, written once — so a probe assembly consumes either
/// through one seam, and at N = 1 the two agree, which is the gate.</para>
///
/// <para>Poles are found once per set from the stackup's own dispersion — their locations are
/// source-independent, and splitting the stack to place the source (which every kernel evaluation
/// does internally) leaves the dispersion unchanged, gated by the split-invariance tests. The
/// per-(z, z′) RESIDUES are computed per evaluation by
/// <see cref="TransmissionLineGreens.PoleVerticalResidues"/>.</para>
/// </summary>
internal sealed class MultiLayerVerticalKernelSet : VerticalKernels
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
    public override double FrequencyHz { get; }
    public override double K0 { get; }
    public override double TotalThicknessMeters => Stackup.TotalThicknessMeters;
    public override double MaxRelativePermittivity => Stackup.Layers.Max(l => l.RelativePermittivity);
    public override IReadOnlyList<SurfaceWavePole> Poles => _poles;

    public override VerticalKernelImage[] Images(double z, double zPrime) =>
        MultiLayerVerticalImages.Images(Stackup, z, zPrime);

    /// <summary>The two-image set as source-segment transforms: the primary (the wire machinery's
    /// own dispatch) with K_Φ coefficient c₀ = 2/(ε(z) + ε(z′)), and its PEC-ground reflection at
    /// z + z′ with −c₀. Unlike the single slab's, c₀ depends on WHICH LAYERS the pair sits in —
    /// which is exactly why the tube is forced to node at every interface, so an element pair has
    /// one material each and the coefficient is a constant of the pair rather than a function of
    /// the quadrature point.</summary>
    public override VerticalMomentTracks MomentTracks(double z, double zPrime)
    {
        var epsZ = MultiLayerFieldKernels.RegionPermittivity(Stackup, z);
        var epsSource = MultiLayerFieldKernels.RegionPermittivity(Stackup, zPrime);
        var c0 = 2 / (epsZ + epsSource);
        return new VerticalMomentTracks(c0, new[]
        {
            new VerticalMomentTrack(zp => -zp, 1, -c0)
        });
    }

    public override (Complex GAzz, Complex GAxz, Complex KPhi) Remainder(
        double rho, double z, double zPrime, int refinement) =>
        SommerfeldIntegrator.VerticalRemainderMultiLayer(
            Stackup, K0, _poles, rho, z, zPrime, refinement);

    public override (Complex GAzz, Complex GAxz, Complex KPhi) PoleResidues(
        Complex poleKRho, bool isTm, double z, double zPrime) =>
        TransmissionLineGreens.PoleVerticalResidues(Stackup, K0, poleKRho, isTm, z, zPrime);
}
