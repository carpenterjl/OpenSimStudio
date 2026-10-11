using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// A general grounded multi-layer stackup for the RF layered-media Green's function:
/// an infinite ground at z = 0 (PEC, or an impedance boundary of finite conductivity —
/// <see cref="GroundConductivitySiemensPerMeter"/>), then an ordered list of dielectric layers stacked
/// upward (index 0 sits on the ground), free space above the top layer. The metal /
/// source plane sits at the TOP of the stack, z = <see cref="TotalThicknessMeters"/>
/// — the same "all metal coplanar at the slab top" contract as <see cref="SubstrateStackup"/>,
/// now with N slabs beneath it instead of one.
///
/// Stage F generalizes the single-slab closed-form kernels (<see cref="SpectralKernels"/>)
/// to this list through a transmission-line Green's function (<see cref="TransmissionLineGreens"/>).
/// The N = 1 case is bit-for-bit the old physics: <see cref="SubstrateStackup"/> maps to a
/// one-element list, and the F1 gates pin the TLGF against the single-slab closed form and
/// an independent N-layer boundary-value solve.
/// </summary>
public sealed record LayeredStackup
{
    /// <summary>One dielectric layer: εr (≥ 1), tanδ (≥ 0), thickness in meters (&gt; 0).
    /// εc = εr(1 − j·tanδ) is the e^{+jωt} lossy convention (<see cref="SpectralKernels"/>).</summary>
    public sealed record Layer
    {
        public double RelativePermittivity { get; }
        public double LossTangent { get; }
        public double ThicknessMeters { get; }

        public Layer(double relativePermittivity, double lossTangent, double thicknessMeters)
        {
            if (relativePermittivity < 1)
                throw new ArgumentOutOfRangeException(nameof(relativePermittivity),
                    "A layer εr must be ≥ 1 — a value below vacuum is not a physical dielectric.");
            if (lossTangent < 0)
                throw new ArgumentOutOfRangeException(nameof(lossTangent),
                    "A layer loss tangent must be ≥ 0.");
            if (thicknessMeters <= 0)
                throw new ArgumentOutOfRangeException(nameof(thicknessMeters),
                    "A layer thickness must be positive.");
            RelativePermittivity = relativePermittivity;
            LossTangent = lossTangent;
            ThicknessMeters = thicknessMeters;
        }

        public Complex ComplexPermittivity => RelativePermittivity * new Complex(1, -LossTangent);
    }

    /// <summary>The dielectric layers, ground-up: <c>Layers[0]</c> rests on the PEC,
    /// <c>Layers[^1]</c>'s top carries the metal.</summary>
    public IReadOnlyList<Layer> Layers { get; }

    public LayeredStackup(IReadOnlyList<Layer> layers)
    {
        if (layers is null || layers.Count == 0)
            throw new ArgumentException("A stackup needs at least one dielectric layer.", nameof(layers));
        Layers = layers.ToArray();
    }

    /// <summary>Conductivity of the ground plane [S/m]; +∞ (the default) is the PEC ground. A finite
    /// value makes the ground an impedance (Leontovich) boundary, E_t = Z_s·n̂ × H with
    /// <see cref="GroundSurfaceImpedance"/>: it absorbs, the surface waves it guides decay, and
    /// their poles leave the real axis (FU-37).</summary>
    public double GroundConductivitySiemensPerMeter
    {
        get => _groundConductivity;
        init => _groundConductivity = value > 0 ? value
            : throw new ArgumentOutOfRangeException(nameof(GroundConductivitySiemensPerMeter),
                "The ground conductivity must be positive (+∞ is the perfect ground).");
    }
    private readonly double _groundConductivity = double.PositiveInfinity;

    /// <summary>Thickness of the ground copper [m]; +∞ (the default) is a ground many skin depths
    /// thick. A finite value carries the current through the thickness at low frequency (the DC
    /// sheet resistance) with no field behind the plane.</summary>
    public double GroundThicknessMeters
    {
        get => _groundThickness;
        init => _groundThickness = value > 0 ? value
            : throw new ArgumentOutOfRangeException(nameof(GroundThicknessMeters),
                "The ground thickness must be positive (+∞ is a thick ground).");
    }
    private readonly double _groundThickness = double.PositiveInfinity;

    /// <summary>The factor the loss continuation scales the ground's surface impedance by, as it
    /// scales every loss tangent (<see cref="SurfaceWaveDispersion.WithLossFraction"/>); 1 is the
    /// physical ground.</summary>
    internal double GroundLossFraction { get; init; } = 1;

    /// <summary>True when the ground is an impedance boundary rather than PEC.</summary>
    public bool HasLossyGround => double.IsFinite(GroundConductivitySiemensPerMeter) && GroundLossFraction > 0;

    /// <summary>The ground's surface impedance at angular frequency <paramref name="omega"/> [Ω]:
    /// (k/σ)·coth(k·t) with k = (1 + j)/δ — (1 + j)/(σδ) for a thick ground, 1/(σt) at low
    /// frequency for a thin one (the field behind the plane is zero, as under an infinite ground).
    /// Zero for the PEC ground.</summary>
    public Complex GroundSurfaceImpedance(double omega)
    {
        if (!HasLossyGround) return Complex.Zero;
        double sigma = GroundConductivitySiemensPerMeter;
        Complex k = Complex.Sqrt(new Complex(0, omega * RfConstants.Mu0 * sigma));
        Complex coth;
        if (double.IsPositiveInfinity(GroundThicknessMeters)) coth = Complex.One;
        else
        {
            Complex kt = k * GroundThicknessMeters;
            coth = kt.Magnitude < 1e-3 ? 1 / kt + kt / 3 : 1 / Complex.Tanh(kt);
        }
        return GroundLossFraction * k / sigma * coth;
    }

    /// <summary>The same ground under another list of layers (a split, a loss fraction) — every
    /// stackup derived from this one carries its ground.</summary>
    internal LayeredStackup WithLayers(IReadOnlyList<Layer> layers) => new(layers)
    {
        GroundConductivitySiemensPerMeter = GroundConductivitySiemensPerMeter,
        GroundThicknessMeters = GroundThicknessMeters,
        GroundLossFraction = GroundLossFraction,
    };

    /// <summary>Total dielectric height — the z of the top (metal) plane above the ground.</summary>
    public double TotalThicknessMeters => Layers.Sum(l => l.ThicknessMeters);

    /// <summary>The cumulative interface heights measured from the ground, one per layer
    /// TOP: <c>InterfaceHeights[i]</c> is the top of layer i (so <c>[^1]</c> is the metal
    /// plane). The ground itself (z = 0) is implicit.</summary>
    public double[] InterfaceHeights()
    {
        var heights = new double[Layers.Count];
        double z = 0;
        for (int i = 0; i < Layers.Count; i++)
        {
            z += Layers[i].ThicknessMeters;
            heights[i] = z;
        }
        return heights;
    }

    /// <summary>Split the stack at height <paramref name="z"/> so that height becomes an
    /// INTERFACE of the returned stackup, and report which interface index it is. Both halves
    /// carry the SAME material as the layer they came from, so the result describes the identical
    /// physical structure.
    ///
    /// <para>This is how a source goes to an arbitrary height. The transmission-line machinery
    /// only ever places sources AT interfaces — that is what its jump conditions are written
    /// against — so rather than deriving a second, interior-to-a-layer source formulation, the
    /// stack is split and the existing interface source is used. The house already gates that
    /// this changes nothing: split-slab invariance is an F1 gate, and it is exactly the statement
    /// that a layer cut in half is the same stack.</para>
    ///
    /// <para>A z already ON an interface returns that interface and THIS stackup unchanged, never
    /// a zero-thickness layer — whose reduced phase would be exactly 1, leaving its two amplitudes
    /// degenerate and the per-k_ρ system singular. "Already on" is judged at 1e-12 of the total
    /// stack height: below that the two descriptions differ by less than the reduced-basis
    /// arithmetic can resolve, so snapping is a rounding decision rather than a physical one, and
    /// it is stated here rather than hidden in a caller.</para></summary>
    public (LayeredStackup Stackup, int Interface) SplitAt(double z)
    {
        double total = TotalThicknessMeters;
        if (double.IsNaN(z) || z <= 0 || z > total)
            throw new ArgumentOutOfRangeException(nameof(z),
                $"A split height must lie in (0, {total}] m — got {z} m. z = 0 is the PEC ground, "
                + "which bounds the stack rather than dividing it.");

        var heights = InterfaceHeights();
        double tolerance = 1e-12 * total;
        for (int i = 0; i < heights.Length; i++)
            if (Math.Abs(z - heights[i]) <= tolerance) return (this, i);

        int host = 0;
        while (host < Layers.Count - 1 && z > heights[host]) host++;
        double below = host == 0 ? 0 : heights[host - 1];
        var material = Layers[host];
        var split = new List<Layer>(Layers.Count + 1);
        for (int i = 0; i < host; i++) split.Add(Layers[i]);
        split.Add(new Layer(material.RelativePermittivity, material.LossTangent, z - below));
        split.Add(new Layer(material.RelativePermittivity, material.LossTangent, heights[host] - z));
        for (int i = host + 1; i < Layers.Count; i++) split.Add(Layers[i]);
        return (WithLayers(split), host);
    }

    /// <summary>The single-slab stackup as a one-layer list — the bridge that keeps the
    /// Stage C/D/E scope a special case of Stage F (and lets the F1 gates compare).</summary>
    public static LayeredStackup FromSubstrate(SubstrateStackup substrate) =>
        new(new[] { new Layer(substrate.RelativePermittivity, substrate.LossTangent,
            substrate.ThicknessMeters) });

    /// <summary>The interface index of the metal plane in a <see cref="CoveredPatch"/> stackup:
    /// the top of the (single) substrate layer, index 0. Pass this as the
    /// <c>sourceInterface</c> of a <see cref="MultiLayerKernelTable"/> to place source AND
    /// observation at the buried metal.</summary>
    public const int CoveredPatchMetalInterface = 0;

    /// <summary>A covered patch: metal buried between a substrate slab and a dielectric COVER
    /// of the SAME εr/tanδ (a homogeneous slab split at the metal). A cover pulls the resonance
    /// DOWN, growing with cover thickness. The metal sits at interface
    /// <see cref="CoveredPatchMetalInterface"/> = 0. Equivalent to the two-material overload
    /// with the cover material equal to the substrate's.</summary>
    public static LayeredStackup CoveredPatch(double epsR, double tanD, double hSub, double hCover) =>
        CoveredPatch(epsR, tanD, hSub, epsR, tanD, hCover);

    /// <summary>A covered patch whose SUPERSTRATE is a different material from the substrate
    /// (εr₂ ≠ εr₁ co-located with the current sheet) — the general form. This was once
    /// restricted to a matched cover on the suspicion that a jump in ε made the interior-source
    /// read-out two-valued at the metal. It does not: the TM contrast source at the sheet
    /// cancels the 1/ε difference identically, so K̃_Φ is single-valued (the derivation is on
    /// <see cref="TransmissionLineGreens.EvaluateInterior"/>, measured by
    /// InteriorSourceKernelTests.ReadOutIsSingleValuedAcrossAnEpsilonJump). Nothing downstream
    /// needed changing — the TLGF, the interior images, the pole residues and the far-field
    /// amplitudes were already εr-general.</summary>
    /// <param name="epsRSubstrate">Substrate relative permittivity (the layer on the ground).</param>
    /// <param name="tanDSubstrate">Substrate loss tangent.</param>
    /// <param name="hSub">Substrate thickness, metres (ground to metal).</param>
    /// <param name="epsRCover">Superstrate relative permittivity (1 = an uncovered patch).</param>
    /// <param name="tanDCover">Superstrate loss tangent.</param>
    /// <param name="hCover">Superstrate thickness, metres (metal to open air).</param>
    public static LayeredStackup CoveredPatch(
        double epsRSubstrate, double tanDSubstrate, double hSub,
        double epsRCover, double tanDCover, double hCover) =>
        new(new[]
        {
            new Layer(epsRSubstrate, tanDSubstrate, hSub),
            new Layer(epsRCover, tanDCover, hCover)
        });

    /// <summary>True when this stackup is a single slab — the fast path that dispatches to
    /// the pinned single-slab closed form instead of the general TLGF recursion. A lossy ground is
    /// not a single slab in this sense: the closed forms are PEC-backed.</summary>
    public bool IsSingleSlab => Layers.Count == 1 && !HasLossyGround;

    /// <summary>The equivalent <see cref="SubstrateStackup"/> for the single-slab case
    /// (throws otherwise) — used by the fast path to reach the pinned closed forms.</summary>
    public SubstrateStackup AsSubstrate()
    {
        if (!IsSingleSlab)
            throw new InvalidOperationException(
                "AsSubstrate is only valid for a single-layer stackup on a perfect ground; this stackup has "
                + $"{Layers.Count} layers" + (HasLossyGround ? " and a lossy ground." : "."));
        var l = Layers[0];
        return new SubstrateStackup(l.RelativePermittivity, l.LossTangent, l.ThicknessMeters);
    }
}
