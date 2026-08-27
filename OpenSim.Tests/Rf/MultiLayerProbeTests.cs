using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using static OpenSim.Tests.Rf.MultiLayerProbeFixtures;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage C2 — the probe-fed solve over an N-layer grounded stackup: the IDENTITIES and the
/// structure. (The quasi-static razor is <see cref="MultiLayerProbeCapacitanceTests"/>; the
/// buried metal plane is <see cref="CoveredProbeFedPatchTests"/>.)
///
/// <para>The assembly is shared with the single-slab probe verbatim (one <c>SolveProbeFedCore</c>
/// behind three seams), so these gates measure the MEDIUM: the multi-layer vertical kernels, the
/// interface-pinned tube, and the buried metal plane. That is what makes the N = 1 identity
/// meaningful — it is not two implementations tuned to agree, it is the same arithmetic fed two
/// decompositions of the same physics.</para>
///
/// <para>Measured: N = 1 ≡ the single-slab probe at 7.1e-7 (Zin), 4.8e-7 (tube), 5.1e-7 (edges);
/// split invariance 3.5e-10; εr = 1 ≡ the thin-wire monopole 2.8e-10 basis by basis.</para>
/// </summary>
public class MultiLayerProbeTests
{
    [Fact]
    public void AtOneLayer_ItIsTheSingleSlabProbe()
    {
        // THE identity. A one-layer stackup and the single slab are the same physics decomposed
        // two different ways — five quasi-static images and a closed form against two images and
        // a TLGF assembly — through the SAME assembly, so a disagreement can only come from the
        // medium.
        //
        // Measured Zin 7.1e-7, tube 4.8e-7, edges 5.1e-7. The RWG-only N = 1 identity on this
        // same mesh is 5.1e-8, so the probe path adds about an order — and that extra is a
        // TABULATION difference, not a kernel one: the two media extract different image sets, so
        // the coupling tables' splined remainder carries different content and the spline's own
        // resolution sets the floor (the kernels themselves agree at ~5e-10, gated in C1).
        var surface = Plate(MeshEdge);
        var probe = Probe();
        var solver = new SurfaceMomSolver();

        var slab = solver.SolveProbeFed(surface,
            new LayeredKernelTable(Substrate, Frequency, 0.025), probe);
        var multi = solver.SolveProbeFed(surface,
            new MultiLayerKernelTable(OneLayer, Frequency, 0.025), probe);

        var zs = slab.Surface.InputImpedance;
        double zRel = (zs - multi.Surface.InputImpedance).Magnitude / zs.Magnitude;
        Assert.True(zRel <= 5e-6,
            $"Zin slab {zs} vs multi-layer {multi.Surface.InputImpedance} (rel {zRel:e3})");

        double refTube = slab.TubeCurrents.Max(c => c.Magnitude);
        for (int i = 0; i < slab.TubeCurrents.Length; i++)
        {
            double rel = (slab.TubeCurrents[i] - multi.TubeCurrents[i]).Magnitude / refTube;
            Assert.True(rel <= 5e-6, $"tube current {i}: rel {rel:e3}");
        }
        double refEdge = slab.Surface.EdgeCurrents.Max(c => c.Magnitude);
        for (int i = 0; i < slab.Surface.EdgeCurrents.Length; i++)
        {
            double rel = (slab.Surface.EdgeCurrents[i] - multi.Surface.EdgeCurrents[i]).Magnitude
                         / refEdge;
            Assert.True(rel <= 5e-6, $"edge current {i}: rel {rel:e3}");
        }
    }

    [Fact]
    public void DescribingOneLayerAsTwo_ChangesNothing()
    {
        // Split invariance at the SOLVE level. The fixture is chosen so the tube nodes come out
        // BITWISE identical either way (4 segments over one layer = 2 over each half), because
        // otherwise this would compare two discretizations rather than two descriptions of one
        // stack. Measured 3.5e-10 — the kernels' own split invariance, carried all the way to Zin.
        var surface = Plate(CoarseEdge);
        var probe = Probe(ThinRadius, 4);
        var two = Two(2.2, Thickness / 2, 2.2, Thickness / 2);

        var nodesOne = ProbeAssembly.TubeNodes(OneLayer, null, probe);
        var nodesTwo = ProbeAssembly.TubeNodes(two, null, probe);
        Assert.Equal(nodesOne.Length, nodesTwo.Length);
        for (int i = 0; i < nodesOne.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(nodesOne[i]),
                BitConverter.DoubleToInt64Bits(nodesTwo[i]));

        var solver = new SurfaceMomSolver();
        var zOne = solver.SolveProbeFed(surface,
            new MultiLayerKernelTable(OneLayer, Frequency, 0.025), probe).Surface.InputImpedance;
        var zTwo = solver.SolveProbeFed(surface,
            new MultiLayerKernelTable(two, Frequency, 0.025), probe).Surface.InputImpedance;
        double rel = (zOne - zTwo).Magnitude / zOne.Magnitude;
        Assert.True(rel <= 1e-8, $"one layer {zOne} vs split {zTwo} (rel {rel:e3})");
    }

    [Fact]
    public void AtEpsilonOne_TheTubeIsTheThinWireMonopole()
    {
        // The cross-SOLVER identity, now through a two-layer stack so the interface-pinned tube
        // and the multi-layer kernels are both exercised: at εr = 1 a standalone probe of length
        // L over a PEC ground IS a monopole, and the all-air image set (primary + its POSITIVE
        // PEC image) is exact, so the Sommerfeld remainder must integrate to nothing. Measured
        // 2.8e-10, basis by basis — an order tighter than the single-slab gate's 1e-6, because
        // there the closed-form kernels still carry a smooth track that has to cancel numerically.
        double length = 0.0312, radius = 0.5e-3, f = 2.4e9;
        int segments = 8;
        var air = Two(1.0, length / 2, 1.0, length / 2);
        var probe = new ProbeFeed(0, 0, radius, segments);
        var nodes = ProbeAssembly.TubeNodes(air, null, probe);
        Assert.Equal(segments + 1, nodes.Length);

        var (zProbe, probeCurrents) = ProbeAssembly.SolveProbeOnly(
            new MultiLayerVerticalKernelSet(air, f), nodes, probe);

        var wireNodes = new Vector3D[segments + 1];
        for (int i = 0; i <= segments; i++) wireNodes[i] = new Vector3D(0, 0, length * i / segments);
        var wire = new WireStructure(wireNodes, Enumerable.Repeat(radius, segments).ToArray(),
            isLoop: false, ground: new GroundPlane(0), startGrounded: true);
        var wireSolution = new ThinWireMomSolver().Solve(wire, f, feedBasis: 0);

        double rel = (zProbe - wireSolution.InputImpedance).Magnitude
                     / wireSolution.InputImpedance.Magnitude;
        Assert.True(rel <= 1e-7,
            $"probe Zin {zProbe} vs thin-wire monopole {wireSolution.InputImpedance} (rel {rel:e2})");
        for (int b = 0; b < probeCurrents.Length; b++)
        {
            double currentRel = (probeCurrents[b] - wireSolution.BasisCurrents[b]).Magnitude
                                / wireSolution.BasisCurrents[0].Magnitude;
            Assert.True(currentRel <= 1e-7, $"basis {b}: rel {currentRel:e2}");
        }
    }

    [Fact]
    public void EveryInternalInterfaceIsATubeNode()
    {
        // The structural claim the multi-layer tube rests on: the vertical source strength is
        // −2µ₀/ε, which is two-valued exactly on a material discontinuity (the kernels refuse it
        // by name), so no element may straddle one. Nodding at every interface makes that case
        // unreachable rather than merely unlikely — and the segment count is a TARGET for the
        // whole tube, so a many-layer stack legitimately gets more elements than asked for.
        var stack = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.0, 0.8e-3),
            new LayeredStackup.Layer(2.2, 0.0, 0.2e-3),
            new LayeredStackup.Layer(9.8, 0.0, 0.6e-3)
        });
        var nodes = ProbeAssembly.TubeNodes(stack, null, new ProbeFeed(0, 0, 0.02e-3, 4));

        foreach (double h in stack.InterfaceHeights())
            Assert.Contains(nodes, n => Math.Abs(n - h) <= 1e-15 * stack.TotalThicknessMeters);
        Assert.Equal(0.0, nodes[0]);
        Assert.Equal(stack.TotalThicknessMeters, nodes[^1], 15);
        for (int i = 1; i < nodes.Length; i++) Assert.True(nodes[i] > nodes[i - 1]);

        // The 0.2 mm layer is 1/8 of the stack and rounds to less than one element of a 4-segment
        // request — it still gets one, so 2 + 1 + 2 = 5 elements come back.
        Assert.Equal(6, nodes.Length);

        // A tube that ends on a BURIED interface stops there and nods the interfaces below it.
        var buried = ProbeAssembly.TubeNodes(stack, 1, new ProbeFeed(0, 0, 0.02e-3, 4));
        Assert.Equal(1.0e-3, buried[^1], 15);
        Assert.Contains(buried, n => Math.Abs(n - 0.8e-3) <= 1e-15);
    }

    [Fact]
    public void TheMultiLayerTubeBlockIsBitwiseComplexSymmetric()
    {
        var stack = Two(2.2, 0.8e-3, 9.8, 0.788e-3);
        var probe = new ProbeFeed(0, 0, 0.15e-3, 4);
        var block = ProbeAssembly.ProbeSelfBlock(
            new MultiLayerVerticalKernelSet(stack, 10e9),
            ProbeAssembly.TubeNodes(stack, null, probe), probe, 2 * Math.PI * 10e9,
            includeTopBasis: true);
        for (int i = 0; i < block.Rows; i++)
            for (int j = i + 1; j < block.Columns; j++)
                Assert.Equal(block[i, j], block[j, i]);
    }

    [Fact]
    public void TheTubeBlockIsBitwiseIdenticalAtAnyThreadCount()
    {
        // The smooth track runs one independent Sommerfeld evaluation per (element pair,
        // quadrature point) in ordered slots; the fill consumes them in its historical order.
        var stack = Two(4.4, 0.6e-3, 2.2, 1.0e-3);
        var probe = new ProbeFeed(0, 0, 0.12e-3, 5);
        var set = new MultiLayerVerticalKernelSet(stack, 8e9);
        var nodes = ProbeAssembly.TubeNodes(stack, null, probe);
        double omega = 2 * Math.PI * 8e9;
        var serial = ProbeAssembly.ProbeSelfBlock(set, nodes, probe, omega, true, 1);
        var parallel = ProbeAssembly.ProbeSelfBlock(set, nodes, probe, omega, true);
        for (int i = 0; i < serial.Rows; i++)
            for (int j = 0; j < serial.Columns; j++)
                Assert.Equal(serial[i, j], parallel[i, j]);
    }

    [Fact]
    public void TheTypedFailuresName_TheirCause()
    {
        var stack = Two(2.2, 0.3e-3, 2.2, 1.288e-3);

        // A layer too thin for the bore names the LAYER, not just the stack.
        var thin = Assert.Throws<InvalidOperationException>(() =>
            ProbeAssembly.TubeNodes(stack, null, new ProbeFeed(0, 0, 0.2e-3, 4)));
        Assert.Contains("Layer 0", thin.Message);
        Assert.Contains("2·radius", thin.Message);

        // A metal interface the stackup does not have.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProbeAssembly.TubeNodes(stack, 5, new ProbeFeed(0, 0, 0.05e-3, 4)));

        var solver = new SurfaceMomSolver();
        var table = new MultiLayerKernelTable(OneLayer, Frequency, 0.025);

        // A structure carrying its own ground plane: the stackup already contains one.
        var grounded = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, CoarseEdge, z: Thickness, portFraction: 0,
            ground: new GroundPlane(0), snapVertex: (0.0, ProbeY)).Structure!;
        var doubleGround = Assert.Throws<ArgumentException>(() =>
            solver.SolveProbeFed(grounded, table, Probe()));
        Assert.Contains("Green's function", doubleGround.Message);

        // A probe that is not on a mesh vertex has no anchor for its attachment fan.
        var offVertex = Assert.Throws<ArgumentException>(() =>
            solver.SolveProbeFed(Plate(CoarseEdge), table,
                new ProbeFeed(0.37e-3, ProbeY + 0.21e-3, ProbeRadius, 3)));
        Assert.Contains("not a mesh vertex", offVertex.Message);
    }
}
