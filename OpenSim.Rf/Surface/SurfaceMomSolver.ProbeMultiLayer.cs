using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Surface;

/// <summary>
/// Stage C2 — the probe-fed solve over an N-layer grounded stackup: a coaxial probe through a
/// multi-gap board, or up to a patch buried under a superstrate.
///
/// <para>There is no second assembly here, and that is the whole design. The extended system,
/// the junction mode, the by-parts coupling and the matrix bookkeeping are
/// <see cref="SolveProbeFedCore"/>'s, written once; this file only chooses the medium — the RWG
/// pair moments through <c>LayeredKernelSplit</c>'s multi-layer image list, the junction disc
/// through <see cref="MultiLayerRadialGaKernel"/>, and the tube through
/// <see cref="MultiLayerVerticalKernelSet"/>. So the N = 1 identity gate (a one-layer stackup
/// reproducing the single-slab probe) measures the KERNELS: everything else is literally the
/// same code, executing the same operations in the same order.</para>
///
/// <para>Two things the multi-layer case genuinely adds. The tube ends on the metal plane, which
/// for a covered patch is an INTERIOR interface rather than the top of the stack — so the tube's
/// span and the coupling tables' observation height come from the table's own
/// <see cref="MultiLayerKernelTable.SourceInterface"/>, not from the stack height. And every
/// internal interface below the metal is forced to be a tube NODE
/// (<see cref="ProbeAssembly.TubeNodes(LayeredStackup, int?, ProbeFeed)"/>): the vertical source
/// strength is −2µ₀/ε, which is two-valued exactly on a material discontinuity, and nodding
/// there makes the ambiguous case structurally unreachable rather than merely unlikely.</para>
/// </summary>
public sealed partial class SurfaceMomSolver
{
    /// <summary>Kernel facts every consumer must surface next to multi-layer probe-fed results.</summary>
    public static IReadOnlyList<string> MultiLayerProbeFedAssumptions { get; } = new[]
    {
        "Zero-thickness sheet and probe tube; perfect conductors unless a sheet or wire surface impedance is given, which adds their ohmic loss (the ground plane stays perfect).",
        "An N-layer grounded stackup (per-layer εr, tanδ); ALL sheet metal coplanar at ONE interface — the top of the stack, or buried under a dielectric cover.",
        "Coaxial probe: a vertical tube from the ground plane to that metal interface, delta-gap driven at its BASE (a real port voltage against ground).",
        "Every internal dielectric interface below the metal is a tube node, so no current element straddles a material change; the probe's segment count is a TARGET for the whole tube and each layer takes at least one element, so a many-layer stack yields more elements than requested.",
        "Classical 1/ρ attachment mode at the junction (the probe position is a mesh vertex); the tube and disc deltas cancel exactly — no junction point charge.",
        "Far field and the power ledger add the tube's own vertical leg and the junction's exact transforms to the sheet currents; the surface-wave ledger sums the horizontal and vertical launches COHERENTLY (they excite the same mode)."
    };

    /// <summary>Solve a probe-fed sheet over a multi-layer grounded stackup. The metal plane is
    /// wherever <paramref name="kernel"/> puts it (its <c>SourceInterface</c>; the stack top when
    /// null), and the tube runs from the ground to that plane.</summary>
    public ProbeFedSolution SolveProbeFed(SurfaceStructure surface, MultiLayerKernelTable kernel,
        ProbeFeed probe, double gapVolts = 1.0)
    {
        if (kernel.IsShielded || kernel.IsUngrounded)
            throw new ArgumentException("A probe needs the vertical kernels, which are written for a grounded stack with an open top; "
                + "between two ground planes or over no ground it is not modelled.", nameof(kernel));
        int vertex = ResolveProbeVertex(surface, probe);
        var set = new MultiLayerVerticalKernelSet(kernel.Stackup, kernel.FrequencyHz);
        double[] tubeNodes = ProbeAssembly.TubeNodes(kernel.Stackup, kernel.SourceInterface, probe);
        return SolveProbeFedCore(surface, new LayeredKernelSplit(kernel),
            new MultiLayerRadialGaKernel(kernel), kernel.FrequencyHz, set, tubeNodes,
            tubeNodes[^1], probe, vertex, gapVolts);
    }
}
