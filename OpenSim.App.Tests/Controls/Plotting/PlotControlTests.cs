using System.Windows;
using System.Windows.Media;
using OpenSim.App.Controls;
using OpenSim.App.Controls.Plotting;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;

namespace OpenSim.App.Tests.Controls.Plotting;

/// <summary>
/// The interactive plot controls (OP-7), laid out headless on an STA thread: the cursor readout
/// finds the plotted point under a pixel, zoom changes the view, and every plot's CSV export reads
/// back as the numbers it plotted.
/// </summary>
public sealed class PlotControlTests
{
    private static void OnSta(Action test)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static T Lay<T>(T element, double width = 480, double height = 360) where T : FrameworkElement
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        return element;
    }

    private static readonly Point2[] Spot =
        { new(0, 0), new(0.012, -0.004), new(-0.008, 0.01), new(0.003, 0.007) };

    private static ScatterPlot SpotPlot() => Lay(new ScatterPlot
    {
        Series = new[] { new ScatterSeries("0.55 µm", Spot, Colors.SteelBlue, PlotMarker.Plus) },
        Circles = new[] { new PlotCircle(0, 0, 0.02, "Airy", Colors.Black) },
        Unit = "mm",
        XLabel = "x",
        YLabel = "y"
    });

    [Fact]
    public void Scatter_ReadoutFindsThePointUnderTheCursor() => OnSta(() =>
    {
        var plot = SpotPlot();
        var frame = plot.CurrentFrame()!;
        var pixel = frame.ToPixel(Spot[2]);

        var readout = plot.ReadoutAt(new Point(pixel.X + 3, pixel.Y - 2))!;
        Assert.Equal(pixel, readout.Anchor);
        Assert.Contains("point 3", readout.Text);
        Assert.Contains("x = -0.008 mm", readout.Text);
        Assert.Contains("y = 0.01 mm", readout.Text);

        // Away from every point the readout is the cursor position, with no marker.
        var corner = plot.ReadoutAt(new Point(frame.Area.Left + 2, frame.Area.Top + 2))!;
        Assert.False(corner.HasAnchor);
        Assert.Contains("x = ", corner.Text);
        Assert.Null(plot.ReadoutAt(new Point(1, 1)));   // outside the data area
    });

    [Fact]
    public void Scatter_IsAtEqualScale_AndCoversTheAiryCircle() => OnSta(() =>
    {
        var frame = SpotPlot().CurrentFrame()!;
        Assert.Equal(frame.X.DataPerPixel, frame.Y.DataPerPixel, 15);
        Assert.True(frame.Range.X0 < -0.02 && frame.Range.X1 > 0.02 && frame.Range.Y0 < -0.02 && frame.Range.Y1 > 0.02);
    });

    [Fact]
    public void Scatter_APerfectSpot_StillGetsAnAxis() => OnSta(() =>
    {
        var plot = Lay(new ScatterPlot { Series = new[] { new ScatterSeries("", new[] { new Point2(3, 3), new Point2(3, 3) }, Colors.Red) } });
        var range = plot.DataRange!.Value;
        Assert.True(range.Width > 0 && range.Height > 0);
        Assert.InRange(3.0, range.X0, range.X1);
    });

    [Fact]
    public void Scatter_CsvRoundTripsThePlottedPoints() => OnSta(() =>
    {
        var back = PlotCsv.ReadPoints(SpotPlot().ExportCsv());
        Assert.Equal(Spot.Length, back.Count);
        for (int i = 0; i < Spot.Length; i++)
            Assert.Equal(new CsvPoint("0.55 µm", Spot[i].X, Spot[i].Y), back[i]);
    });

    [Fact]
    public void Zoom_HalvesTheViewAboutTheCursor_AndResetRestoresIt() => OnSta(() =>
    {
        var plot = SpotPlot();
        var before = plot.CurrentFrame()!;
        var cursor = new Point(before.Area.Left + 100, before.Area.Top + 80);
        var under = before.ToData(cursor);
        int changes = 0;
        plot.ViewChanged += (_, _) => changes++;

        plot.ZoomAt(cursor, 2);
        var after = plot.CurrentFrame()!;
        Assert.Equal(before.Range.Width / 2, after.Range.Width, 12);
        Assert.Equal(under.X, after.ToData(cursor).X, 12);
        Assert.Equal(under.Y, after.ToData(cursor).Y, 12);

        plot.ZoomToPixels(new Rect(before.Area.Left, before.Area.Top, 40, 40));
        Assert.NotNull(plot.View);
        plot.ResetZoom();
        Assert.Null(plot.View);
        Assert.Equal(before.Range, plot.CurrentFrame()!.Range);
        Assert.Equal(3, changes);
    });

    [Fact]
    public void Line_ReadoutAndLogAxes() => OnSta(() =>
    {
        double[] f = { 1, 10, 100, 1000 }, mtf = { 0.99, 0.9, 0.5, 0.01 };
        var plot = Lay(new LinePlot
        {
            Series = new[] { new PlotSeries("T", f, mtf, Colors.Blue) },
            LogXAxis = true,
            XLabel = "frequency",
            YLabel = "MTF"
        });
        var frame = plot.CurrentFrame()!;
        Assert.True(frame.X.Log);
        // Decades are evenly spaced on screen.
        Assert.Equal(frame.X.ToPixel(10) - frame.X.ToPixel(1), frame.X.ToPixel(1000) - frame.X.ToPixel(100), 9);
        var readout = plot.ReadoutAt(frame.ToPixel(100, 0.5))!;
        Assert.Contains("frequency = 100", readout.Text);
        Assert.Contains("MTF = 0.5", readout.Text);

        var back = PlotCsv.ReadPoints(plot.ExportCsv());
        Assert.Equal(mtf, back.Select(p => p.Y));
    });

    [Fact]
    public void Polar_CosineLobeReadsBackItsAngleAndRadius() => OnSta(() =>
    {
        var theta = Enumerable.Range(0, 181).Select(i => -Math.PI / 2 + i * Math.PI / 180).ToArray();
        var r = theta.Select(Math.Cos).ToArray();
        var plot = Lay(new PolarPlot { Series = new[] { new PolarSeries("cos", theta, r, Colors.Green) } }, 400, 400);
        Assert.Equal(1.0, plot.Map.RMax, 12);

        var frame = plot.CurrentFrame()!;
        // θ = 60° on the lobe: r = cos 60° = 0.5, at (0.25, 0.433) in the unit disc.
        double t = Math.PI / 3;
        var readout = plot.ReadoutAt(frame.ToPixel(plot.Map.ToCartesian(t, Math.Cos(t))))!;
        Assert.True(readout.HasAnchor);
        Assert.Contains("θ = 60°", readout.Text);
        Assert.Contains("r = 0.5", readout.Text);

        var back = PlotCsv.ReadPoints(plot.ExportCsv());
        Assert.Equal(181, back.Count);
        for (int i = 0; i < back.Count; i++)
        {
            Assert.Equal(theta[i], back[i].X * Math.PI / 180, 14);
            Assert.Equal(r[i], back[i].Y);
        }
    });

    private static GridField Gaussian(int n = 81) =>
        GridField.FromFunction((x, y) => Math.Exp(-(x * x + y * y) / 2), n, n, -4, 4, -4, 4);

    [Fact]
    public void Contour_LinesExportTheGaussianRadii() => OnSta(() =>
    {
        var plot = Lay(new ContourPlot { Field = Gaussian(), Levels = new[] { 0.5 } });
        var (levels, edges, _, bands) = plot.Contouring!.Value;
        Assert.Equal(new[] { 0.5 }, levels);
        Assert.Equal(3, edges.Count);
        Assert.Equal(2, bands.Count);

        // h = 0.1: the edge-interpolation bound of GridContourTests, (2h²/8)/(r0·L).
        double r0 = Math.Sqrt(-2 * Math.Log(0.5)), bound = (2 * 0.01 / 8) / (r0 * 0.5);
        var (header, rows) = PlotCsv.ReadTable(plot.Tables[1].Csv());
        Assert.Equal(new[] { "level", "x1", "y1", "x2", "y2" }, header);
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            double x1 = PlotCsv.Parse(row[1], 0), y1 = PlotCsv.Parse(row[2], 0);
            Assert.InRange(Math.Sqrt(x1 * x1 + y1 * y1), r0 - bound, r0 + bound);
        }
    });

    [Fact]
    public void Contour_ReadoutGivesTheValueAndItsBand() => OnSta(() =>
    {
        var field = Gaussian();
        var plot = Lay(new ContourPlot { Field = field, LevelCount = 4 });
        var frame = plot.CurrentFrame()!;
        var pixel = frame.ToPixel(0.7, -0.3);
        var at = frame.ToData(pixel);
        var readout = plot.ReadoutAt(pixel)!;
        Assert.Contains($"value = {AxisTicks.Readout(field.Interpolate(at.X, at.Y))}", readout.Text);
        Assert.Contains("band 0.6 to 0.8", readout.Text);   // exp(-0.29) = 0.748

        var grid = PlotCsv.ReadGrid(plot.ExportCsv());
        Assert.Equal(field.Values[40, 40], grid.Values[40, 40]);
    });

    [Fact]
    public void Image_CrossSectionDefaultsMovesAndExports() => OnSta(() =>
    {
        var field = GridField.FromFunction((x, y) => x * x + 2 * y, 21, 31, -3, 3, -1, 1);
        var plot = Lay(new ImagePlot { Field = field }, 480, 480);
        var (start, end) = plot.Section!.Value;
        Assert.Equal(new Point2(-3, 0), start);   // the middle row is y = 0
        Assert.Equal(new Point2(3, 0), end);

        int moved = 0;
        plot.CrossSectionChanged += (_, _) => moved++;
        plot.MoveSection(new Point2(0, -1), new Point2(0, 1));
        Assert.True(moved >= 1);
        Assert.Equal(new Point(0, -1), plot.CrossSectionStart);

        // Along x = 0 the field is 2y; bilinear interpolation is exact for it.
        var (_, rows) = PlotCsv.ReadTable(plot.Tables[1].Csv());
        Assert.Equal(plot.Profile().Count, rows.Count);
        foreach (var row in rows)
            Assert.Equal(2 * PlotCsv.Parse(row[2], 0), PlotCsv.Parse(row[3], 0), 12);
        Assert.Equal(2.0, PlotCsv.Parse(rows[^1][0], 0), 12);   // the section is 2 long
    });

    [Fact]
    public void Image_ReadoutGivesTheSampleUnderTheCursor() => OnSta(() =>
    {
        var field = GridField.FromFunction((x, y) => 10 * x + y, 5, 7, 0, 6, 0, 4);
        var plot = Lay(new ImagePlot { Field = field, ShowCrossSection = false });
        var frame = plot.CurrentFrame()!;
        var pixel = frame.ToPixel(field.XAt(4) + 0.3, field.YAt(1) - 0.2);
        var readout = plot.ReadoutAt(pixel)!;
        Assert.Equal(frame.ToPixel(field.XAt(4), field.YAt(1)), readout.Anchor);
        Assert.Contains("value = 41", readout.Text);
        Assert.Contains("sample [1, 4]", readout.Text);
    });

    [Fact]
    public void MultiPanel_LinkedPanelsShareOneScale_AndExportEveryPanel() => OnSta(() =>
    {
        ScatterPlot Field(string title, double cy, double spread) => new()
        {
            Title = title,
            Series = new[] { new ScatterSeries("", new[] { new Point2(-spread, cy - spread), new Point2(spread, cy + spread) }, Colors.Blue) }
        };
        var a = Field("on axis", 0, 0.01);
        var b = Field("full field", 5, 0.03);
        var figure = Lay(new MultiPanelPlot { LinkViews = true, Children = { a, b } }, 800, 400);

        Assert.Equal(a.View!.Value.Width, b.View!.Value.Width, 12);
        Assert.Equal(a.View!.Value.Height, b.View!.Value.Height, 12);
        Assert.Equal(5, 0.5 * (b.View!.Value.Y0 + b.View!.Value.Y1), 9);   // each about its own centre

        a.ZoomAt(new Point(200, 200), 2);
        Assert.Equal(a.View!.Value.Width, b.View!.Value.Width, 12);

        var sections = PlotCsv.SplitSections(figure.ExportCsv());
        Assert.Equal(new[] { "on axis: points", "full field: points" }, sections.Select(s => s.Name));
        Assert.Equal(2, PlotCsv.ReadPoints(sections[1].Csv).Count);
    });

    [Fact]
    public void ExistingStaticPlots_StillRender() => OnSta(() =>
    {
        // OP-7 adds controls beside CurvePlot and FieldMapPlot without changing them.
        var curve = CurvePlot.Render(new[] { new PlotSeries("a", new[] { 1.0, 2 }, new[] { 3.0, 4 }, Colors.Red) }, false, false, "x", "y");
        Assert.Equal(560, curve.Width, 0);
        var map = FieldMapPlot.Render(new double[,] { { 1, 2 }, { 3, 4 } }, 0, 1, 0, 1, Array.Empty<MapOutline>(), false, 3, "x", "y", "V");
        Assert.True(map.Height > 0);
    });
}
