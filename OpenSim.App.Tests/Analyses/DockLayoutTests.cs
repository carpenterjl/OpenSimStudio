using OpenSim.App.Analyses;

namespace OpenSim.App.Tests.Analyses;

/// <summary>OP-9: the first-party docking model — dock, tab together, split, float, close — and
/// its saved form.</summary>
public sealed class DockLayoutTests
{
    [Fact]
    public void Docking_TabsIntoTheZonesLastPane_AndSplittingMakesANewOne()
    {
        var layout = new DockLayout<string>();
        layout.Dock("spot", DockZone.Bottom);
        layout.Dock("fan", DockZone.Bottom);                    // a tab beside spot
        layout.Dock("mtf", DockZone.Bottom, newPane: true);     // side by side

        Assert.Equal(2, layout.Bottom.Count);
        Assert.Equal(new[] { "spot", "fan" }, layout.Bottom[0].Items);
        Assert.Equal("fan", layout.Bottom[0].Selected);
        Assert.Equal(new[] { "mtf" }, layout.Bottom[1].Items);
        Assert.Equal(new DockPlacement(DockZone.Bottom, 1, true), layout.Find("mtf"));
        Assert.Equal(new DockPlacement(DockZone.Bottom, 0, false), layout.Find("spot"));
    }

    [Fact]
    public void Moving_LeavesNoEmptyPane_AndKeepsATabOnShow()
    {
        var layout = new DockLayout<string>();
        layout.Dock("a", DockZone.Right);
        layout.Dock("b", DockZone.Right);
        layout.Dock("c", DockZone.Right, newPane: true);

        layout.Dock("b", DockZone.Float);
        Assert.Equal("a", layout.Right[0].Selected);           // b was on show; its neighbour takes over
        Assert.Equal(new[] { "b" }, layout.Floating);

        layout.TabWith("c", layout.Right[0]);
        Assert.Single(layout.Right);                            // c's pane went with it
        Assert.Equal(new[] { "a", "c" }, layout.Right[0].Items);
        Assert.Equal("c", layout.Right[0].Selected);

        layout.Dock("b", DockZone.Right);
        Assert.Empty(layout.Floating);
        Assert.Equal(new[] { "a", "c", "b" }, layout.Right[0].Items);
    }

    [Fact]
    public void Removing_TheLastTab_RemovesThePane()
    {
        var layout = new DockLayout<string>();
        layout.Dock("a", DockZone.Bottom);
        int changes = 0;
        layout.Changed += (_, _) => changes++;

        Assert.True(layout.Remove("a"));
        Assert.False(layout.Remove("a"));
        Assert.Empty(layout.Bottom);
        Assert.Null(layout.Find("a"));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Placements_RestoreToTheSameLayout()
    {
        var layout = new DockLayout<string>();
        layout.Dock("r1", DockZone.Right);
        layout.Dock("r2", DockZone.Right, newPane: true);
        layout.Dock("r3", DockZone.Right);
        layout.Right[1].Selected = "r2";
        layout.Dock("b1", DockZone.Bottom);
        layout.Dock("f1", DockZone.Float);
        var saved = layout.Placements();

        var restored = new DockLayout<string>();
        restored.Restore(saved);

        Assert.Equal(saved, restored.Placements());
        Assert.Equal(new[] { "r1", "r2", "r3", "b1", "f1" }, restored.Items);
    }

    [Fact]
    public void Restore_ToleratesGapsInPaneNumbers_AndAPaneWithNoSelection()
    {
        var layout = new DockLayout<string>();
        layout.Restore(new[]
        {
            ("x", new DockPlacement(DockZone.Bottom, 3, false)),
            ("y", new DockPlacement(DockZone.Bottom, 3, false)),
            ("z", new DockPlacement(DockZone.Bottom, 7, true)),
        });
        Assert.Equal(2, layout.Bottom.Count);
        Assert.Equal("x", layout.Bottom[0].Selected);
        Assert.Equal(new DockPlacement(DockZone.Bottom, 1, true), layout.Find("z"));
    }
}
