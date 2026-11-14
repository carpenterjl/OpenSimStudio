using System.Windows;
using OpenSim.App.Controls.Plotting;

namespace OpenSim.App.Tests.Controls.Plotting;

/// <summary>The plain layout geometry behind the interactive plots (OP-7): frames and panel grids.</summary>
public sealed class PlotLayoutTests
{
    [Fact]
    public void Frame_MapsTheRangeOntoTheArea_WithYUp()
    {
        var frame = PlotFrame.Create(new Rect(50, 20, 400, 200), new PlotRange(-1, 3, 10, 20), equalAspect: false);
        Assert.Equal(new Point(50, 220), frame.ToPixel(-1, 10));
        Assert.Equal(new Point(450, 20), frame.ToPixel(3, 20));
        var d = frame.ToData(new Point(250, 120));
        Assert.Equal(1, d.X, 12);
        Assert.Equal(15, d.Y, 12);
    }

    [Fact]
    public void Frame_EqualAspect_WidensTheShortSideAboutItsCentre()
    {
        // Data 2 × 2 in a 400 × 200 area: y sets the scale (0.01 per pixel), x widens to 4 wide.
        var frame = PlotFrame.Create(new Rect(0, 0, 400, 200), new PlotRange(-1, 1, 5, 7), equalAspect: true);
        Assert.Equal(frame.X.DataPerPixel, frame.Y.DataPerPixel, 15);
        Assert.Equal(-2, frame.Range.X0, 12); Assert.Equal(2, frame.Range.X1, 12);
        Assert.Equal(5, frame.Range.Y0, 12); Assert.Equal(7, frame.Range.Y1, 12);
    }

    [Fact]
    public void Range_PaddingWidensDegenerateSides()
    {
        var padded = new PlotRange(3, 3, -1, 1).Padded(0.1, minimumHalfWidth: 2);
        Assert.Equal(new PlotRange(0.6, 5.4, -1.2, 1.2), padded with
        {
            X0 = Math.Round(padded.X0, 12), X1 = Math.Round(padded.X1, 12),
            Y0 = Math.Round(padded.Y0, 12), Y1 = Math.Round(padded.Y1, 12)
        });
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(4, 0, 2)]
    [InlineData(5, 0, 3)]
    [InlineData(9, 0, 3)]
    [InlineData(3, 5, 3)]
    [InlineData(6, 2, 2)]
    public void PanelGrid_Columns(int count, int requested, int expected) =>
        Assert.Equal(expected, PanelGrid.Columns(count, requested));

    [Fact]
    public void PanelGrid_CellsFillRowByRow()
    {
        var cells = PanelGrid.Cells(5, 3, new Size(600, 400));
        Assert.Equal(new Rect(0, 0, 200, 200), cells[0]);
        Assert.Equal(new Rect(400, 0, 200, 200), cells[2]);
        Assert.Equal(new Rect(200, 200, 200, 200), cells[4]);
    }

    [Fact]
    public void PanelGrid_UniformViewsShareOneScaleAboutEachCentre()
    {
        var views = PanelGrid.UniformViews(new PlotRange?[] { new(0, 2, 0, 1), new(10, 11, 5, 9), null });
        Assert.Equal(new PlotRange(0, 2, -1.5, 2.5), views[0]);
        Assert.Equal(new PlotRange(9.5, 11.5, 5, 9), views[1]);
        Assert.Null(views[2]);

        // A zoom in the first panel, followed by the second about its own centre.
        var follow = PanelGrid.Follow(new PlotRange(0.5, 1.0, 0, 1), new PlotRange(0, 2, 0, 1), new PlotRange(10, 12, 5, 6));
        Assert.Equal(new PlotRange(10.5, 11.0, 5, 6), follow);
    }
}
