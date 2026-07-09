using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Ipc2581;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// Regression tests against the real 2-layer KiCad export at the repo root. This board
/// found two real bugs the synthetic fixtures missed: per-face polygon cleaning
/// desynchronising shared arrangement boundaries, and boolean snap-rounding mismatches
/// (nanometre crossings / on-edge vertices at grazing tangencies) that only a conformal
/// weld + imprint repairs. Skipped silently when the file isn't in the checkout.
/// </summary>
public class SpiderBoardTests
{
    private static string? FindBoardFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "spider_connector_v1_1_pcb.cvg");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static NetMeshOptions Options(PcbBoard board)
    {
        var layerT = new Dictionary<int, double>();
        var gaps = new Dictionary<int, double>();
        if (board.Stackup is not null)
        {
            for (int i = 0; i < board.Stackup.CopperLayerThicknesses.Count; i++)
                layerT[i + 1] = board.Stackup.CopperLayerThicknesses[i];
            for (int i = 0; i < board.Stackup.DielectricGapThicknesses.Count; i++)
                gaps[i + 1] = board.Stackup.DielectricGapThicknesses[i];
        }
        return new NetMeshOptions { LayerThickness = layerT, DielectricGapThickness = gaps };
    }

    [Fact]
    public void SignalNet_MeshesMultiLayer_WithBarrel()
    {
        string? path = FindBoardFile();
        if (path is null) return;
        var board = new Ipc2581Reader().Read(path);

        // A small via-stitched signal net (SPI_SCK). Must mesh both layers + the barrel,
        // never fall back to a single layer.
        var net = board.Nets.First(n => n.Name == "SPI_SCK");
        var result = new NetMesher().MeshNet(net, board.Pads, Options(board));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("could not be recovered"));
        Assert.True(result.Body.Mesh!.ElementCount > 0);
        Assert.Contains(result.Warnings, w => w.Contains("annular via barrel"));
    }

    [Fact]
    public void ViaStitchedGroundPour_MeshesMultiLayer_QuicklyEnoughToUse()
    {
        string? path = FindBoardFile();
        if (path is null) return;
        var board = new Ipc2581Reader().Read(path);

        // The GND pour: 8 islands, 29 stitching vias, ~2500 vertices — the worst meshable
        // net on the board. This is the case whose arrangement carries grazing-tangency
        // snap-rounding artifacts; it must still mesh conformally on both layers.
        var net = board.Nets.First(n => n.Name == "GND");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new NetMesher().MeshNet(net, board.Pads, Options(board));
        sw.Stop();

        Assert.DoesNotContain(result.Warnings, w => w.Contains("could not be recovered"));
        Assert.Contains(result.Warnings, w => w.Contains("29 annular via barrel"));

        // The mesh must span both copper layers (top of L1 down to bottom of L2).
        var mesh = result.Body.Mesh!;
        double zMin = double.MaxValue, zMax = double.MinValue;
        foreach (var n in mesh.Nodes)
        {
            zMin = Math.Min(zMin, n.Z);
            zMax = Math.Max(zMax, n.Z);
        }
        double expectedSpan = board.Stackup!.CopperLayerThicknesses.Sum()
                            + board.Stackup.DielectricGapThicknesses.Sum();
        Assert.Equal(expectedSpan, zMax - zMin, expectedSpan * 0.01);

        // Perf guard, generous: this took 476 s before the spatial indexes (quadratic
        // scans in the CDT and mesher); anything near that again is a regression.
        Assert.True(sw.Elapsed.TotalSeconds < 60,
            $"GND pour took {sw.Elapsed.TotalSeconds:f0} s — the meshing hot paths have regressed.");
    }

    [Fact]
    public void AllMeshableMultiLayerNets_RecoverConformally()
    {
        string? path = FindBoardFile();
        if (path is null) return;
        var board = new Ipc2581Reader().Read(path);

        foreach (var net in board.Nets.Where(n => !n.IsSingleLayer))
        {
            NetMesher.Result result;
            try { result = new NetMesher().MeshNet(net, board.Pads, Options(board)); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("cap"))
            { continue; }                                        // plane/pour over the guard
            Assert.DoesNotContain(result.Warnings, w => w.Contains("could not be recovered"));
        }
    }
}

/// <summary>Synthetic coverage for the conformal arrangement repair.</summary>
public class ArrangementWeldTests
{
    [Fact]
    public void NanometreMismatch_BetweenSharedBoundaries_IsWelded()
    {
        // Two "copies" of a shared boundary that disagree by 2 nm (a snap-rounding unit
        // more than the weld's own grid) — the classic Intersect/Difference mismatch.
        var left = new Polygon2(new[]
        {
            new Point2(0, 0), new Point2(1e-3, 0), new Point2(1e-3, 1e-3), new Point2(0, 1e-3)
        });
        var right = new Polygon2(new[]
        {
            new Point2(1e-3 + 2e-9, 0), new Point2(2e-3, 0),
            new Point2(2e-3, 1e-3), new Point2(1e-3 - 2e-9, 1e-3)
        });

        var welded = ArrangementWeld.Apply(new[] { left, right }, 50e-9);
        Assert.Equal(2, welded.Count);
        // The two copies of each shared corner must now be one exact point.
        Assert.Contains(welded[1].Outer, p => welded[0].Outer.Any(q => q.X == p.X && q.Y == p.Y));
    }

    [Fact]
    public void VertexOnForeignEdge_IsImprintedAsTJunction()
    {
        // A vertex of the right face sits on the interior of the left face's edge —
        // a T-junction the CDT can only constrain if the edge is split there.
        var left = new Polygon2(new[]
        {
            new Point2(0, 0), new Point2(1e-3, 0), new Point2(1e-3, 1e-3), new Point2(0, 1e-3)
        });
        var right = new Polygon2(new[]
        {
            new Point2(1e-3 + 1e-9, 0.5e-3), new Point2(2e-3, 0), new Point2(2e-3, 1e-3)
        });

        var welded = ArrangementWeld.Apply(new[] { left, right }, 50e-9);
        // The left face's right edge must now contain the imprinted mid vertex.
        Assert.Contains(welded[0].Outer, p => Math.Abs(p.Y - 0.5e-3) < 1e-9);
        Assert.Equal(5, welded[0].Outer.Count);
    }
}
