using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// Viewport net picking against layer-height parallax: with an angled camera the click
/// ray must be intersected with each layer's own z-plane, not one fixed board plane —
/// projecting to a fixed plane selects whatever copper happens to sit at that plane's
/// hit point instead of the trace under the cursor (the reported bug).
/// </summary>
public class NetPickerTests
{
    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    private static CopperNet Net(int id, int layer, Polygon2 shape) =>
        new(id, new[] { new CopperIsland(0, layer, $"L{layer}", shape) });

    [Fact]
    public void AngledRay_PicksTraceAtItsOwnLayerHeight_NotAtTheFixedPlane()
    {
        // Trace A on L1 at z = 0.7 mm; trace B on L2 at z = 0.3 mm, positioned exactly
        // where the ray crosses z = 0. The old fixed-plane math picked B (its island
        // contains the z-0 hit point) even though the ray misses B at B's real height.
        var a = Net(1, 1, Rect(0, 0, 1e-3, 1e-3));
        var b = Net(2, 2, Rect(2e-3, 0, 3e-3, 1e-3));
        var layerZ = new Dictionary<int, double> { [1] = 0.7e-3, [2] = 0.3e-3 };

        // Through the center of A at L1's height, tilted in +x so that the z = 0
        // crossing lands at x = 0.5 mm + 0.7e-3·dx = 2.5 mm — the center of B.
        var origin = (0.5e-3, 0.5e-3, 0.7e-3);
        var direction = (2e-3 / 0.7e-3, 0.0, -1.0);

        var picked = NetPicker.Pick(new[] { a, b }, _ => true, _ => true, layerZ, origin, direction);
        Assert.Same(a, picked);
    }

    [Fact]
    public void TopDownRay_PicksTheTraceUnderTheCursor()
    {
        var a = Net(1, 1, Rect(0, 0, 1e-3, 1e-3));
        var b = Net(2, 1, Rect(2e-3, 0, 3e-3, 1e-3));
        var layerZ = new Dictionary<int, double> { [1] = 0.7e-3 };

        var picked = NetPicker.Pick(new[] { a, b }, _ => true, _ => true, layerZ,
            (2.5e-3, 0.5e-3, 1.0), (0.0, 0.0, -1.0));
        Assert.Same(b, picked);
    }

    [Fact]
    public void SmallestIsland_WinsOverThePlaneBeneathIt()
    {
        // A small trace above a large plane, both under the cursor at their own heights:
        // the smaller island wins so traces stay pickable over pours.
        var trace = Net(1, 1, Rect(4e-3, 4e-3, 5e-3, 5e-3));
        var plane = Net(2, 2, Rect(0, 0, 10e-3, 10e-3));
        var layerZ = new Dictionary<int, double> { [1] = 0.7e-3, [2] = 0.3e-3 };

        var picked = NetPicker.Pick(new[] { plane, trace }, _ => true, _ => true, layerZ,
            (4.5e-3, 4.5e-3, 1.0), (0.0, 0.0, -1.0));
        Assert.Same(trace, picked);
    }

    [Fact]
    public void DisabledLayerAndGrazingRay_PickNothingWrong()
    {
        var trace = Net(1, 1, Rect(4e-3, 4e-3, 5e-3, 5e-3));
        var plane = Net(2, 2, Rect(0, 0, 10e-3, 10e-3));
        var layerZ = new Dictionary<int, double> { [1] = 0.7e-3, [2] = 0.3e-3 };

        // Hiding L1 makes the plane pickable through the (hidden) trace.
        var picked = NetPicker.Pick(new[] { plane, trace }, _ => true, layer => layer != 1, layerZ,
            (4.5e-3, 4.5e-3, 1.0), (0.0, 0.0, -1.0));
        Assert.Same(plane, picked);

        // A ray parallel to the board has no per-layer intersection at all.
        Assert.Null(NetPicker.Pick(new[] { plane, trace }, _ => true, _ => true, layerZ,
            (4.5e-3, 4.5e-3, 0.5e-3), (1.0, 0.0, 0.0)));
    }

    // ---------------- net-list filtering (IsListed) ----------------

    [Fact]
    public void IsListed_DelistsNetsWhoseOnlyLayersAreDisabled_ButKeepsViaStitchedNets()
    {
        var topOnly = Net(1, 1, Rect(0, 0, 1e-3, 1e-3));
        var bottomOnly = Net(2, 2, Rect(0, 0, 1e-3, 1e-3));
        var spansBoth = new CopperNet(3, new[]
        {
            new CopperIsland(0, 1, "L1", Rect(2e-3, 0, 3e-3, 1e-3)),
            new CopperIsland(1, 2, "L2", Rect(2e-3, 0, 3e-3, 1e-3)),
        });
        var viaStitched = new CopperNet(4, new[]
        {
            new CopperIsland(2, 1, "L1", Rect(4e-3, 0, 5e-3, 1e-3)),
        })
        { StitchingVias = new[] { new ViaBridge(new Via(new Point2(4.5e-3, 0.5e-3), 0.3e-3, Plated: true), new[] { 1, 2 }) } };

        // L1 disabled: only the net living solely on L1 (and without vias) is delisted.
        Func<int, bool> l1Off = layer => layer != 1;
        Assert.False(NetVisibility.IsListed(topOnly, l1Off));
        Assert.True(NetVisibility.IsListed(bottomOnly, l1Off));
        Assert.True(NetVisibility.IsListed(spansBoth, l1Off));
        Assert.True(NetVisibility.IsListed(viaStitched, l1Off));

        // All layers disabled: only the via-stitched net survives in the list.
        Func<int, bool> allOff = _ => false;
        Assert.False(NetVisibility.IsListed(spansBoth, allOff));
        Assert.True(NetVisibility.IsListed(viaStitched, allOff));
    }
}
