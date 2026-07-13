using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// A general grounded multi-layer stackup for the RF layered-media Green's function:
/// an infinite PEC ground at z = 0, then an ordered list of dielectric layers stacked
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

    /// <summary>The single-slab stackup as a one-layer list — the bridge that keeps the
    /// Stage C/D/E scope a special case of Stage F (and lets the F1 gates compare).</summary>
    public static LayeredStackup FromSubstrate(SubstrateStackup substrate) =>
        new(new[] { new Layer(substrate.RelativePermittivity, substrate.LossTangent,
            substrate.ThicknessMeters) });

    /// <summary>True when this stackup is a single slab — the fast path that dispatches to
    /// the pinned single-slab closed form instead of the general TLGF recursion.</summary>
    public bool IsSingleSlab => Layers.Count == 1;

    /// <summary>The equivalent <see cref="SubstrateStackup"/> for the single-slab case
    /// (throws otherwise) — used by the fast path to reach the pinned closed forms.</summary>
    public SubstrateStackup AsSubstrate()
    {
        if (!IsSingleSlab)
            throw new InvalidOperationException(
                "AsSubstrate is only valid for a single-layer stackup; this stackup has "
                + $"{Layers.Count} layers.");
        var l = Layers[0];
        return new SubstrateStackup(l.RelativePermittivity, l.LossTangent, l.ThicknessMeters);
    }
}
