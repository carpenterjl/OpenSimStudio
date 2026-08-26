using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage A4 — resolving a board net's copper layer into the antenna stackup. Before this,
/// every board net was solved over ONE homogeneous slab built from two panel scalars, whatever
/// layer it was on: a net on an inner layer of a four-layer board is physically a covered patch
/// (dielectric above the metal as well as below), and the per-gap εr/tanδ the board already
/// carried never reached the solver.
///
/// The gates are COMPOSITION identities rather than new physics — the multi-layer TLGF and the
/// covered-patch solve are gated elsewhere; what is new here is the mapping from board layer
/// orders (top-down) to a ground-up stackup plus a source interface. So an inner-layer net must
/// resolve to EXACTLY the stackup a user would hand-build for the same geometry, and a
/// top-layer net must keep the pre-existing single-slab path bitwise.
/// </summary>
public class BoardAntennaStackupTests
{
    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    /// <summary>A board with <paramref name="copperLayers"/> copper layers, one island per
    /// layer, and an explicit per-gap stackup (distinct materials so a mis-ordered gap list
    /// cannot pass by coincidence).</summary>
    private static PcbBoard LayeredBoard(int copperLayers,
        double[]? epsR = null, double[]? tanD = null, double[]? h = null)
    {
        int gaps = copperLayers - 1;
        epsR ??= Enumerable.Range(0, gaps).Select(i => 3.0 + i).ToArray();
        tanD ??= Enumerable.Range(0, gaps).Select(i => 0.001 * (i + 1)).ToArray();
        h ??= Enumerable.Range(0, gaps).Select(i => 0.2e-3 * (i + 1)).ToArray();

        var islands = new List<CopperIsland>();
        var nets = new List<CopperNet>();
        for (int layer = 1; layer <= copperLayers; layer++)
        {
            var island = new CopperIsland(layer - 1, layer, $"L{layer}",
                Rect(0, 0, 10e-3, 8e-3));
            islands.Add(island);
            nets.Add(new CopperNet(layer, new[] { island }) { Name = $"NET_L{layer}" });
        }
        return new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = islands,
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = nets,
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            Stackup = new PcbStackupSettings
            {
                DielectricGapThicknesses = h,
                DielectricGapPermittivities = epsR,
                DielectricGapLossTangents = tanD,
            },
        };
    }

    [Fact]
    public void TopLayerNet_ResolvesToTheCoplanarTopSlab()
    {
        // L1 of a two-layer board: one gap, metal on top, no cover — the case that has always
        // shipped. IsSingleSlabTop is what keeps the caller on the pre-existing SubstrateStackup
        // path, so this assertion is the bitwise pin's precondition.
        var board = LayeredBoard(2, epsR: new[] { 4.4 }, tanD: new[] { 0.02 }, h: new[] { 1.6e-3 });
        var r = BoardAntennaStackup.Resolve(board, 1, 4.4, 0.02, 1.6e-3);

        Assert.True(r.IsCoplanarTop);
        Assert.True(r.IsSingleSlabTop);
        Assert.Equal(0, r.SourceInterface);
        var only = Assert.Single(r.Stackup.Layers);
        Assert.Equal(4.4, only.RelativePermittivity, 12);
        Assert.Equal(0.02, only.LossTangent, 12);
        Assert.Equal(1.6e-3, only.ThicknessMeters, 15);
    }

    [Fact]
    public void TopLayerNetOfAMultiGapBoard_StaysCoplanarAtTheSlabTop()
    {
        // Four copper layers, net on L1: three gaps stack up beneath it and NOTHING lies above,
        // so it is the F2a coplanar-at-top case on a genuine multi-layer stack. The gap order
        // must be ground-up — the gap adjacent to the bottom plane first.
        var board = LayeredBoard(4);
        var r = BoardAntennaStackup.Resolve(board, 1, 4.4, 0.02, 1.6e-3);

        Assert.True(r.IsCoplanarTop);
        Assert.False(r.IsSingleSlabTop);
        Assert.Equal(2, r.SourceInterface);            // the top interface of a 3-layer stack
        Assert.Equal(3, r.Stackup.Layers.Count);
        // Ground-up: gap 2 (L3–L4) sits on the ground, then gap 1 (L2–L3), then gap 0 (L1–L2).
        Assert.Equal(5.0, r.Stackup.Layers[0].RelativePermittivity, 12);   // 3.0 + 2
        Assert.Equal(4.0, r.Stackup.Layers[1].RelativePermittivity, 12);
        Assert.Equal(3.0, r.Stackup.Layers[2].RelativePermittivity, 12);
        Assert.Equal(0.6e-3, r.Stackup.Layers[0].ThicknessMeters, 15);     // 0.2e-3 * 3
        Assert.Equal(0.4e-3, r.Stackup.Layers[1].ThicknessMeters, 15);
        Assert.Equal(0.2e-3, r.Stackup.Layers[2].ThicknessMeters, 15);
    }

    [Fact]
    public void InnerLayerNet_ResolvesToTheHandBuiltCoveredPatchStackup()
    {
        // THE composition identity. A net on L2 of a three-layer board has one gap below it and
        // one above — exactly a covered patch. The resolved stackup must be the one a user would
        // hand-build for the same geometry, member for member, and the source interface must
        // name the buried metal.
        var board = LayeredBoard(3,
            epsR: new[] { 2.2, 4.4 },        // gap0 = L1–L2 (the cover), gap1 = L2–L3 (substrate)
            tanD: new[] { 0.001, 0.02 },
            h: new[] { 0.5e-3, 0.8e-3 });
        var r = BoardAntennaStackup.Resolve(board, 2, 9.9, 9.9, 9.9e-3);

        Assert.False(r.IsCoplanarTop);
        Assert.Equal(0, r.SourceInterface);           // metal on the first interface up
        var expected = LayeredStackup.CoveredPatch(
            epsRSubstrate: 4.4, tanDSubstrate: 0.02, hSub: 0.8e-3,
            epsRCover: 2.2, tanDCover: 0.001, hCover: 0.5e-3);
        Assert.Equal(expected.Layers.Count, r.Stackup.Layers.Count);
        for (int i = 0; i < expected.Layers.Count; i++)
            Assert.Equal(expected.Layers[i], r.Stackup.Layers[i]);
    }

    [Fact]
    public void InnerLayerNet_SolvesBitwiseTheHandBuiltCoveredPatch()
    {
        // The identity carried all the way through a solve: the resolved stackup and the
        // hand-built one are the same object graph, so the same mesh must give the same Zin bit
        // for bit. This is what makes the resolver a pure re-description of the board's data
        // rather than a second physical model.
        var board = LayeredBoard(3,
            epsR: new[] { 2.2, 4.4 }, tanD: new[] { 0.0, 0.0 }, h: new[] { 0.5e-3, 0.8e-3 });
        var r = BoardAntennaStackup.Resolve(board, 2, 1.0, 0.0, 1e-3);
        var hand = LayeredStackup.CoveredPatch(4.4, 0.0, 0.8e-3, 2.2, 0.0, 0.5e-3);

        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            1.186e-2, 0.906e-2, 1.4e-3, z: 0.8e-3, portFraction: 0);
        System.Numerics.Complex Zin(LayeredStackup s, int m) =>
            new SurfaceMomSolver().Solve(grid.Structure!,
                new MultiLayerKernelTable(s, 10e9, 0.03, sourceInterface: m),
                grid.Port!).InputImpedance;

        Assert.Equal(Zin(hand, LayeredStackup.CoveredPatchMetalInterface),
                     Zin(r.Stackup, r.SourceInterface));
    }

    [Fact]
    public void GapMaterialsFallBackToTheSuppliedDefaults_WhenTheBoardCarriesNoStackup()
    {
        // A Gerber set has no stackup at all. The defaults must fill every gap and the total
        // thickness must split evenly across them — the same rule the SI extractor uses.
        var board = LayeredBoard(3);
        var bare = new PcbBoard
        {
            Outline = board.Outline, Islands = board.Islands, Pads = board.Pads,
            Vias = board.Vias, Nets = board.Nets, Layers = board.Layers,
            Warnings = board.Warnings, Stackup = null,
        };
        var r = BoardAntennaStackup.Resolve(bare, 1, 4.4, 0.02, 1.6e-3);

        Assert.Equal(2, r.Stackup.Layers.Count);
        foreach (var l in r.Stackup.Layers)
        {
            Assert.Equal(4.4, l.RelativePermittivity, 12);
            Assert.Equal(0.02, l.LossTangent, 12);
            Assert.Equal(0.8e-3, l.ThicknessMeters, 15);   // 1.6 mm split across two gaps
        }
        Assert.Contains(r.Assumptions, a => a.Contains("defaults"));
    }

    [Fact]
    public void TheBottomLayerIsTheGroundPlane_AndIsStatedNotAssumed()
    {
        var r = BoardAntennaStackup.Resolve(LayeredBoard(4), 2, 4.4, 0.02, 1.6e-3);
        Assert.Contains(r.Assumptions, a => a.Contains("L4") && a.Contains("ground plane"));
        Assert.Contains(r.Assumptions, a => a.Contains("covered patch"));
    }

    [Fact]
    public void ANetOnTheGroundLayer_IsATypedFailure()
    {
        // The radiator cannot be its own reference. Solving it over a slab anyway would return
        // a number, which is exactly the failure mode worth refusing.
        var e = Assert.Throws<ArgumentException>(
            () => BoardAntennaStackup.Resolve(LayeredBoard(3), 3, 4.4, 0.02, 1.6e-3));
        Assert.Contains("ground plane", e.Message);
        Assert.Contains("L3", e.Message);
    }

    [Fact]
    public void ASingleCopperLayerBoard_IsATypedFailure()
    {
        var e = Assert.Throws<ArgumentException>(
            () => BoardAntennaStackup.Resolve(LayeredBoard(1), 1, 4.4, 0.02, 1.6e-3));
        Assert.Contains("reference plane", e.Message);
    }

    [Fact]
    public void TryResolve_ReportsTheReasonWithoutThrowing()
    {
        Assert.False(BoardAntennaStackup.TryResolve(
            LayeredBoard(3), 3, 4.4, 0.02, 1.6e-3, out var resolved, out var failure));
        Assert.Null(resolved);
        Assert.Contains("ground plane", failure);
        Assert.DoesNotContain("Parameter", failure);
    }

    [Fact]
    public void EveryInnerLayer_LandsOnItsOwnInterface()
    {
        // The layer-order reversal, swept: on an N-layer board a net on Lk sits at interface
        // N−1−k, so L1 is the top interface and the layer just above the ground is interface 0.
        // An off-by-one here would silently solve the right stackup with the metal in the wrong
        // plane — a wrong answer that still converges.
        const int layers = 5;
        var board = LayeredBoard(layers);
        for (int k = 1; k < layers; k++)
        {
            var r = BoardAntennaStackup.Resolve(board, k, 4.4, 0.02, 1.6e-3);
            Assert.Equal(layers - 1 - k, r.SourceInterface);
            Assert.Equal(layers - 1, r.Stackup.Layers.Count);
            Assert.Equal(k == 1, r.IsCoplanarTop);
        }
    }
}
