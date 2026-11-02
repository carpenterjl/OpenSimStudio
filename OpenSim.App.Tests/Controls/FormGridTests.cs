using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenSim.App.Controls;

namespace OpenSim.App.Tests.Controls;

/// <summary>
/// The form every properties panel lays out with. A panel needs an STA thread, so each test
/// runs on its own. Children here are fixed-width elements: what is tested is the row decision,
/// the natural widths being whatever a real label or box asks for.
/// </summary>
public sealed class FormGridTests
{
    private const double PaneWidth = 302;   // the 352 px properties column less its padding and scrollbar

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

    private static Border Box(double width, double height = 20) => new() { Width = width, Height = height };

    private static Size Lay(FormGrid grid)
    {
        grid.Measure(new Size(PaneWidth, double.PositiveInfinity));
        grid.Arrange(new Rect(new Point(0, 0), new Size(PaneWidth, grid.DesiredSize.Height)));
        return grid.DesiredSize;
    }

    private static Point Where(UIElement child, FormGrid grid) =>
        child.TransformToAncestor(grid).Transform(new Point(0, 0));

    [Fact]
    public void ALabelThatFitsItsHalf_SitsBesideItsValue() => OnSta(() =>
    {
        var label = Box(100);
        var value = new TextBox();
        var grid = new FormGrid { Children = { label, value } };

        var size = Lay(grid);

        Assert.Equal(153, Where(value, grid).X, 0);
        Assert.Equal(0, Where(value, grid).Y, 0);
        Assert.Equal(Math.Max(20, value.DesiredSize.Height), size.Height, 3);
    });

    [Fact]
    public void ALabelTooLongForItsHalf_DropsItsValueToTheNextLine_AtFullWidth() => OnSta(() =>
    {
        var label = Box(200);
        var value = new TextBox();
        var grid = new FormGrid { Children = { label, value } };

        Lay(grid);

        Assert.Equal(0, Where(value, grid).X, 0);
        Assert.Equal(20, Where(value, grid).Y, 0);
        Assert.Equal(PaneWidth, value.ActualWidth, 0);
    });

    [Fact]
    public void ThreeBoxesThatWouldNotShowTheirDigitsInHalf_TakeTheWholeWidth() => OnSta(() =>
    {
        var split = new UniformGrid { Columns = 3, Children = { new TextBox(), new TextBox(), new TextBox() } };
        var grid = new FormGrid { Children = { Box(50), split } };

        Lay(grid);

        Assert.Equal(PaneWidth, split.ActualWidth, 0);
        Assert.Equal(0, Where(split, grid).X, 0);
    });

    [Fact]
    public void TwoBoxes_StillFitBesideAShortLabel() => OnSta(() =>
    {
        var split = new UniformGrid { Columns = 2, Children = { new TextBox(), new TextBox() } };
        var grid = new FormGrid { Children = { Box(50), split } };

        Lay(grid);

        Assert.Equal(149, split.ActualWidth, 0);
        Assert.Equal(153, Where(split, grid).X, 0);
    });

    [Fact]
    public void ACollapsedPair_TakesNoRoom_AndAnUnpairedChildTakesTheRow() => OnSta(() =>
    {
        var hiddenLabel = Box(50);
        hiddenLabel.Visibility = Visibility.Collapsed;
        var hiddenValue = Box(50);
        hiddenValue.Visibility = Visibility.Collapsed;
        var last = new Button();
        var grid = new FormGrid { Children = { hiddenLabel, hiddenValue, Box(50), Box(50), last } };

        var size = Lay(grid);

        Assert.Equal(20, Where(last, grid).Y, 0);
        Assert.Equal(PaneWidth, last.ActualWidth, 0);
        Assert.Equal(20 + last.DesiredSize.Height, size.Height, 3);
    });
}
